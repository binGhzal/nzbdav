using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Database;

public static class DatabaseTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task ExportJsonAsync
    (
        DavDatabaseContext dbContext,
        string outputPath,
        CancellationToken ct = default
    )
    {
        var snapshot = new DatabaseTransferSnapshot
        {
            ExportedAt = DateTimeOffset.UtcNow,
            Provider = DavDatabaseContext.DatabaseProvider,
            Accounts = await dbContext.Accounts.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            Items = await dbContext.Items.AsNoTracking().OrderBy(x => x.Path).ToListAsync(ct).ConfigureAwait(false),
            NzbFiles = await dbContext.NzbFiles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            RarFiles = await dbContext.RarFiles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            MultipartFiles = await dbContext.MultipartFiles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            QueueItems = await dbContext.QueueItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            HistoryItems = await dbContext.HistoryItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            QueueNzbContents = await dbContext.QueueNzbContents.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            HealthCheckResults = await dbContext.HealthCheckResults.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            HealthCheckStats = await dbContext.HealthCheckStats.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            ConfigItems = await dbContext.ConfigItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            BlobCleanupItems = await dbContext.BlobCleanupItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            HistoryCleanupItems = await dbContext.HistoryCleanupItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            DavCleanupItems = await dbContext.DavCleanupItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            NzbNames = await dbContext.NzbNames.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            NzbBlobCleanupItems = await dbContext.NzbBlobCleanupItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            RcloneInvalidationItems = await dbContext.RcloneInvalidationItems.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            WorkerJobs = await dbContext.WorkerJobs.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            RepairRuns = await dbContext.RepairRuns.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            RepairEntryHealth = await dbContext.RepairEntryHealth.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            RepairBrokenFiles = await dbContext.RepairBrokenFiles.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            ArrDownloadCorrelations = await dbContext.ArrDownloadCorrelations.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            QueuePriorityHints = await dbContext.QueuePriorityHints.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            ArrSearchNudgeCommands = await dbContext.ArrSearchNudgeCommands.AsNoTracking().ToListAsync(ct).ConfigureAwait(false),
            ArrDownloadLifecycleEvents = await dbContext.ArrDownloadLifecycleEvents.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, ct).ConfigureAwait(false);
    }

    public static async Task<DatabaseTransferImportResult> ImportJsonAsync
    (
        DavDatabaseContext dbContext,
        string inputPath,
        bool replace,
        CancellationToken ct = default
    )
    {
        await using var stream = File.OpenRead(inputPath);
        var snapshot = await JsonSerializer.DeserializeAsync<DatabaseTransferSnapshot>(stream, JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Database transfer snapshot is empty or invalid.");
        if (snapshot.Version != DatabaseTransferSnapshot.CurrentVersion)
            throw new InvalidDataException($"Unsupported database transfer snapshot version {snapshot.Version}.");

        await dbContext.Database.MigrateAsync(cancellationToken: ct).ConfigureAwait(false);
        var hasRows = await HasApplicationRowsAsync(dbContext, ct).ConfigureAwait(false);
        if (hasRows && !replace)
            throw new InvalidOperationException("Target database is not empty. Re-run with --replace to overwrite it.");

        await using var tx = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
        if (replace)
            await ClearApplicationTablesAsync(dbContext, ct).ConfigureAwait(false);

        await AddAndSaveAsync(dbContext, snapshot.Accounts, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.ConfigItems, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.Items, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.NzbFiles, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.RarFiles, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.MultipartFiles, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.QueueItems, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.HistoryItems, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.QueueNzbContents, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.HealthCheckResults, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.HealthCheckStats, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.BlobCleanupItems, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.HistoryCleanupItems, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.DavCleanupItems, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.NzbNames, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.NzbBlobCleanupItems, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.RcloneInvalidationItems, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.WorkerJobs, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.RepairRuns, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.RepairEntryHealth, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.RepairBrokenFiles, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.ArrDownloadCorrelations, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.QueuePriorityHints, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.ArrSearchNudgeCommands, ct).ConfigureAwait(false);
        await AddAndSaveAsync(dbContext, snapshot.ArrDownloadLifecycleEvents, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new DatabaseTransferImportResult(snapshot.TotalRows);
    }

    private static async Task AddAndSaveAsync<T>
    (
        DavDatabaseContext dbContext,
        IReadOnlyCollection<T> rows,
        CancellationToken ct
    ) where T : class
    {
        if (rows.Count == 0) return;
        dbContext.Set<T>().AddRange(rows);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
    }

    private static async Task<bool> HasApplicationRowsAsync(DavDatabaseContext dbContext, CancellationToken ct)
    {
        return await dbContext.Items.AnyAsync(ct).ConfigureAwait(false)
               || await dbContext.QueueItems.AnyAsync(ct).ConfigureAwait(false)
               || await dbContext.HistoryItems.AnyAsync(ct).ConfigureAwait(false)
               || await dbContext.ConfigItems.AnyAsync(ct).ConfigureAwait(false)
               || await dbContext.Accounts.AnyAsync(ct).ConfigureAwait(false);
    }

    private static async Task ClearApplicationTablesAsync(DavDatabaseContext dbContext, CancellationToken ct)
    {
        await dbContext.ArrDownloadLifecycleEvents.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.ArrSearchNudgeCommands.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.QueuePriorityHints.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.ArrDownloadCorrelations.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.RepairBrokenFiles.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.RepairEntryHealth.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.RepairRuns.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.WorkerJobs.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.RcloneInvalidationItems.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.NzbBlobCleanupItems.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.NzbNames.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.DavCleanupItems.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.HistoryCleanupItems.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.BlobCleanupItems.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.HealthCheckStats.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.HealthCheckResults.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.QueueNzbContents.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.MultipartFiles.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.RarFiles.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.NzbFiles.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.QueueItems.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.HistoryItems.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.Items.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.ConfigItems.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await dbContext.Accounts.ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
}

public sealed class DatabaseTransferSnapshot
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset ExportedAt { get; set; }
    public string Provider { get; set; } = "";
    public List<Account> Accounts { get; set; } = [];
    public List<DavItem> Items { get; set; } = [];
    public List<DavNzbFile> NzbFiles { get; set; } = [];
    public List<DavRarFile> RarFiles { get; set; } = [];
    public List<DavMultipartFile> MultipartFiles { get; set; } = [];
    public List<QueueItem> QueueItems { get; set; } = [];
    public List<HistoryItem> HistoryItems { get; set; } = [];
    public List<QueueNzbContents> QueueNzbContents { get; set; } = [];
    public List<HealthCheckResult> HealthCheckResults { get; set; } = [];
    public List<HealthCheckStat> HealthCheckStats { get; set; } = [];
    public List<ConfigItem> ConfigItems { get; set; } = [];
    public List<BlobCleanupItem> BlobCleanupItems { get; set; } = [];
    public List<HistoryCleanupItem> HistoryCleanupItems { get; set; } = [];
    public List<DavCleanupItem> DavCleanupItems { get; set; } = [];
    public List<NzbName> NzbNames { get; set; } = [];
    public List<NzbBlobCleanupItem> NzbBlobCleanupItems { get; set; } = [];
    public List<RcloneInvalidationItem> RcloneInvalidationItems { get; set; } = [];
    public List<WorkerJob> WorkerJobs { get; set; } = [];
    public List<RepairRun> RepairRuns { get; set; } = [];
    public List<RepairEntryHealth> RepairEntryHealth { get; set; } = [];
    public List<RepairBrokenFile> RepairBrokenFiles { get; set; } = [];
    public List<ArrDownloadCorrelation> ArrDownloadCorrelations { get; set; } = [];
    public List<QueuePriorityHint> QueuePriorityHints { get; set; } = [];
    public List<ArrSearchNudgeCommand> ArrSearchNudgeCommands { get; set; } = [];
    public List<ArrDownloadLifecycleEvent> ArrDownloadLifecycleEvents { get; set; } = [];

    public int TotalRows =>
        Accounts.Count + Items.Count + NzbFiles.Count + RarFiles.Count + MultipartFiles.Count
        + QueueItems.Count + HistoryItems.Count + QueueNzbContents.Count + HealthCheckResults.Count
        + HealthCheckStats.Count + ConfigItems.Count + BlobCleanupItems.Count + HistoryCleanupItems.Count
        + DavCleanupItems.Count + NzbNames.Count + NzbBlobCleanupItems.Count + RcloneInvalidationItems.Count
        + WorkerJobs.Count + RepairRuns.Count + RepairEntryHealth.Count + RepairBrokenFiles.Count
        + ArrDownloadCorrelations.Count + QueuePriorityHints.Count + ArrSearchNudgeCommands.Count
        + ArrDownloadLifecycleEvents.Count;
}

public sealed record DatabaseTransferImportResult(int ImportedRows);
