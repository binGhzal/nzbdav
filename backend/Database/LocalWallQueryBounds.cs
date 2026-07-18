using Microsoft.EntityFrameworkCore;

namespace NzbWebDAV.Database;

internal static class LocalWallQueryBounds
{
    internal static DateTime NormalizeInclusiveLowerBound(
        DavDatabaseContext context,
        DateTime localWallInstant)
        => NormalizeCeiling(context, localWallInstant, "inclusive local-wall lower bound");

    internal static DateTime NormalizeExclusiveUpperBound(
        DavDatabaseContext context,
        DateTime localWallInstant)
        => NormalizeCeiling(context, localWallInstant, "exclusive local-wall upper bound");

    internal static DateTime NormalizeExclusiveLowerBound(
        DavDatabaseContext context,
        DateTime localWallInstant)
        => NormalizeFloor(context, localWallInstant);

    private static DateTime NormalizeCeiling(
        DavDatabaseContext context,
        DateTime localWallInstant,
        string boundaryName)
    {
        ArgumentNullException.ThrowIfNull(context);
        var normalized = DateTime.SpecifyKind(localWallInstant, DateTimeKind.Unspecified);
        if (!context.Database.IsNpgsql()) return normalized;

        var remainder = normalized.Ticks % 10;
        if (remainder == 0) return normalized;
        var adjustment = 10 - remainder;
        if (normalized.Ticks > DateTime.MaxValue.Ticks - adjustment)
            throw new ArgumentOutOfRangeException(
                nameof(localWallInstant),
                $"The {boundaryName} cannot be represented at PostgreSQL microsecond precision.");
        return new DateTime(normalized.Ticks + adjustment, DateTimeKind.Unspecified);
    }

    internal static DateTime NormalizeInclusiveUpperBound(
        DavDatabaseContext context,
        DateTime localWallInstant)
        => NormalizeFloor(context, localWallInstant);

    private static DateTime NormalizeFloor(
        DavDatabaseContext context,
        DateTime localWallInstant)
    {
        ArgumentNullException.ThrowIfNull(context);
        var normalized = DateTime.SpecifyKind(localWallInstant, DateTimeKind.Unspecified);
        if (!context.Database.IsNpgsql()) return normalized;

        return new DateTime(normalized.Ticks - normalized.Ticks % 10, DateTimeKind.Unspecified);
    }

    internal static DateTime NormalizeExactBoundary(
        DavDatabaseContext context,
        DateTime localWallBoundary)
    {
        ArgumentNullException.ThrowIfNull(context);
        return DateTime.SpecifyKind(localWallBoundary, DateTimeKind.Unspecified);
    }
}
