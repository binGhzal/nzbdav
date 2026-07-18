using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Database;

internal static class PostgreSqlModelConfiguration
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        ConfigureLocalWall(
            modelBuilder.Entity<DavItem>().Property(item => item.CreatedAt),
            "DavItem.CreatedAt");
        ConfigureLocalWall(
            modelBuilder.Entity<HistoryItem>().Property(item => item.CreatedAt),
            "HistoryItem.CreatedAt");
        ConfigureLocalWall(
            modelBuilder.Entity<QueueItem>().Property(item => item.CreatedAt),
            "QueueItem.CreatedAt");
        ConfigureNullableLocalWall(
            modelBuilder.Entity<QueueItem>().Property(item => item.PauseUntil),
            "QueueItem.PauseUntil");

        // PostgreSQL's default collation is deployment-dependent. Only strings
        // that participate in application keys or indexes use bytewise C
        // ordering so equality and ordering stay deterministic across hosts.
        UseC(modelBuilder.Entity<Account>().Property(item => item.Username));
        UseC(modelBuilder.Entity<DavItem>().Property(item => item.Name));
        UseC(modelBuilder.Entity<DavItem>().Property(item => item.IdPrefix));
        UseC(modelBuilder.Entity<QueueItem>().Property(item => item.Category));
        UseC(modelBuilder.Entity<QueueItem>().Property(item => item.FileName));
        UseC(modelBuilder.Entity<HistoryItem>().Property(item => item.Category));
        UseC(modelBuilder.Entity<ConfigItem>().Property(item => item.ConfigName));
        UseC(modelBuilder.Entity<RcloneInvalidationItem>().Property(item => item.Path));

        UseC(modelBuilder.Entity<ArrDownloadCorrelation>().Property(item => item.ArrApp));
        UseC(modelBuilder.Entity<ArrDownloadCorrelation>().Property(item => item.InstanceKey));
        UseC(modelBuilder.Entity<ArrDownloadCorrelation>().Property(item => item.DownloadId));
        UseC(modelBuilder.Entity<ArrDownloadCorrelation>().Property(item => item.MediaKey));
        UseC(modelBuilder.Entity<ArrDownloadCorrelation>().Property(item => item.Source));

        UseC(modelBuilder.Entity<ArrSearchNudgeCommand>().Property(item => item.ArrApp));
        UseC(modelBuilder.Entity<ArrSearchNudgeCommand>().Property(item => item.InstanceKey));
        UseC(modelBuilder.Entity<ArrSearchNudgeCommand>().Property(item => item.Status));
        UseC(modelBuilder.Entity<ArrSearchNudgeCommand>().Property(item => item.CooldownKey));

        UseC(modelBuilder.Entity<ArrDownloadLifecycleEvent>().Property(item => item.ArrApp));
        UseC(modelBuilder.Entity<ArrDownloadLifecycleEvent>().Property(item => item.InstanceKey));
        UseC(modelBuilder.Entity<ArrDownloadLifecycleEvent>().Property(item => item.State));

        modelBuilder.Entity<ArrDownloadLifecycleEvent>()
            .HasIndex(item => new { item.ArrApp, item.InstanceKey, item.State, item.CreatedAt })
            .HasDatabaseName("IX_ArrLifecycle_Instance_State_CreatedAt");

        modelBuilder.Entity<WorkerJob>()
            .HasIndex(item => new
            {
                item.Kind,
                item.Status,
                item.AvailableAt,
                item.LeaseExpiresAt,
                item.Priority,
                item.CreatedAt
            })
            .HasDatabaseName("IX_WorkerJobs_ClaimOrder");
    }

    private static void UseC<TProperty>(PropertyBuilder<TProperty> property) =>
        property.UseCollation("C");

    private static void ConfigureLocalWall(
        PropertyBuilder<DateTime> property,
        string field)
    {
        property
            .HasConversion(new ValueConverter<DateTime, DateTime>(
                value => ValidateLocalWall(value, field),
                value => DateTime.SpecifyKind(value, DateTimeKind.Unspecified)))
            .HasColumnType("timestamp without time zone");
    }

    private static void ConfigureNullableLocalWall(
        PropertyBuilder<DateTime?> property,
        string field)
    {
        property
            .HasConversion(new ValueConverter<DateTime?, DateTime?>(
                value => value.HasValue ? ValidateLocalWall(value.Value, field) : null,
                value => value.HasValue
                    ? DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified)
                    : null))
            .HasColumnType("timestamp without time zone");
    }

    private static DateTime ValidateLocalWall(DateTime value, string field)
    {
        if (value.Kind != DateTimeKind.Unspecified || value.Ticks % 10 != 0)
            throw new InvalidOperationException(
                $"{field} must use DateTimeKind.Unspecified and whole PostgreSQL microseconds (Ticks % 10 == 0).");
        return value;
    }
}
