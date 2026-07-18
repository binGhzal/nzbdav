using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Background service that migrates usenet file data
/// from the sqlite database to the blob-store.
/// </summary>
public class UsenetFileToBlobstoreMigrationService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SuccessThrottleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(5);

    // This namespace is a durable part of the on-disk blob identity scheme. It must never change,
    // because the same legacy row must resolve to the same blob after an ambiguous commit or restart.
    private static readonly Guid MigrationBlobNamespace = new("c69da24f-e328-4aa5-84db-ac1edccf1ab0");

    private readonly WebsocketManager _websocketManager;
    private readonly Func<DavDatabaseContext> _createDbContext;

    public UsenetFileToBlobstoreMigrationService(WebsocketManager websocketManager)
        : this(websocketManager, DavDatabaseContextRuntimeFactory.Create)
    {
    }

    internal UsenetFileToBlobstoreMigrationService(
        WebsocketManager websocketManager,
        Func<DavDatabaseContext> createDbContext)
    {
        _websocketManager = websocketManager;
        _createDbContext = createDbContext;
    }

    internal enum LegacyFileKind : byte
    {
        Nzb = 1,
        Rar = 2,
        Multipart = 3
    }

    internal enum MigrationStepResult
    {
        NoFile,
        Migrated
    }

    private enum CommitReconciliation
    {
        Committed,
        Uncommitted,
        Indeterminate
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        Report("Determining number of files to migrate...");
        var initialRemaining = await GetTotalCountLeft(stoppingToken);
        var totalRemaining = initialRemaining;
        ReportProgress(totalRemaining, initialRemaining);
        totalRemaining = await MigrateNzbFiles(totalRemaining, initialRemaining, stoppingToken);
        totalRemaining = await MigrateRarFiles(totalRemaining, initialRemaining, stoppingToken);
        totalRemaining = await MigrateMultipartFiles(totalRemaining, initialRemaining, stoppingToken);
        var complete = initialRemaining - totalRemaining;
        Report(complete == 0
            ? "Done! Nothing to migrate."
            : $"Done! Migrated {complete}/{initialRemaining} file(s) to the blob-store.");
    }

    private async Task<int> GetTotalCountLeft(CancellationToken ct)
    {
        await using var dbContext = _createDbContext();
        return await dbContext.NzbFiles.CountAsync(ct) +
               await dbContext.RarFiles.CountAsync(ct) +
               await dbContext.MultipartFiles.CountAsync(ct);
    }

    private Task<int> MigrateNzbFiles(int totalRemaining, int initialRemaining, CancellationToken ct)
    {
        return MigrateUsenetFiles(MigrateNzbFileOnceAsync, totalRemaining, initialRemaining, ct);
    }

    private Task<int> MigrateRarFiles(int totalRemaining, int initialRemaining, CancellationToken ct)
    {
        return MigrateUsenetFiles(MigrateRarFileOnceAsync, totalRemaining, initialRemaining, ct);
    }

    private Task<int> MigrateMultipartFiles(int totalRemaining, int initialRemaining, CancellationToken ct)
    {
        return MigrateUsenetFiles(MigrateMultipartFileOnceAsync, totalRemaining, initialRemaining, ct);
    }

    internal Task<MigrationStepResult> MigrateNzbFileOnceAsync(CancellationToken ct)
    {
        return MigrateUsenetFileOnceAsync<DavNzbFile>(
            (dbContext, token) => dbContext.NzbFiles.FirstOrDefaultAsync(token),
            file => file.Id,
            (file, id) => file.Id = id,
            (dbContext, id) => dbContext.NzbFiles.Remove(new DavNzbFile { Id = id }),
            (dbContext, id, token) => dbContext.NzbFiles.AnyAsync(x => x.Id == id, token),
            LegacyFileKind.Nzb,
            ct);
    }

    internal Task<MigrationStepResult> MigrateRarFileOnceAsync(CancellationToken ct)
    {
        return MigrateUsenetFileOnceAsync<DavRarFile>(
            (dbContext, token) => dbContext.RarFiles.FirstOrDefaultAsync(token),
            file => file.Id,
            (file, id) => file.Id = id,
            (dbContext, id) => dbContext.RarFiles.Remove(new DavRarFile { Id = id }),
            (dbContext, id, token) => dbContext.RarFiles.AnyAsync(x => x.Id == id, token),
            LegacyFileKind.Rar,
            ct);
    }

    internal Task<MigrationStepResult> MigrateMultipartFileOnceAsync(CancellationToken ct)
    {
        return MigrateUsenetFileOnceAsync<DavMultipartFile>(
            (dbContext, token) => dbContext.MultipartFiles.FirstOrDefaultAsync(token),
            file => file.Id,
            (file, id) => file.Id = id,
            (dbContext, id) => dbContext.MultipartFiles.Remove(new DavMultipartFile { Id = id }),
            (dbContext, id, token) => dbContext.MultipartFiles.AnyAsync(x => x.Id == id, token),
            LegacyFileKind.Multipart,
            ct);
    }

    private async Task<int> MigrateUsenetFiles(
        Func<CancellationToken, Task<MigrationStepResult>> migrateOnce,
        int totalRemaining,
        int initialRemaining,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await migrateOnce(ct).ConfigureAwait(false);
                if (result == MigrationStepResult.NoFile) return totalRemaining;

                totalRemaining--;
                ReportProgress(totalRemaining, initialRemaining);
                await Task.Delay(SuccessThrottleDelay, ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Error(e, $"Error migrating usenet-file to blob-store: {e.Message}");
                await Task.Delay(ErrorDelay, ct).ConfigureAwait(false);
            }
        }

        return totalRemaining;
    }

    private async Task<MigrationStepResult> MigrateUsenetFileOnceAsync<T>(
        Func<DavDatabaseContext, CancellationToken, Task<T?>> getFileToMigrate,
        Func<T, Guid> getFileToMigrateId,
        Action<T, Guid> setFileToMigrateId,
        Action<DavDatabaseContext, Guid> removeFileToMigrateFromDb,
        Func<DavDatabaseContext, Guid, CancellationToken, Task<bool>> legacyRowExists,
        LegacyFileKind fileKind,
        CancellationToken ct)
    {
        await using var dbContext = _createDbContext();
        var fileToMigrate = await getFileToMigrate(dbContext, ct).ConfigureAwait(false);
        if (fileToMigrate == null) return MigrationStepResult.NoFile;

        var sourceId = getFileToMigrateId(fileToMigrate);
        var davItem = await GetDavItem(sourceId, dbContext, ct).ConfigureAwait(false);
        dbContext.Entry(fileToMigrate).State = EntityState.Detached;

        if (davItem.FileBlobId is { } existingBlobId
            && BlobStore.TryStatBlob(existingBlobId).Status == BlobStore.BlobReadStatus.Found)
        {
            removeFileToMigrateFromDb(dbContext, sourceId);
            return await SaveAndReconcileAsync(
                    dbContext,
                    sourceId,
                    existingBlobId,
                    legacyRowExists,
                    deleteTargetWhenDefinitelyUncommitted: false,
                    ct)
                .ConfigureAwait(false);
        }

        var targetBlobId = CreateTargetBlobId(sourceId, fileKind);
        if (await HasConflictingBlobIdentityAsync(dbContext, sourceId, targetBlobId, ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"Deterministic migration blob `{targetBlobId}` is already owned or reserved by another record.");

        setFileToMigrateId(fileToMigrate, targetBlobId);
        await BlobStore.WriteBlob(targetBlobId, fileToMigrate).ConfigureAwait(false);

        davItem.FileBlobId = targetBlobId;
        removeFileToMigrateFromDb(dbContext, sourceId);
        return await SaveAndReconcileAsync(
                dbContext,
                sourceId,
                targetBlobId,
                legacyRowExists,
                deleteTargetWhenDefinitelyUncommitted: true,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<MigrationStepResult> SaveAndReconcileAsync(
        DavDatabaseContext dbContext,
        Guid sourceId,
        Guid targetBlobId,
        Func<DavDatabaseContext, Guid, CancellationToken, Task<bool>> legacyRowExists,
        bool deleteTargetWhenDefinitelyUncommitted,
        CancellationToken ct)
    {
        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return MigrationStepResult.Migrated;
        }
        catch
        {
            var reconciliation = await ReconcileCommitAsync(
                    sourceId,
                    targetBlobId,
                    legacyRowExists)
                .ConfigureAwait(false);

            if (reconciliation == CommitReconciliation.Committed)
                return MigrationStepResult.Migrated;

            if (reconciliation == CommitReconciliation.Uncommitted
                && deleteTargetWhenDefinitelyUncommitted)
                BlobStore.Delete(targetBlobId);

            throw;
        }
    }

    private async Task<CommitReconciliation> ReconcileCommitAsync(
        Guid sourceId,
        Guid targetBlobId,
        Func<DavDatabaseContext, Guid, CancellationToken, Task<bool>> legacyRowExists)
    {
        using var timeout = new CancellationTokenSource(ReconciliationTimeout);
        try
        {
            await using var dbContext = _createDbContext();
            var current = await dbContext.Items
                .AsNoTracking()
                .Where(x => x.Id == sourceId)
                .Select(x => new { x.FileBlobId })
                .SingleOrDefaultAsync(timeout.Token)
                .ConfigureAwait(false);
            var legacyStillExists = await legacyRowExists(dbContext, sourceId, timeout.Token)
                .ConfigureAwait(false);

            if (current?.FileBlobId == targetBlobId && !legacyStillExists)
                return CommitReconciliation.Committed;

            if (current is not null
                && current.FileBlobId != targetBlobId
                && legacyStillExists
                && !await HasConflictingBlobIdentityAsync(
                        dbContext,
                        sourceId,
                        targetBlobId,
                        timeout.Token)
                    .ConfigureAwait(false))
                return CommitReconciliation.Uncommitted;

            return CommitReconciliation.Indeterminate;
        }
        catch (Exception e)
        {
            Log.Warning(
                e,
                "Could not reconcile legacy file migration for DavItem {DavItemId}; preserving blob {BlobId}",
                sourceId,
                targetBlobId);
            return CommitReconciliation.Indeterminate;
        }
    }

    private static async Task<bool> HasConflictingBlobIdentityAsync(
        DavDatabaseContext dbContext,
        Guid sourceId,
        Guid targetBlobId,
        CancellationToken ct)
    {
        if (await dbContext.Items.AsNoTracking().AnyAsync(
                x => (x.Id != sourceId && x.FileBlobId == targetBlobId)
                     || x.NzbBlobId == targetBlobId,
                ct).ConfigureAwait(false))
            return true;

        if (await dbContext.QueueItems.AsNoTracking()
                .AnyAsync(x => x.Id == targetBlobId, ct)
                .ConfigureAwait(false))
            return true;

        if (await dbContext.HistoryItems.AsNoTracking()
                .AnyAsync(x => x.NzbBlobId == targetBlobId, ct)
                .ConfigureAwait(false))
            return true;

        if (await dbContext.NzbNames.AsNoTracking()
                .AnyAsync(x => x.Id == targetBlobId, ct)
                .ConfigureAwait(false))
            return true;

        if (await dbContext.NzbBlobCleanupItems.AsNoTracking()
                .AnyAsync(x => x.Id == targetBlobId, ct)
                .ConfigureAwait(false))
            return true;

        return await dbContext.BlobCleanupItems.AsNoTracking()
            .AnyAsync(x => x.Id == targetBlobId, ct)
            .ConfigureAwait(false);
    }

    internal static Guid CreateTargetBlobId(Guid sourceId, LegacyFileKind fileKind)
    {
        Span<byte> input = stackalloc byte[33];
        if (!MigrationBlobNamespace.TryWriteBytes(input[..16], bigEndian: true, out var namespaceBytes)
            || namespaceBytes != 16
            || !sourceId.TryWriteBytes(input.Slice(16, 16), bigEndian: true, out var sourceBytes)
            || sourceBytes != 16)
            throw new InvalidOperationException("Could not encode deterministic migration blob identity.");

        input[32] = (byte)fileKind;
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest[..16], bigEndian: true);
    }

    private static async Task<DavItem> GetDavItem(Guid id, DavDatabaseContext dbContext, CancellationToken ct)
    {
        return (await dbContext.Items.Where(x => x.Id == id).FirstOrDefaultAsync(ct).ConfigureAwait(false))
               ?? throw new Exception($"DavItem with id `{id}` not found");
    }

    private void Report(string message)
    {
        _ = _websocketManager.SendMessage(WebsocketTopic.UsenetFileToBlobstoreMigrationProgress, message);
    }

    private void ReportProgress(int totalRemaining, int initialRemaining)
    {
        var complete = initialRemaining - totalRemaining;
        Report($"Migrated {complete}/{initialRemaining} file(s) to the blob-store.");
    }
}
