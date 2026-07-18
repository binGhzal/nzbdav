using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace NzbWebDAV.Database;

public static class DatabaseStorageTelemetry
{
    private static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim CaptureLock = new(1, 1);
    private static string? _cachedKey;
    private static DatabaseStorageSnapshot? _cachedSnapshot;

    public static async Task<DatabaseStorageSnapshot> CaptureAsync(
        DbContext context,
        CancellationToken ct = default,
        TimeSpan? cacheDuration = null)
    {
        var duration = cacheDuration ?? DefaultCacheDuration;
        var cacheKey = $"{context.Database.ProviderName}:{context.Database.GetDbConnection().DataSource}";
        var cached = Volatile.Read(ref _cachedSnapshot);
        if (IsFresh(cacheKey, cached, duration)) return cached!;

        await CaptureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _cachedSnapshot);
            if (IsFresh(cacheKey, cached, duration)) return cached!;

            var captured = await CaptureCoreAsync(context, ct).ConfigureAwait(false);
            _cachedKey = cacheKey;
            Volatile.Write(ref _cachedSnapshot, captured);
            return captured;
        }
        finally
        {
            CaptureLock.Release();
        }
    }

    private static bool IsFresh(
        string cacheKey,
        DatabaseStorageSnapshot? snapshot,
        TimeSpan cacheDuration)
    {
        return snapshot is not null
               && string.Equals(_cachedKey, cacheKey, StringComparison.Ordinal)
               && DateTimeOffset.UtcNow - snapshot.CapturedAt < cacheDuration;
    }

    private static async Task<DatabaseStorageSnapshot> CaptureCoreAsync(
        DbContext context,
        CancellationToken ct)
    {
        if (!context.Database.IsSqlite())
        {
            return new DatabaseStorageSnapshot(
                Provider: context.Database.ProviderName ?? "unknown",
                DatabaseBytes: 0,
                WalBytes: 0,
                SharedMemoryBytes: 0,
                PageSizeBytes: 0,
                PageCount: 0,
                FreelistPages: 0,
                CheckpointBusy: 0,
                WalFrames: 0,
                CheckpointedFrames: 0,
                CheckpointBacklogBytes: 0,
                CapturedAt: DateTimeOffset.UtcNow);
        }

        var connection = context.Database.GetDbConnection();
        var closeAfterCapture = connection.State == ConnectionState.Closed;
        if (closeAfterCapture)
            await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

        try
        {
            var pageSize = await ReadScalarLongAsync(connection, "PRAGMA page_size;", ct).ConfigureAwait(false);
            var pageCount = await ReadScalarLongAsync(connection, "PRAGMA page_count;", ct).ConfigureAwait(false);
            var freelistPages = await ReadScalarLongAsync(connection, "PRAGMA freelist_count;", ct).ConfigureAwait(false);
            var checkpoint = await ReadCheckpointAsync(connection, ct).ConfigureAwait(false);
            var databasePath = connection.DataSource;
            var databaseBytes = GetFileLength(databasePath);
            var walBytes = GetFileLength(databasePath + "-wal");
            var sharedMemoryBytes = GetFileLength(databasePath + "-shm");
            var backlogFrames = Math.Max(0, checkpoint.LogFrames - checkpoint.CheckpointedFrames);

            return new DatabaseStorageSnapshot(
                Provider: "sqlite",
                DatabaseBytes: databaseBytes,
                WalBytes: walBytes,
                SharedMemoryBytes: sharedMemoryBytes,
                PageSizeBytes: pageSize,
                PageCount: pageCount,
                FreelistPages: freelistPages,
                CheckpointBusy: checkpoint.Busy,
                WalFrames: checkpoint.LogFrames,
                CheckpointedFrames: checkpoint.CheckpointedFrames,
                CheckpointBacklogBytes: checked(backlogFrames * pageSize),
                CapturedAt: DateTimeOffset.UtcNow);
        }
        finally
        {
            if (closeAfterCapture)
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<long> ReadScalarLongAsync(
        DbConnection connection,
        string sql,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static async Task<CheckpointSnapshot> ReadCheckpointAsync(
        DbConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        // SQLite 3.53's NOOP mode reports WAL/checkpoint counters without
        // checkpointing frames or adding storage work to a telemetry read.
        command.CommandText = "PRAGMA wal_checkpoint(NOOP);";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new CheckpointSnapshot(0, 0, 0);
        return new CheckpointSnapshot(
            Busy: reader.GetInt64(0),
            LogFrames: reader.GetInt64(1),
            CheckpointedFrames: reader.GetInt64(2));
    }

    private static long GetFileLength(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    private sealed record CheckpointSnapshot(long Busy, long LogFrames, long CheckpointedFrames);
}

public sealed record DatabaseStorageSnapshot(
    string Provider,
    long DatabaseBytes,
    long WalBytes,
    long SharedMemoryBytes,
    long PageSizeBytes,
    long PageCount,
    long FreelistPages,
    long CheckpointBusy,
    long WalFrames,
    long CheckpointedFrames,
    long CheckpointBacklogBytes,
    DateTimeOffset CapturedAt);
