using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using Serilog;

namespace NzbWebDAV.Database;

public sealed class DavDatabaseContext() : DbContext(CreateOptions())
{
    public static string ConfigPath => EnvironmentUtil.GetVariable("CONFIG_PATH") ?? "/config";
    public static string DatabaseFilePath => Path.Join(ConfigPath, "db.sqlite");
    public static string DatabaseProvider => EnvironmentUtil.GetVariable("NZBDAV_DATABASE_PROVIDER") ?? "sqlite";
    public static bool IsSqlite => DatabaseProvider.Equals("sqlite", StringComparison.OrdinalIgnoreCase);
    public static bool IsPostgres => DatabaseProvider.Equals("postgres", StringComparison.OrdinalIgnoreCase)
                                    || DatabaseProvider.Equals("postgresql", StringComparison.OrdinalIgnoreCase);

    private static DbContextOptions<DavDatabaseContext> CreateOptions()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>();

        if (IsPostgres)
        {
            var postgresConnectionString = EnvironmentUtil.GetRequiredVariable("NZBDAV_DATABASE_CONNECTION_STRING");
            return options
                .UseNpgsql(postgresConnectionString)
                .ConfigureWarnings(warnings =>
                    warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .AddInterceptors(new ContentIndexSnapshotInterceptor())
                .Options;
        }

        if (!IsSqlite)
            throw new InvalidOperationException(
                "Unsupported database provider. Set NZBDAV_DATABASE_PROVIDER to 'sqlite' or 'postgres'.");

        var sqliteConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();

        return options
            .UseSqlite(sqliteConnectionString)
            .AddInterceptors(new SqliteForeignKeyEnabler(), new ContentIndexSnapshotInterceptor())
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
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

    // blob items
    public List<DavNzbFile> BlobNzbFiles = [];
    public List<DavRarFile> BlobRarFiles = [];
    public List<DavMultipartFile> BlobMultipartFiles = [];

    // tables
    protected override void OnModelCreating(ModelBuilder b)
    {
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
                .HasColumnType("TEXT")
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
                .HasColumnType("TEXT")
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
                .HasColumnType("TEXT")
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
                .HasColumnType("TEXT")
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

            e.Property(i => i.LastError)
                .HasMaxLength(1024);

            e.Property(i => i.PayloadJson);

            e.HasIndex(i => new { i.Kind, i.TargetId })
                .IsUnique();

            e.HasIndex(i => new { i.Kind, i.Status, i.AvailableAt, i.LeaseExpiresAt, i.Priority, i.CreatedAt })
                .IsUnique(false);
        });

        // ArrDownloadCorrelation
        b.Entity<ArrDownloadCorrelation>(e =>
        {
            e.ToTable("ArrDownloadCorrelations");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .HasColumnType("TEXT")
                .ValueGeneratedNever();

            e.Property(i => i.QueueItemId)
                .HasColumnType("TEXT");

            e.Property(i => i.HistoryItemId)
                .HasColumnType("TEXT");

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
        });

        // QueuePriorityHint
        b.Entity<QueuePriorityHint>(e =>
        {
            e.ToTable("QueuePriorityHints");
            e.HasKey(i => i.QueueItemId);

            e.Property(i => i.QueueItemId)
                .HasColumnType("TEXT")
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
                .HasColumnType("TEXT")
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
                .HasColumnType("TEXT")
                .ValueGeneratedNever();

            e.Property(i => i.QueueItemId)
                .HasColumnType("TEXT");

            e.Property(i => i.HistoryItemId)
                .HasColumnType("TEXT");

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
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        try
        {
            // save blobs to blob-store
            foreach (var blobNzbFile in BlobNzbFiles)
                await BlobStore.WriteBlob(blobNzbFile.Id, blobNzbFile);
            foreach (var blobRarFile in BlobRarFiles)
                await BlobStore.WriteBlob(blobRarFile.Id, blobRarFile);
            foreach (var blobMultipartFile in BlobMultipartFiles)
                await BlobStore.WriteBlob(blobMultipartFile.Id, blobMultipartFile);

            // save db changes
            var addedOrRemovedDavItems = GetAddedOrRemovedDavItems();
            EnqueueRcloneVfsForget(addedOrRemovedDavItems);
            var result = await base.SaveChangesAsync(cancellationToken);

            // clear pending blob writes
            BlobNzbFiles.Clear();
            BlobRarFiles.Clear();
            BlobMultipartFiles.Clear();

            // return
            return result;
        }
        catch
        {
            // on errors, remove any already-written blob files
            foreach (var blobNzbFile in BlobNzbFiles)
                BlobStore.Delete(blobNzbFile.Id);
            foreach (var blobRarFile in BlobRarFiles)
                BlobStore.Delete(blobRarFile.Id);
            foreach (var blobMultipartFile in BlobMultipartFiles)
                BlobStore.Delete(blobMultipartFile.Id);

            // rethrow the exception
            throw;
        }
    }

    private List<DavItem> GetAddedOrRemovedDavItems()
    {
        return ChangeTracker.Entries<DavItem>()
            .Where(x => x.State is EntityState.Added or EntityState.Deleted)
            .Select(x => x.Entity)
            .ToList();
    }

    private static List<string> GetRcloneVfsForgetDirectories(List<DavItem> addedOrRemoved)
    {
        var contentDirs = addedOrRemoved
            .Select(x => x.Path)
            .Select(x => Path.GetDirectoryName(x)!)
            .ToList();

        var idDirs = addedOrRemoved
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Select(x => DatabaseStoreSymlinkFile.GetTargetPath(x.Id))
            .Select(x => Path.GetDirectoryName(x)!)
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
        if (!ShouldQueueRcloneInvalidations()) return;
        if (addedOrRemovedDavItems.Count == 0) return;

        var vfsForgetPaths = GetRcloneVfsForgetDirectories(addedOrRemovedDavItems);
        EnqueueRcloneVfsForgetPaths(vfsForgetPaths);
    }

    public void EnqueueRcloneVfsForgetPaths(IEnumerable<string> paths)
    {
        if (!ShouldQueueRcloneInvalidations()) return;

        var pathList = paths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (pathList.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        RcloneInvalidationItems.AddRange(pathList.Select(path => new RcloneInvalidationItem
        {
            Id = Guid.NewGuid(),
            Path = path,
            CreatedAt = now,
            NextAttemptAt = now
        }));
    }

    public static async Task EnqueueRcloneVfsForgetAsync
    (
        List<DavItem> addedOrRemovedDavItems,
        CancellationToken cancellationToken = default
    )
    {
        if (!ShouldQueueRcloneInvalidations()) return;
        if (addedOrRemovedDavItems.Count == 0) return;

        await using var dbContext = new DavDatabaseContext();
        dbContext.EnqueueRcloneVfsForget(addedOrRemovedDavItems);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task EnqueueRcloneVfsForgetPathsAsync
    (
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default
    )
    {
        if (!ShouldQueueRcloneInvalidations()) return;

        await using var dbContext = new DavDatabaseContext();
        dbContext.EnqueueRcloneVfsForgetPaths(paths);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool ShouldQueueRcloneInvalidations()
    {
        return RcloneClient.IsRemoteControlEnabled && RcloneClient.Host != null;
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
