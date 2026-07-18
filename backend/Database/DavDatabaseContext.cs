using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Transfer;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using Serilog;

namespace NzbWebDAV.Database;

public class DavDatabaseContext : DbContext
{
    public const string SqliteOwnerProviderMismatchMessage =
        "DavDatabaseContext owns the SQLite migration chain and cannot be constructed for PostgreSQL. " +
        "Use DavDatabaseContextRuntimeFactory for provider selection.";

    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int MaxInvalidationConcurrencyRetries = 5;
    private const int WorkerJobJsonMaxUtf8Bytes = 16 * 1024;
    private int _pendingRcloneInvalidationWake;

    public DavDatabaseContext() : base(CreateSqliteOptions(enforceProviderSelection: true))
    {
    }

    public DavDatabaseContext(DbContextOptions<DavDatabaseContext> options) : base(ValidateSqliteOwnerOptions(options))
    {
    }

    private protected DavDatabaseContext(DbContextOptions options) : base(options)
    {
    }

    public static string ConfigPath => EnvironmentUtil.GetVariable("CONFIG_PATH") ?? "/config";
    public static string DatabaseFilePath => Path.Join(ConfigPath, "db.sqlite");
    public static string DatabaseProvider => EnvironmentUtil.GetVariable("NZBDAV_DATABASE_PROVIDER") ?? "sqlite";
    public static bool IsSqlite => DatabaseProvider.Equals("sqlite", StringComparison.OrdinalIgnoreCase);
    public static bool IsPostgres => DatabaseProvider.Equals("postgres", StringComparison.OrdinalIgnoreCase)
                                    || DatabaseProvider.Equals("postgresql", StringComparison.OrdinalIgnoreCase);

    internal static DbContextOptions<DavDatabaseContext> CreateSqliteOptions(bool enforceProviderSelection)
    {
        if (enforceProviderSelection && !IsSqlite)
        {
            if (IsPostgres)
                throw new InvalidOperationException(SqliteOwnerProviderMismatchMessage);
            throw new InvalidOperationException(
                "Unsupported database provider. Set NZBDAV_DATABASE_PROVIDER to 'sqlite' or 'postgres'.");
        }

        var sqliteConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Shared-cache mode uses table-level locks and returns SQLITE_LOCKED
            // under concurrent writers, bypassing the busy timeout. NZBDav uses
            // many short-lived DbContexts, so private cache + WAL gives better
            // writer/read concurrency and fewer user-visible "file" errors.
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();

        return new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(
                sqliteConnectionString,
                sqlite => sqlite.MigrationsHistoryTable(DatabaseMigrationPolicy.SqliteHistoryTableName))
            .AddInterceptors(
                new SqliteForeignKeyEnabler(),
                new ContentIndexSnapshotInterceptor(),
                new DatabaseCommandTelemetryInterceptor(DatabaseTelemetry.Shared),
                new DatabaseTransactionTelemetryInterceptor(DatabaseTelemetry.Shared))
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
    }

    private static DbContextOptions<DavDatabaseContext> ValidateSqliteOwnerOptions(
        DbContextOptions<DavDatabaseContext> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var databaseProviderExtensions = options.Extensions
            .Where(extension => extension.Info.IsDatabaseProvider)
            .ToArray();
        if (databaseProviderExtensions.Length != 1
            || !string.Equals(
                databaseProviderExtensions[0].GetType().Assembly.GetName().Name,
                "Microsoft.EntityFrameworkCore.Sqlite",
                StringComparison.Ordinal))
            throw new InvalidOperationException(SqliteOwnerProviderMismatchMessage);

        return options;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        return ExecuteWithSqliteBusyRetry(() => SaveChangesWithBlobsAndInvalidations(acceptAllChangesOnSuccess));
    }

    public override Task<int> SaveChangesAsync
    (
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default
    )
    {
        return ExecuteWithSqliteBusyRetryAsync(
            () => SaveChangesWithBlobsAndInvalidationsAsync(acceptAllChangesOnSuccess, cancellationToken),
            cancellationToken);
    }

