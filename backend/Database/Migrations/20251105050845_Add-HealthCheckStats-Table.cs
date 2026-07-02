using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthCheckStatsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HealthCheckStats",
                columns: table => new
                {
                    DateStartInclusive = table.Column<long>(nullable: false),
                    DateEndExclusive = table.Column<long>(nullable: false),
                    Result = table.Column<int>(nullable: false),
                    RepairStatus = table.Column<int>(nullable: false),
                    Count = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckStats", x => new { x.DateStartInclusive, x.DateEndExclusive, x.Result, x.RepairStatus });
                });

            // Populate HealthCheckStats from HealthCheckResults
            // Group by day (truncated to beginning of day in UTC), Result, and RepairStatus
            if (MigrationProvider.IsPostgreSql(migrationBuilder))
            {
                migrationBuilder.Sql(
                    """
                    INSERT INTO "HealthCheckStats" ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count")
                    SELECT
                        ("CreatedAt" / 86400) * 86400 AS "DateStartInclusive",
                        (("CreatedAt" / 86400) * 86400) + 86400 AS "DateEndExclusive",
                        "Result",
                        "RepairStatus",
                        COUNT(*)::integer AS "Count"
                    FROM "HealthCheckResults"
                    GROUP BY
                        ("CreatedAt" / 86400) * 86400,
                        (("CreatedAt" / 86400) * 86400) + 86400,
                        "Result",
                        "RepairStatus"
                    """);

                migrationBuilder.Sql(
                    """
                    CREATE OR REPLACE FUNCTION "FN_TR_HealthCheckResults_IncrementStats"()
                    RETURNS trigger AS $$
                    DECLARE
                        start_seconds bigint := (NEW."CreatedAt" / 86400) * 86400;
                        end_seconds bigint := ((NEW."CreatedAt" / 86400) * 86400) + 86400;
                    BEGIN
                        INSERT INTO "HealthCheckStats" ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count")
                        VALUES (start_seconds, end_seconds, NEW."Result", NEW."RepairStatus", 1)
                        ON CONFLICT ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus") DO UPDATE SET
                            "Count" = "HealthCheckStats"."Count" + 1;
                        RETURN NEW;
                    END;
                    $$ LANGUAGE plpgsql;

                    CREATE TRIGGER "TR_HealthCheckResults_IncrementStats"
                    AFTER INSERT ON "HealthCheckResults"
                    FOR EACH ROW
                    EXECUTE FUNCTION "FN_TR_HealthCheckResults_IncrementStats"();
                    """);

                migrationBuilder.Sql(
                    """
                    CREATE OR REPLACE FUNCTION "FN_TR_HealthCheckResults_DecrementStats"()
                    RETURNS trigger AS $$
                    DECLARE
                        start_seconds bigint := (OLD."CreatedAt" / 86400) * 86400;
                        end_seconds bigint := ((OLD."CreatedAt" / 86400) * 86400) + 86400;
                    BEGIN
                        UPDATE "HealthCheckStats"
                        SET "Count" = "Count" - 1
                        WHERE "DateStartInclusive" = start_seconds
                          AND "DateEndExclusive" = end_seconds
                          AND "Result" = OLD."Result"
                          AND "RepairStatus" = OLD."RepairStatus";

                        DELETE FROM "HealthCheckStats"
                        WHERE "DateStartInclusive" = start_seconds
                          AND "DateEndExclusive" = end_seconds
                          AND "Result" = OLD."Result"
                          AND "RepairStatus" = OLD."RepairStatus"
                          AND "Count" <= 0;

                        RETURN OLD;
                    END;
                    $$ LANGUAGE plpgsql;

                    CREATE TRIGGER "TR_HealthCheckResults_DecrementStats"
                    AFTER DELETE ON "HealthCheckResults"
                    FOR EACH ROW
                    EXECUTE FUNCTION "FN_TR_HealthCheckResults_DecrementStats"();
                    """);

                migrationBuilder.Sql(
                    """
                    CREATE OR REPLACE FUNCTION "FN_TR_HealthCheckResults_UpdateStats"()
                    RETURNS trigger AS $$
                    DECLARE
                        old_start_seconds bigint := (OLD."CreatedAt" / 86400) * 86400;
                        old_end_seconds bigint := ((OLD."CreatedAt" / 86400) * 86400) + 86400;
                        new_start_seconds bigint := (NEW."CreatedAt" / 86400) * 86400;
                        new_end_seconds bigint := ((NEW."CreatedAt" / 86400) * 86400) + 86400;
                    BEGIN
                        UPDATE "HealthCheckStats"
                        SET "Count" = "Count" - 1
                        WHERE "DateStartInclusive" = old_start_seconds
                          AND "DateEndExclusive" = old_end_seconds
                          AND "Result" = OLD."Result"
                          AND "RepairStatus" = OLD."RepairStatus";

                        DELETE FROM "HealthCheckStats"
                        WHERE "DateStartInclusive" = old_start_seconds
                          AND "DateEndExclusive" = old_end_seconds
                          AND "Result" = OLD."Result"
                          AND "RepairStatus" = OLD."RepairStatus"
                          AND "Count" <= 0;

                        INSERT INTO "HealthCheckStats" ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count")
                        VALUES (new_start_seconds, new_end_seconds, NEW."Result", NEW."RepairStatus", 1)
                        ON CONFLICT ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus") DO UPDATE SET
                            "Count" = "HealthCheckStats"."Count" + 1;

                        RETURN NEW;
                    END;
                    $$ LANGUAGE plpgsql;

                    CREATE TRIGGER "TR_HealthCheckResults_UpdateStats"
                    AFTER UPDATE ON "HealthCheckResults"
                    FOR EACH ROW
                    WHEN (OLD."Result" <> NEW."Result" OR OLD."RepairStatus" <> NEW."RepairStatus")
                    EXECUTE FUNCTION "FN_TR_HealthCheckResults_UpdateStats"();
                    """);
            }
            else
            {
                migrationBuilder.Sql(
                    """
                    INSERT INTO HealthCheckStats (DateStartInclusive, DateEndExclusive, Result, RepairStatus, Count)
                    SELECT
                        CAST(strftime('%s', date(datetime(CreatedAt, 'unixepoch')) || ' 00:00:00') AS INTEGER) AS DateStartInclusive,
                        CAST(strftime('%s', date(datetime(CreatedAt, 'unixepoch'), '+1 day') || ' 00:00:00') AS INTEGER) AS DateEndExclusive,
                        Result,
                        RepairStatus,
                        COUNT(*) AS Count
                    FROM HealthCheckResults
                    GROUP BY
                        CAST(strftime('%s', date(datetime(CreatedAt, 'unixepoch')) || ' 00:00:00') AS INTEGER),
                        CAST(strftime('%s', date(datetime(CreatedAt, 'unixepoch'), '+1 day') || ' 00:00:00') AS INTEGER),
                        Result,
                        RepairStatus
                    """
                );

                migrationBuilder.Sql(
                    """
                    CREATE TRIGGER TR_HealthCheckResults_IncrementStats
                    AFTER INSERT ON HealthCheckResults
                    BEGIN
                        INSERT INTO HealthCheckStats (DateStartInclusive, DateEndExclusive, Result, RepairStatus, Count)
                        VALUES (
                            CAST(strftime('%s', date(datetime(NEW.CreatedAt, 'unixepoch')) || ' 00:00:00') AS INTEGER),
                            CAST(strftime('%s', date(datetime(NEW.CreatedAt, 'unixepoch'), '+1 day') || ' 00:00:00') AS INTEGER),
                            NEW.Result,
                            NEW.RepairStatus,
                            1
                        )
                        ON CONFLICT(DateStartInclusive, DateEndExclusive, Result, RepairStatus) DO UPDATE SET
                            Count = Count + 1;
                    END
                    """
                );

                migrationBuilder.Sql(
                    """
                    CREATE TRIGGER TR_HealthCheckResults_DecrementStats
                    AFTER DELETE ON HealthCheckResults
                    BEGIN
                        UPDATE HealthCheckStats
                        SET Count = Count - 1
                        WHERE DateStartInclusive = CAST(strftime('%s', date(datetime(OLD.CreatedAt, 'unixepoch')) || ' 00:00:00') AS INTEGER)
                          AND DateEndExclusive = CAST(strftime('%s', date(datetime(OLD.CreatedAt, 'unixepoch'), '+1 day') || ' 00:00:00') AS INTEGER)
                          AND Result = OLD.Result
                          AND RepairStatus = OLD.RepairStatus;

                        DELETE FROM HealthCheckStats
                        WHERE DateStartInclusive = CAST(strftime('%s', date(datetime(OLD.CreatedAt, 'unixepoch')) || ' 00:00:00') AS INTEGER)
                          AND DateEndExclusive = CAST(strftime('%s', date(datetime(OLD.CreatedAt, 'unixepoch'), '+1 day') || ' 00:00:00') AS INTEGER)
                          AND Result = OLD.Result
                          AND RepairStatus = OLD.RepairStatus
                          AND Count <= 0;
                    END
                    """
                );

                migrationBuilder.Sql(
                    """
                    CREATE TRIGGER TR_HealthCheckResults_UpdateStats
                    AFTER UPDATE ON HealthCheckResults
                    WHEN OLD.Result != NEW.Result OR OLD.RepairStatus != NEW.RepairStatus
                    BEGIN
                        UPDATE HealthCheckStats
                        SET Count = Count - 1
                        WHERE DateStartInclusive = CAST(strftime('%s', date(datetime(OLD.CreatedAt, 'unixepoch')) || ' 00:00:00') AS INTEGER)
                          AND DateEndExclusive = CAST(strftime('%s', date(datetime(OLD.CreatedAt, 'unixepoch'), '+1 day') || ' 00:00:00') AS INTEGER)
                          AND Result = OLD.Result
                          AND RepairStatus = OLD.RepairStatus;

                        DELETE FROM HealthCheckStats
                        WHERE DateStartInclusive = CAST(strftime('%s', date(datetime(OLD.CreatedAt, 'unixepoch')) || ' 00:00:00') AS INTEGER)
                          AND DateEndExclusive = CAST(strftime('%s', date(datetime(OLD.CreatedAt, 'unixepoch'), '+1 day') || ' 00:00:00') AS INTEGER)
                          AND Result = OLD.Result
                          AND RepairStatus = OLD.RepairStatus
                          AND Count <= 0;

                        INSERT INTO HealthCheckStats (DateStartInclusive, DateEndExclusive, Result, RepairStatus, Count)
                        VALUES (
                            CAST(strftime('%s', date(datetime(NEW.CreatedAt, 'unixepoch')) || ' 00:00:00') AS INTEGER),
                            CAST(strftime('%s', date(datetime(NEW.CreatedAt, 'unixepoch'), '+1 day') || ' 00:00:00') AS INTEGER),
                            NEW.Result,
                            NEW.RepairStatus,
                            1
                        )
                        ON CONFLICT(DateStartInclusive, DateEndExclusive, Result, RepairStatus) DO UPDATE SET
                            Count = Count + 1;
                    END
                    """
                );
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            MigrationProvider.DropTrigger(
                migrationBuilder,
                "TR_HealthCheckResults_UpdateStats",
                "HealthCheckResults");
            MigrationProvider.DropTrigger(
                migrationBuilder,
                "TR_HealthCheckResults_DecrementStats",
                "HealthCheckResults");
            MigrationProvider.DropTrigger(
                migrationBuilder,
                "TR_HealthCheckResults_IncrementStats",
                "HealthCheckResults");

            migrationBuilder.DropTable(
                name: "HealthCheckStats");
        }
    }
}