    public static T ExecuteWithSqliteBusyRetry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (IsRetryableSqliteLock(ex) && attempt < 6)
            {
                DatabaseTelemetry.Shared.RecordBusyRetry();
                Thread.Sleep(GetSqliteRetryDelay(attempt));
            }
        }
    }

    public static async Task<T> ExecuteWithSqliteBusyRetryAsync<T>
    (
        Func<Task<T>> action,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryableSqliteLock(ex) && attempt < 6)
            {
                DatabaseTelemetry.Shared.RecordBusyRetry();
                await Task.Delay(GetSqliteRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryableSqliteLock(Exception ex)
    {
        if (!IsSqlite) return false;

        return ex switch
        {
            SqliteException sqliteException => IsRetryableSqliteLock(sqliteException),
            DbUpdateException { InnerException: SqliteException sqliteException } => IsRetryableSqliteLock(sqliteException),
            _ => false
        };
    }

    private static bool IsRetryableSqliteLock(SqliteException ex)
    {
        return ex.SqliteErrorCode == SqliteBusy || ex.SqliteErrorCode == SqliteLocked;
    }

    private static TimeSpan GetSqliteRetryDelay(int attempt)
    {
        return TimeSpan.FromMilliseconds(100 * attempt * attempt);
    }

    // database sets
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<DavItem> Items => Set<DavItem>();
    public DbSet<DavNzbFile> NzbFiles => Set<DavNzbFile>();
    public DbSet<DavRarFile> RarFiles => Set<DavRarFile>();
    public DbSet<DavMultipartFile> MultipartFiles => Set<DavMultipartFile>();
    public DbSet<QueueItem> QueueItems => Set<QueueItem>();
    public DbSet<HistoryItem> HistoryItems => Set<HistoryItem>();
    public DbSet<QueueNzbContents> QueueNzbContents => Set<QueueNzbContents>();
    public DbSet<HealthCheckResult> HealthCheckResults => Set<HealthCheckResult>();
    public DbSet<HealthCheckStat> HealthCheckStats => Set<HealthCheckStat>();
    public DbSet<ConfigItem> ConfigItems => Set<ConfigItem>();
    public DbSet<BlobCleanupItem> BlobCleanupItems => Set<BlobCleanupItem>();
    public DbSet<HistoryCleanupItem> HistoryCleanupItems => Set<HistoryCleanupItem>();
    public DbSet<DavCleanupItem> DavCleanupItems => Set<DavCleanupItem>();
    public DbSet<NzbName> NzbNames => Set<NzbName>();
    public DbSet<NzbBlobCleanupItem> NzbBlobCleanupItems => Set<NzbBlobCleanupItem>();
    public DbSet<RcloneInvalidationItem> RcloneInvalidationItems => Set<RcloneInvalidationItem>();
    public DbSet<WorkerJob> WorkerJobs => Set<WorkerJob>();
    public DbSet<RepairRun> RepairRuns => Set<RepairRun>();
    public DbSet<RepairEntryHealth> RepairEntryHealth => Set<RepairEntryHealth>();
    public DbSet<RepairBrokenFile> RepairBrokenFiles => Set<RepairBrokenFile>();
    public DbSet<ArrDownloadCorrelation> ArrDownloadCorrelations => Set<ArrDownloadCorrelation>();
    public DbSet<QueuePriorityHint> QueuePriorityHints => Set<QueuePriorityHint>();
    public DbSet<ArrSearchNudgeCommand> ArrSearchNudgeCommands => Set<ArrSearchNudgeCommand>();
    public DbSet<ArrDownloadLifecycleEvent> ArrDownloadLifecycleEvents => Set<ArrDownloadLifecycleEvent>();
    public DbSet<ImportReceipt> ImportReceipts => Set<ImportReceipt>();
    public DbSet<ArrImportCommand> ArrImportCommands => Set<ArrImportCommand>();
    public DbSet<MaintenanceRun> MaintenanceRuns => Set<MaintenanceRun>();

    // blob items
    public List<DavNzbFile> BlobNzbFiles = [];
    public List<DavRarFile> BlobRarFiles = [];
    public List<DavMultipartFile> BlobMultipartFiles = [];

    public bool SuppressRcloneInvalidations { get; set; }

    // tables
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Model.SetMaxIdentifierLength(63);

        // Account
        b.Entity<Account>(e =>
        {
            e.ToTable("Accounts");
            e.HasKey(i => new { i.Type, i.Username });

            e.Property(i => i.Type)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Username)
                .IsRequired()
                .HasMaxLength(255);

            e.Property(i => i.PasswordHash)
                .IsRequired();

            e.Property(i => i.RandomSalt)
                .IsRequired();
        });

        // DavItem
        b.Entity<DavItem>(e =>
        {
            e.ToTable("DavItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.Name)
                .IsRequired()
                .HasMaxLength(255);

            e.Property(i => i.Type)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.SubType)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Path)
                .IsRequired();

            e.Property(i => i.IdPrefix)
                .IsRequired();

            e.Property(i => i.ReleaseDate)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.LastHealthCheck)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.NextHealthCheck)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.FileBlobId)
                .ValueGeneratedNever()
                .IsRequired(false);

            e.Property(i => i.HistoryItemId)
                .ValueGeneratedNever()
                .IsRequired(false);

            e.Property(i => i.NzbBlobId)
                .ValueGeneratedNever()
                .IsRequired(false);

            e.HasIndex(i => new { i.ParentId, i.Name })
                .IsUnique();

            e.HasIndex(i => new { i.IdPrefix, i.Type });

            e.HasIndex(i => new { i.Type, i.HistoryItemId, i.NextHealthCheck, i.ReleaseDate, i.Id });

            e.HasIndex(i => new { i.HistoryItemId, i.Type, i.CreatedAt });

            e.HasIndex(i => new { i.HistoryItemId, i.SubType, i.CreatedAt });

            e.HasIndex(i => i.NzbBlobId)
                .IsUnique(false);
        });

        // DavNzbFile
        b.Entity<DavNzbFile>(e =>
        {
            e.ToTable("DavNzbFiles");
            e.HasKey(f => f.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(f => f.SegmentIds)
                .HasConversion(new ValueConverter<string[], string>
                (
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => DeserializeOrFallback<string[]>(v) ?? Array.Empty<string>()
                ))
                .IsRequired();

            e.HasOne(f => f.DavItem)
                .WithOne()
                .HasForeignKey<DavNzbFile>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DavRarFile
        b.Entity<DavRarFile>(e =>
        {
            e.ToTable("DavRarFiles");
            e.HasKey(f => f.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(f => f.RarParts)
                .HasConversion(new ValueConverter<DavRarFile.RarPart[], string>
                (
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => DeserializeOrFallback<DavRarFile.RarPart[]>(v) ?? Array.Empty<DavRarFile.RarPart>()
                ))
                .IsRequired();

            e.HasOne(f => f.DavItem)
                .WithOne()
                .HasForeignKey<DavRarFile>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DavMultipartFile
        b.Entity<DavMultipartFile>(e =>
        {
            e.ToTable("DavMultipartFiles");
            e.HasKey(f => f.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(f => f.Metadata)
                .HasConversion(new ValueConverter<DavMultipartFile.Meta, string>
                (
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => DeserializeOrFallback<DavMultipartFile.Meta>(v) ?? new DavMultipartFile.Meta()
                ))
                .IsRequired();

            e.HasOne(f => f.DavItem)
                .WithOne()
                .HasForeignKey<DavMultipartFile>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // QueueItem
        b.Entity<QueueItem>(e =>
        {
            e.ToTable("QueueItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.FileName)
                .IsRequired();

            e.Property(i => i.NzbFileSize)
                .IsRequired();

            e.Property(i => i.TotalSegmentBytes)
                .IsRequired();

            e.Property(i => i.Category)
                .IsRequired();

            e.Property(i => i.ArchivePassword);

            e.Property(i => i.Priority)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.PostProcessing)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.PauseUntil)
                .ValueGeneratedNever();

            e.Property(i => i.JobName)
                .IsRequired();

            e.HasIndex(i => new { i.Category, i.FileName })
                .IsUnique();

            e.HasIndex(i => new { i.Priority })
                .IsUnique(false);

            e.HasIndex(i => new { i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category })
                .IsUnique(false);

            e.HasIndex(i => new { i.Priority, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Priority, i.PauseUntil, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category, i.Priority, i.CreatedAt })
                .IsUnique(false);
        });

        // HistoryItem
        b.Entity<HistoryItem>(e =>
        {
            e.ToTable("HistoryItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.FileName)
                .IsRequired();

            e.Property(i => i.JobName)
                .IsRequired();

            e.Property(i => i.Category)
                .IsRequired();

            e.Property(i => i.DownloadStatus)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.TotalSegmentBytes)
                .IsRequired();

            e.Property(i => i.DownloadTimeSeconds)
                .IsRequired();

            e.Property(i => i.FailMessage)
                .IsRequired(false);

            e.Property(i => i.DownloadDirId)
                .IsRequired(false);

            e.Property(i => i.NzbBlobId)
                .IsRequired(false);

            e.HasIndex(i => new { i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category, i.DownloadDirId })
                .IsUnique(false);

            e.HasIndex(i => i.NzbBlobId)
                .IsUnique(false);
        });

        // QueueNzbContents
        b.Entity<QueueNzbContents>(e =>
        {
            e.ToTable("QueueNzbContents");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.NzbContents)
                .IsRequired();

            e.HasOne(f => f.QueueItem)
                .WithOne()
                .HasForeignKey<QueueNzbContents>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // HealthCheckResult
        b.Entity<HealthCheckResult>(e =>
        {
            e.ToTable("HealthCheckResults");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.DavItemId)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.Path)
                .IsRequired();

            e.Property(i => i.Result)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.RepairStatus)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Message)
                .IsRequired(false);

            e.HasIndex(i => new { i.Result, i.RepairStatus, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.DavItemId, i.CreatedAt, i.Id })
                .IsDescending(false, true, true)
                .IsUnique(false);

            e.HasIndex(h => h.DavItemId)
                .HasFilter("\"RepairStatus\" = 3")
                .IsUnique(false);
        });

        // HealthCheckStats
        b.Entity<HealthCheckStat>(e =>
        {
            e.ToTable("HealthCheckStats");
            e.HasKey(i => new { i.DateStartInclusive, i.DateEndExclusive, i.Result, i.RepairStatus });

            e.Property(i => i.DateStartInclusive)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.DateEndExclusive)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.Result)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.RepairStatus)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Count);
        });

        // WorkerJob
        b.Entity<WorkerJob>(e =>
        {
            e.ToTable("WorkerJobs");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.Kind)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Status)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.TargetId)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.Priority)
                .IsRequired();

            e.Property(i => i.Attempts)
                .IsRequired();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.UpdatedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.AvailableAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.LeaseExpiresAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                    x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null
                );

            e.Property(i => i.CompletedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                    x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null
                );

            e.Property(i => i.LeaseOwner)
                .HasMaxLength(255);

            e.Property(i => i.LeaseToken);

            e.Property(i => i.LeaseGeneration)
                .HasDefaultValue(0L)
                .IsRequired();

            e.Property(i => i.LastHeartbeatAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                    x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null
                );

            e.Property(i => i.StartedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                    x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null
                );

            e.Property(i => i.CancelRequestedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                    x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null
                );

            e.Property(i => i.FailureKind)
                .HasConversion<int?>();

            e.Property(i => i.ProgressJson)
                .HasMaxLength(16 * 1024);

            e.Property(i => i.ProgressUpdatedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                    x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null
                );

            e.Property(i => i.ResultJson)
                .HasMaxLength(16 * 1024);

            e.Property(i => i.LastError)
                .HasMaxLength(1024);

            e.Property(i => i.PayloadJson);

            e.HasIndex(i => new { i.Kind, i.TargetId })
                .IsUnique();

            e.HasIndex(i => new { i.Kind, i.Status, i.AvailableAt, i.LeaseExpiresAt, i.Priority, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Kind, i.Status, i.Priority, i.AvailableAt, i.CreatedAt })
                .IsDescending(false, false, true, false, false)
                .HasDatabaseName("IX_WorkerJobs_Kind_Status_Priority_AvailableAt_CreatedAt")
                .IsUnique(false);

            e.HasIndex(i => new { i.Kind, i.Status, i.LeaseExpiresAt })
                .HasDatabaseName("IX_WorkerJobs_Kind_Status_LeaseExpiresAt")
                .IsUnique(false);

            e.HasIndex(i => new { i.Status, i.LeaseExpiresAt, i.LeaseGeneration })
                .HasDatabaseName("IX_WorkerJobs_Status_LeaseExpiresAt_LeaseGeneration")
                .IsUnique(false);
        });

        // ImportReceipt
        b.Entity<ImportReceipt>(e =>
        {
            e.ToTable("ImportReceipts");
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).ValueGeneratedNever();
            e.Property(i => i.DavItemId).ValueGeneratedNever().IsRequired();
            e.Property(i => i.HistoryItemId).ValueGeneratedNever().IsRequired();
            e.Property(i => i.State).HasConversion<int>().IsRequired();
            e.Property(i => i.CreatedAt).ValueGeneratedNever().HasConversion(
                x => x.UtcTicks,
                x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))).IsRequired();
            e.Property(i => i.UpdatedAt).ValueGeneratedNever().HasConversion(
                x => x.UtcTicks,
                x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))).IsRequired();
            e.Property(i => i.ImportedAt).ValueGeneratedNever().HasConversion(
                x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null);
            e.Property(i => i.RemovedAt).ValueGeneratedNever().HasConversion(
                x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null);
            e.Property(i => i.Detail).HasMaxLength(1024);
            e.HasIndex(i => new { i.DavItemId, i.HistoryItemId }).IsUnique();
            e.HasIndex(i => i.State);
            e.HasIndex(i => i.UpdatedAt);
        });

        // ArrImportCommand
        b.Entity<ArrImportCommand>(e =>
        {
            e.ToTable("ArrImportCommands");
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).ValueGeneratedNever();
            e.Property(i => i.HistoryItemId).ValueGeneratedNever().IsRequired();
            e.Property(i => i.Category).HasMaxLength(255).IsRequired();
            e.Property(i => i.RequiredInvalidationPathsJson).IsRequired();
            e.Property(i => i.Status).HasConversion<int>().IsRequired();
            e.Property(i => i.Attempts).IsRequired();
            e.Property(i => i.CreatedAt).ValueGeneratedNever().HasConversion(
                x => x.UtcTicks,
                x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))).IsRequired();
            e.Property(i => i.UpdatedAt).ValueGeneratedNever().HasConversion(
                x => x.UtcTicks,
                x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))).IsRequired();
            e.Property(i => i.NextAttemptAt).ValueGeneratedNever().HasConversion(
                x => x.UtcTicks,
                x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))).IsRequired();
            e.Property(i => i.LastAttemptAt).ValueGeneratedNever().HasConversion(
                x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null);
            e.Property(i => i.LeaseExpiresAt).ValueGeneratedNever().HasConversion(
                x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null);
            e.Property(i => i.LeaseToken).ValueGeneratedNever();
            e.Property(i => i.VisibleAt).ValueGeneratedNever().HasConversion(
                x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null);
            e.Property(i => i.CompletedAt).ValueGeneratedNever().HasConversion(
                x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null);
            e.Property(i => i.ResultsJson).IsRequired();
            e.Property(i => i.LastError).HasMaxLength(2048);
            e.HasIndex(i => i.HistoryItemId).IsUnique();
            e.HasIndex(i => new { i.Status, i.NextAttemptAt, i.CreatedAt });
            e.HasIndex(i => new { i.Status, i.LeaseExpiresAt });
            e.HasOne<HistoryItem>()
                .WithMany()
                .HasForeignKey(i => i.HistoryItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // MaintenanceRun
        b.Entity<MaintenanceRun>(e =>
        {
            e.ToTable("MaintenanceRuns");
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).ValueGeneratedNever();
            e.Property(i => i.Kind).HasConversion<int>().IsRequired();
            e.Property(i => i.Status).HasConversion<int>().IsRequired();
            e.Property(i => i.ActiveSlot);
            e.Property(i => i.RequestedBy).HasMaxLength(32).IsRequired();
            e.Property(i => i.CreatedAt).ValueGeneratedNever().HasConversion(
                x => x.UtcTicks,
                x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))).IsRequired();
            e.Property(i => i.StartedAt).ValueGeneratedNever().HasConversion(
                x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null);
            e.Property(i => i.UpdatedAt).ValueGeneratedNever().HasConversion(
                x => x.UtcTicks,
                x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))).IsRequired();
            e.Property(i => i.CompletedAt).ValueGeneratedNever().HasConversion(
                x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null);
            e.Property(i => i.CancellationRequestedAt).ValueGeneratedNever().HasConversion(
                x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null);
            e.Property(i => i.ProgressCurrent).IsRequired();
            e.Property(i => i.ProgressTotal);
            e.Property(i => i.Message).HasMaxLength(2048);
            e.Property(i => i.Error).HasMaxLength(4096);
            e.HasIndex(i => i.ActiveSlot).IsUnique();
            e.HasIndex(i => new { i.Status, i.CreatedAt });
            e.HasIndex(i => new { i.Kind, i.CreatedAt });
        });

        // ArrDownloadCorrelation
        b.Entity<ArrDownloadCorrelation>(e =>
        {
            e.ToTable("ArrDownloadCorrelations");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.QueueItemId);

            e.Property(i => i.HistoryItemId);

            e.Property(i => i.ArrApp)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(i => i.InstanceKey)
                .HasMaxLength(512)
                .IsRequired();

            e.Property(i => i.InstanceHost)
                .HasMaxLength(1024)
                .IsRequired();

            e.Property(i => i.DownloadId)
                .HasMaxLength(255);

            e.Property(i => i.MediaKey)
                .HasMaxLength(255);

            e.Property(i => i.ReleaseTitle)
                .HasMaxLength(1024);

            e.Property(i => i.Category)
                .HasMaxLength(255);

            e.Property(i => i.Indexer)
                .HasMaxLength(255);

            e.Property(i => i.DownloadClient)
                .HasMaxLength(255);

            e.Property(i => i.Quality)
                .HasMaxLength(255);

            e.Property(i => i.Status)
                .HasMaxLength(64);

            e.Property(i => i.TrackedDownloadStatus)
                .HasMaxLength(64);

            e.Property(i => i.TrackedDownloadState)
                .HasMaxLength(64);

            e.Property(i => i.Source)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(i => i.ManualLock)
                .IsRequired();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.UpdatedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.LastSeenAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.HasIndex(i => i.QueueItemId)
                .IsUnique(false);

            e.HasIndex(i => i.HistoryItemId)
                .IsUnique(false);

            e.HasIndex(i => new { i.ArrApp, i.InstanceKey, i.DownloadId })
                .IsUnique(false);

            e.HasIndex(i => new { i.ArrApp, i.InstanceKey, i.MediaKey })
                .IsUnique(false);

            e.HasIndex(i => new { i.ArrApp, i.InstanceKey, i.QueueRecordId })
                .IsUnique(false);

            e.HasIndex(i => new { i.IsDuplicate, i.LastSeenAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Source, i.ManualLock })
                .IsUnique(false);
        });

        // QueuePriorityHint
        b.Entity<QueuePriorityHint>(e =>
        {
            e.ToTable("QueuePriorityHints");
            e.HasKey(i => i.QueueItemId);

            e.Property(i => i.QueueItemId)
                .ValueGeneratedNever();

            e.Property(i => i.EffectivePriority)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.ApplyToScheduling)
                .IsRequired();

            e.Property(i => i.ReasonsJson)
                .IsRequired();

            e.Property(i => i.Source)
                .HasMaxLength(64)
                .IsRequired();

            e.Property(i => i.ComputedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.ExpiresAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.StaleReason)
                .HasMaxLength(1024);

            e.HasOne<QueueItem>()
                .WithOne()
                .HasForeignKey<QueuePriorityHint>(i => i.QueueItemId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(i => new { i.EffectivePriority, i.Score, i.ExpiresAt })
                .IsUnique(false);
        });

        // ArrSearchNudgeCommand
        b.Entity<ArrSearchNudgeCommand>(e =>
        {
            e.ToTable("ArrSearchNudgeCommands");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.ArrApp)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(i => i.InstanceKey)
                .HasMaxLength(512)
                .IsRequired();

            e.Property(i => i.InstanceHost)
                .HasMaxLength(1024)
                .IsRequired();

            e.Property(i => i.CommandName)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(i => i.TargetsJson)
                .IsRequired();

            e.Property(i => i.Mode)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(i => i.Status)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(i => i.CooldownKey)
                .HasMaxLength(512)
                .IsRequired();

            e.Property(i => i.Error)
                .HasMaxLength(1024);

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.CompletedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                    x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null
                );

            e.Property(i => i.NextAllowedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.HasIndex(i => new { i.ArrApp, i.InstanceKey, i.Status, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.CooldownKey, i.NextAllowedAt })
                .IsUnique(false);
        });

        // ArrDownloadLifecycleEvent
        b.Entity<ArrDownloadLifecycleEvent>(e =>
        {
            e.ToTable("ArrDownloadLifecycleEvents");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.QueueItemId);

            e.Property(i => i.HistoryItemId);

            e.Property(i => i.ArrApp)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(i => i.InstanceKey)
                .HasMaxLength(512)
                .IsRequired();

            e.Property(i => i.DownloadId)
                .HasMaxLength(255);

            e.Property(i => i.MediaKey)
                .HasMaxLength(255);

            e.Property(i => i.State)
                .HasMaxLength(64)
                .IsRequired();

            e.Property(i => i.StateReason)
                .HasMaxLength(1024);

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.HasIndex(i => new { i.QueueItemId, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.HistoryItemId, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.ArrApp, i.InstanceKey, i.State, i.CreatedAt })
                .IsUnique(false);
        });

        // ConfigItem
        b.Entity<ConfigItem>(e =>
        {
            e.ToTable("ConfigItems");
            e.HasKey(i => i.ConfigName);
            e.Property(i => i.ConfigValue)
                .IsRequired();
        });

        // RepairRun
        b.Entity<RepairRun>(e =>
        {
            e.ToTable("RepairRuns");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.Status)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Stage)
                .HasMaxLength(64)
                .IsRequired();

            e.Property(i => i.StartedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.UpdatedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.CompletedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                    x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null
                );

            e.Property(i => i.CancelledAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                    x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null
                );

            e.Property(i => i.NextDueAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.UtcTicks : (long?)null,
                    x => x.HasValue ? new DateTimeOffset(new DateTime(x.Value, DateTimeKind.Utc)) : null
                );

            e.Property(i => i.Message)
                .HasMaxLength(1024);

            e.HasIndex(i => new { i.Status, i.StartedAt })
                .IsUnique(false);
        });

        // RepairEntryHealth
        b.Entity<RepairEntryHealth>(e =>
        {
            e.ToTable("RepairEntryHealth");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.RepairRunId)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.DavItemId)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.Path)
                .IsRequired();

            e.Property(i => i.State)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Message)
                .HasMaxLength(1024);

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.UpdatedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.HasIndex(i => new { i.RepairRunId, i.DavItemId })
                .IsUnique();

            e.HasIndex(i => new { i.RepairRunId, i.State, i.UpdatedAt })
                .IsUnique(false);

            e.HasOne<RepairRun>()
                .WithMany()
                .HasForeignKey(i => i.RepairRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // RepairBrokenFile
        b.Entity<RepairBrokenFile>(e =>
        {
            e.ToTable("RepairBrokenFiles");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.RepairRunId)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.DavItemId)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.Path)
                .IsRequired();

            e.Property(i => i.Reason)
                .HasMaxLength(1024)
                .IsRequired();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.UtcTicks,
                    x => new DateTimeOffset(new DateTime(x, DateTimeKind.Utc))
                )
                .IsRequired();

            e.Property(i => i.Cleared)
                .IsRequired();

            e.HasIndex(i => new { i.RepairRunId, i.Cleared, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.DavItemId, i.Cleared })
                .IsUnique(false);

            e.HasOne<RepairRun>()
                .WithMany()
                .HasForeignKey(i => i.RepairRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BlobCleanupItem
        b.Entity<BlobCleanupItem>(e =>
        {
            e.ToTable("BlobCleanupItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();
        });

        // HistoryCleanupItem
        b.Entity<HistoryCleanupItem>(e =>
        {
            e.ToTable("HistoryCleanupItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.DeleteMountedFiles)
                .IsRequired();
        });

        // DavCleanupItem
        b.Entity<DavCleanupItem>(e =>
        {
            e.ToTable("DavCleanupItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();
        });

        // NzbName
        b.Entity<NzbName>(e =>
        {
            e.ToTable("NzbNames");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.FileName)
                .IsRequired();
        });

        // NzbBlobCleanupItem
        b.Entity<NzbBlobCleanupItem>(e =>
        {
            e.ToTable("NzbBlobCleanupItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();
        });

        // RcloneInvalidationItem
        b.Entity<RcloneInvalidationItem>(e =>
        {
            e.ToTable("RcloneInvalidationItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.Path)
                .IsRequired();

            e.Property(i => i.Revision)
                .IsConcurrencyToken()
                .IsRequired();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.NextAttemptAt)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.LastAttemptAt)
                .ValueGeneratedNever()
                .IsRequired(false)
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.Attempts)
                .IsRequired();

            e.Property(i => i.LastError)
                .HasMaxLength(1024)
                .IsRequired(false);

            e.HasIndex(i => new { i.NextAttemptAt, i.CreatedAt });
            e.HasIndex(i => i.Path);
        });

        ConfigureProviderModel(b);
    }

    protected virtual void ConfigureProviderModel(ModelBuilder modelBuilder)
    {
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        return SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);
    }

    private int SaveChangesWithBlobsAndInvalidations(bool acceptAllChangesOnSuccess)
    {
        try
        {
            ValidateNoTrackedReservedConfigMutation();
            ValidateWorkerJobJsonSizes();

            foreach (var blobNzbFile in BlobNzbFiles)
                BlobStore.WriteBlob(blobNzbFile.Id, blobNzbFile).GetAwaiter().GetResult();
            foreach (var blobRarFile in BlobRarFiles)
                BlobStore.WriteBlob(blobRarFile.Id, blobRarFile).GetAwaiter().GetResult();
            foreach (var blobMultipartFile in BlobMultipartFiles)
                BlobStore.WriteBlob(blobMultipartFile.Id, blobMultipartFile).GetAwaiter().GetResult();

            var addedOrRemovedDavItems = GetAddedOrRemovedDavItems();
            if (!SuppressRcloneInvalidations)
                EnqueueRcloneVfsForget(addedOrRemovedDavItems);
            var hasCommittedRcloneInvalidations = ChangeTracker.Entries<RcloneInvalidationItem>()
                .Any(x => x.State is EntityState.Added or EntityState.Modified);
            var result = SaveChangesWithInvalidationConcurrencyRecovery(acceptAllChangesOnSuccess);
            if (hasCommittedRcloneInvalidations)
                PublishOrDeferRcloneInvalidationWake();

            BlobNzbFiles.Clear();
            BlobRarFiles.Clear();
            BlobMultipartFiles.Clear();

            return result;
        }
        catch
        {
            // A database commit exception can be ambiguous: the transaction may
            // already be durable even though the acknowledgement was lost. Keep
            // the already-durable blobs so a committed row can never reference a
            // file we deleted. Orphan cleanup can safely reclaim unreferenced blobs.
            throw;
        }
    }

    private async Task<int> SaveChangesWithBlobsAndInvalidationsAsync
    (
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken
    )
    {
        try
        {
            ValidateNoTrackedReservedConfigMutation();
            ValidateWorkerJobJsonSizes();

            // save blobs to blob-store
            foreach (var blobNzbFile in BlobNzbFiles)
                await BlobStore.WriteBlob(blobNzbFile.Id, blobNzbFile);
            foreach (var blobRarFile in BlobRarFiles)
                await BlobStore.WriteBlob(blobRarFile.Id, blobRarFile);
            foreach (var blobMultipartFile in BlobMultipartFiles)
                await BlobStore.WriteBlob(blobMultipartFile.Id, blobMultipartFile);

            // save db changes
            var addedOrRemovedDavItems = GetAddedOrRemovedDavItems();
            if (!SuppressRcloneInvalidations)
                EnqueueRcloneVfsForget(addedOrRemovedDavItems);
            var hasCommittedRcloneInvalidations = ChangeTracker.Entries<RcloneInvalidationItem>()
                .Any(x => x.State is EntityState.Added or EntityState.Modified);
            var result = await SaveChangesWithInvalidationConcurrencyRecoveryAsync(
                    acceptAllChangesOnSuccess,
                    cancellationToken)
                .ConfigureAwait(false);
            if (hasCommittedRcloneInvalidations)
                PublishOrDeferRcloneInvalidationWake();

            // clear pending blob writes
            BlobNzbFiles.Clear();
            BlobRarFiles.Clear();
            BlobMultipartFiles.Clear();

            // return
            return result;
        }
        catch
        {
            // Preserve durable blobs across ambiguous commit failures. Retrying
            // is idempotent, while deleting here can corrupt a successful commit.
            throw;
        }
    }

    private void ValidateWorkerJobJsonSizes()
    {
        foreach (var workerJob in ChangeTracker.Entries<WorkerJob>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified)
                     .Select(x => x.Entity))
        {
            ValidateWorkerJobJsonSize(workerJob.ProgressJson, nameof(WorkerJob.ProgressJson));
            ValidateWorkerJobJsonSize(workerJob.ResultJson, nameof(WorkerJob.ResultJson));
        }
    }

    private void ValidateNoTrackedReservedConfigMutation()
    {
        var autoDetectChangesEnabled = ChangeTracker.AutoDetectChangesEnabled;
        try
        {
            ChangeTracker.AutoDetectChangesEnabled = false;
            foreach (var entry in ChangeTracker.Entries<ConfigItem>())
            {
                var currentName = entry.Entity.ConfigName;
                var currentValue = entry.Entity.ConfigValue;
                var originalName = entry.Property(item => item.ConfigName).OriginalValue;
                var originalValue = entry.Property(item => item.ConfigValue).OriginalValue;
                var hasMutation = entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                                  || !string.Equals(currentName, originalName, StringComparison.Ordinal)
                                  || !string.Equals(currentValue, originalValue, StringComparison.Ordinal);
                if (hasMutation
                    && (TransferV3ReservedConfigPolicy.IsReserved(currentName)
                        || TransferV3ReservedConfigPolicy.IsReserved(originalName)))
                    throw new InvalidOperationException(TransferV3ReservedConfigPolicy.ReservedConfigMessage);
            }
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = autoDetectChangesEnabled;
        }
    }

    private int SaveChangesWithInvalidationConcurrencyRecovery(bool acceptAllChangesOnSuccess)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return base.SaveChanges(acceptAllChangesOnSuccess);
            }
            catch (DbUpdateConcurrencyException exception) when (
                attempt < MaxInvalidationConcurrencyRetries
                && IsInvalidationPublishConflict(exception))
            {
                ResolveInvalidationPublishConflict(exception);
            }
            catch (DbUpdateException exception) when (
                attempt < MaxInvalidationConcurrencyRetries
                && IsWholeCacheSentinelInsertConflict(exception))
            {
                ResolveWholeCacheSentinelInsertConflict(exception);
            }
        }
    }

    private async Task<int> SaveChangesWithInvalidationConcurrencyRecoveryAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException exception) when (
                attempt < MaxInvalidationConcurrencyRetries
                && IsInvalidationPublishConflict(exception))
            {
                await ResolveInvalidationPublishConflictAsync(exception, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (
                attempt < MaxInvalidationConcurrencyRetries
                && IsWholeCacheSentinelInsertConflict(exception))
            {
                await ResolveWholeCacheSentinelInsertConflictAsync(exception, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsInvalidationPublishConflict(DbUpdateConcurrencyException exception)
    {
        return exception.Entries.Count > 0
               && exception.Entries.All(entry =>
                   entry.Entity is RcloneInvalidationItem
                   && entry.State == EntityState.Modified);
    }

    private static bool IsWholeCacheSentinelInsertConflict(DbUpdateException exception)
    {
        var uniqueViolation = exception.InnerException switch
        {
            SqliteException { SqliteErrorCode: 19 } => true,
            PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } => true,
            _ => false
        };
        return uniqueViolation
               && exception.Entries.Count > 0
               && exception.Entries.All(entry =>
                   entry is { Entity: RcloneInvalidationItem item, State: EntityState.Added }
                   && item.Id == RcloneInvalidationItem.WholeCacheVisibilityFenceId
                   && item.Path == RcloneInvalidationItem.WholeCacheVisibilityFencePath);
    }

    private static void ResolveWholeCacheSentinelInsertConflict(DbUpdateException exception)
    {
        foreach (var entry in exception.Entries)
        {
            var item = (RcloneInvalidationItem)entry.Entity;
            var databaseValues = entry.GetDatabaseValues();
            RebaseInvalidationEntry(entry, databaseValues, item.Revision, item.NextAttemptAt);
        }
    }

    private static async Task ResolveWholeCacheSentinelInsertConflictAsync(
        DbUpdateException exception,
        CancellationToken cancellationToken)
    {
        foreach (var entry in exception.Entries)
        {
            var item = (RcloneInvalidationItem)entry.Entity;
            var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken).ConfigureAwait(false);
            RebaseInvalidationEntry(entry, databaseValues, item.Revision, item.NextAttemptAt);
        }
    }

    private static void ResolveInvalidationPublishConflict(DbUpdateConcurrencyException exception)
    {
        foreach (var entry in exception.Entries)
        {
            var item = (RcloneInvalidationItem)entry.Entity;
            var databaseValues = entry.GetDatabaseValues();
            RebaseInvalidationEntry(entry, databaseValues, item.Revision, item.NextAttemptAt);
        }
    }

    private static async Task ResolveInvalidationPublishConflictAsync(
        DbUpdateConcurrencyException exception,
        CancellationToken cancellationToken)
    {
        foreach (var entry in exception.Entries)
        {
            var item = (RcloneInvalidationItem)entry.Entity;
            var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken).ConfigureAwait(false);
            RebaseInvalidationEntry(entry, databaseValues, item.Revision, item.NextAttemptAt);
        }
    }

    private static void RebaseInvalidationEntry(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        Microsoft.EntityFrameworkCore.ChangeTracking.PropertyValues? databaseValues,
        long desiredRevision,
        DateTimeOffset desiredNextAttemptAt)
    {
        var item = (RcloneInvalidationItem)entry.Entity;
        if (databaseValues is null)
        {
            item.Revision = Math.Max(1, desiredRevision);
            entry.State = EntityState.Added;
            return;
        }

        var databaseRevision = databaseValues.GetValue<long>(nameof(RcloneInvalidationItem.Revision));
        var databaseNextAttemptAt = databaseValues.GetValue<DateTimeOffset>(
            nameof(RcloneInvalidationItem.NextAttemptAt));
        entry.OriginalValues.SetValues(databaseValues);
        entry.CurrentValues.SetValues(databaseValues);
        entry.State = EntityState.Unchanged;
        item.Revision = checked(Math.Max(desiredRevision, databaseRevision) + 1);
        item.NextAttemptAt = desiredNextAttemptAt <= databaseNextAttemptAt
            ? desiredNextAttemptAt
            : databaseNextAttemptAt;
        entry.Property(nameof(RcloneInvalidationItem.Revision)).IsModified = true;
        if (item.NextAttemptAt != databaseNextAttemptAt)
            entry.Property(nameof(RcloneInvalidationItem.NextAttemptAt)).IsModified = true;
    }

    private void PublishOrDeferRcloneInvalidationWake()
    {
        if (Database.CurrentTransaction is null)
        {
            RcloneInvalidationWakeSignal.Pulse();
            return;
        }

        Volatile.Write(ref _pendingRcloneInvalidationWake, 1);
    }

    internal void PublishCommittedDatabaseWakeSignals()
    {
        if (Interlocked.Exchange(ref _pendingRcloneInvalidationWake, 0) != 0)
            RcloneInvalidationWakeSignal.Pulse();
    }

    internal void DiscardUncommittedDatabaseWakeSignals()
    {
        Volatile.Write(ref _pendingRcloneInvalidationWake, 0);
    }

    private static void ValidateWorkerJobJsonSize(string? value, string propertyName)
    {
        if (value is null || Encoding.UTF8.GetByteCount(value) <= WorkerJobJsonMaxUtf8Bytes) return;

        throw new InvalidOperationException(
            $"WorkerJob {propertyName} exceeds the {WorkerJobJsonMaxUtf8Bytes} UTF-8 byte limit.");
    }

    private List<DavItem> GetAddedOrRemovedDavItems()
    {
        return ChangeTracker.Entries<DavItem>()
            .Where(x => x.State is EntityState.Added or EntityState.Deleted)
            .Select(x => x.Entity)
            .ToList();
    }

    public static IReadOnlyList<string> GetRcloneVfsForgetDirectories(IReadOnlyCollection<DavItem> addedOrRemoved)
    {
        var contentDirs = addedOrRemoved
            .Select(x => x.Path)
            .Select(Path.GetDirectoryName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();

        var idDirs = addedOrRemoved
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Select(x => DatabaseStoreSymlinkFile.GetTargetPath(x.Id))
            .Select(Path.GetDirectoryName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();

        var completedSymlinkDirs = contentDirs
            .Where(x => x.StartsWith("/content"))
            .Select(x => $"/completed-symlinks{x["/content".Length..]}")
            .ToList();

        return contentDirs
            .Concat(completedSymlinkDirs)
            .Concat(idDirs)
            .Distinct()
            .ToList();
    }

    public void EnqueueRcloneVfsForget(List<DavItem> addedOrRemovedDavItems)
    {
        if (SuppressRcloneInvalidations) return;
        if (addedOrRemovedDavItems.Count == 0) return;

        var vfsForgetPaths = GetRcloneVfsForgetDirectories(addedOrRemovedDavItems);
        EnqueueRcloneVfsForgetPaths(vfsForgetPaths);
    }

    public void EnqueueRcloneVfsForgetPaths(IEnumerable<string> paths)
    {
        if (SuppressRcloneInvalidations) return;

        var normalizedPaths = paths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedPaths.Count == 0) return;

        if (!RcloneClient.RequiresVfsVisibilityFence)
        {
            EnqueueWholeCacheVisibilityFence();
            return;
        }

        EnqueueRcloneInvalidationRows(normalizedPaths, useDeterministicWholeCacheIdentity: false);
    }

    internal void EnqueueWholeCacheVisibilityFence()
    {
        if (SuppressRcloneInvalidations) return;
        EnqueueRcloneInvalidationRows(
            [RcloneInvalidationItem.WholeCacheVisibilityFencePath],
            useDeterministicWholeCacheIdentity: true);
    }

    private void EnqueueRcloneInvalidationRows(
        IEnumerable<string> paths,
        bool useDeterministicWholeCacheIdentity)
    {
        var now = DateTimeOffset.UtcNow;
        var pathList = paths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (pathList.Count == 0) return;

        var pathSet = pathList.ToHashSet(StringComparer.Ordinal);
        var existingItems = RcloneInvalidationItems.Local
            .Where(x => pathSet.Contains(x.Path) && Entry(x).State != EntityState.Deleted)
            .Concat(RcloneInvalidationItems
                .Where(x => pathList.Contains(x.Path))
                .ToList())
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .ToList();
        var existingPaths = existingItems
            .Select(x => x.Path)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var existingItem in existingItems)
        {
            existingItem.Revision = checked(existingItem.Revision + 1);
            // Always mark the due time as part of the fenced publication. A
            // worker may reschedule the old revision after this row was read.
            existingItem.NextAttemptAt = now;
        }

        var newItems = new List<RcloneInvalidationItem>();
        foreach (var path in pathList)
        {
            if (existingPaths.Contains(path))
                continue;

            newItems.Add(new RcloneInvalidationItem
            {
                Id = useDeterministicWholeCacheIdentity
                    ? RcloneInvalidationItem.WholeCacheVisibilityFenceId
                    : Guid.NewGuid(),
                Path = path,
                Revision = 1,
                CreatedAt = now,
                NextAttemptAt = now
            });
        }

        RcloneInvalidationItems.AddRange(newItems);
    }

    public static async Task EnqueueRcloneVfsForgetAsync
    (
        List<DavItem> addedOrRemovedDavItems,
        CancellationToken cancellationToken = default
    )
    {
        if (addedOrRemovedDavItems.Count == 0) return;

        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        dbContext.EnqueueRcloneVfsForget(addedOrRemovedDavItems);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task EnqueueRcloneVfsForgetPathsAsync
    (
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default
    )
    {
        await using var dbContext = DavDatabaseContextRuntimeFactory.Create();
        dbContext.EnqueueRcloneVfsForgetPaths(paths);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public void ClearChangeTracker()
    {
        ChangeTracker.Clear();
        BlobNzbFiles.Clear();
        BlobRarFiles.Clear();
        BlobMultipartFiles.Clear();
    }

    private static T? DeserializeOrFallback<T>(string? value)
    {
        if (string.IsNullOrEmpty(value)) return default;

        var first = value[0];
        if (first is '[' or '{' or '"' or '-' or (>= '0' and <= '9') or 't' or 'f' or 'n')
            return JsonSerializer.Deserialize<T>(value, (JsonSerializerOptions?)null);

        try
        {
            var bytes = Convert.FromBase64String(value);
            using var input = new MemoryStream(bytes);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(brotli, Encoding.UTF8);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<T>(json, (JsonSerializerOptions?)null);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "Failed to deserialize database value as JSON or legacy Base64+Brotli. Preview: {Preview}",
                value.Length > 200 ? value[..200] : value);
            return default;
        }
    }
}
