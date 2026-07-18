using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.PostgreSqlMigrations;

public partial class PostgreSqlOperationalTriggers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $nzbdav$
            DECLARE
                history_is_exact boolean := false;
            BEGIN
                IF to_regclass(format('%I.%I', current_schema(), '__EFMigrationsHistory_PostgreSql')) IS NOT NULL THEN
                    EXECUTE format(
                        'SELECT count(*) = 1 AND bool_and("MigrationId" = %L AND "ProductVersion" = %L) FROM %I.%I',
                        '20260712000000_PostgreSqlNativeBaseline',
                        '10.0.9',
                        current_schema(),
                        '__EFMigrationsHistory_PostgreSql')
                    INTO history_is_exact;
                END IF;

                IF NOT coalesce(history_is_exact, false) THEN
                    RAISE EXCEPTION 'NZBDav PostgreSQL operational migration requires the exact native baseline prefix';
                END IF;
            END
            $nzbdav$;

            CREATE OR REPLACE FUNCTION "FN_TR_DavItems_Delete_AddBlobCleanup"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF OLD."FileBlobId" IS NOT NULL THEN
                    INSERT INTO "BlobCleanupItems" ("Id")
                    VALUES (OLD."FileBlobId")
                    ON CONFLICT ("Id") DO NOTHING;
                END IF;
                RETURN OLD;
            END
            $function$;
            CREATE TRIGGER "TR_DavItems_Delete_AddBlobCleanup"
            AFTER DELETE ON "DavItems"
            FOR EACH ROW EXECUTE FUNCTION "FN_TR_DavItems_Delete_AddBlobCleanup"();

            CREATE OR REPLACE FUNCTION "FN_TR_DavItems_Update_AddBlobCleanup"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF OLD."FileBlobId" IS NOT NULL
                   AND OLD."FileBlobId" IS DISTINCT FROM NEW."FileBlobId" THEN
                    INSERT INTO "BlobCleanupItems" ("Id")
                    VALUES (OLD."FileBlobId")
                    ON CONFLICT ("Id") DO NOTHING;
                END IF;
                RETURN NEW;
            END
            $function$;
            CREATE TRIGGER "TR_DavItems_Update_AddBlobCleanup"
            AFTER UPDATE OF "FileBlobId" ON "DavItems"
            FOR EACH ROW EXECUTE FUNCTION "FN_TR_DavItems_Update_AddBlobCleanup"();

            CREATE OR REPLACE FUNCTION "FN_TR_DavItems_DeleteDirectory"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF OLD."Type" = 1 THEN
                    INSERT INTO "DavCleanupItems" ("Id")
                    VALUES (OLD."Id")
                    ON CONFLICT ("Id") DO NOTHING;
                END IF;
                RETURN OLD;
            END
            $function$;
            CREATE TRIGGER "TR_DavItems_DeleteDirectory"
            AFTER DELETE ON "DavItems"
            FOR EACH ROW EXECUTE FUNCTION "FN_TR_DavItems_DeleteDirectory"();

            CREATE OR REPLACE FUNCTION "FN_TR_DavItems_Delete_AddNzbBlobCleanup"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF OLD."NzbBlobId" IS NOT NULL THEN
                    INSERT INTO "NzbBlobCleanupItems" ("Id")
                    VALUES (OLD."NzbBlobId")
                    ON CONFLICT ("Id") DO NOTHING;
                END IF;
                RETURN OLD;
            END
            $function$;
            CREATE TRIGGER "TR_DavItems_Delete_AddNzbBlobCleanup"
            AFTER DELETE ON "DavItems"
            FOR EACH ROW EXECUTE FUNCTION "FN_TR_DavItems_Delete_AddNzbBlobCleanup"();

            CREATE OR REPLACE FUNCTION "FN_TR_HistoryItems_Delete_AddNzbBlobCleanup"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF OLD."NzbBlobId" IS NOT NULL THEN
                    INSERT INTO "NzbBlobCleanupItems" ("Id")
                    VALUES (OLD."NzbBlobId")
                    ON CONFLICT ("Id") DO NOTHING;
                END IF;
                RETURN OLD;
            END
            $function$;
            CREATE TRIGGER "TR_HistoryItems_Delete_AddNzbBlobCleanup"
            AFTER DELETE ON "HistoryItems"
            FOR EACH ROW EXECUTE FUNCTION "FN_TR_HistoryItems_Delete_AddNzbBlobCleanup"();

            CREATE OR REPLACE FUNCTION "FN_TR_QueueItems_Delete_AddNzbBlobCleanup"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                INSERT INTO "NzbBlobCleanupItems" ("Id")
                VALUES (OLD."Id")
                ON CONFLICT ("Id") DO NOTHING;
                RETURN OLD;
            END
            $function$;
            CREATE TRIGGER "TR_QueueItems_Delete_AddNzbBlobCleanup"
            AFTER DELETE ON "QueueItems"
            FOR EACH ROW EXECUTE FUNCTION "FN_TR_QueueItems_Delete_AddNzbBlobCleanup"();

            CREATE OR REPLACE FUNCTION "FN_TR_HealthCheckResults_IncrementStats"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                bucket_start bigint := floor(NEW."CreatedAt"::numeric / 86400)::bigint * 86400;
            BEGIN
                INSERT INTO "HealthCheckStats"
                    ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count")
                VALUES
                    (bucket_start, bucket_start + 86400, NEW."Result", NEW."RepairStatus", 1)
                ON CONFLICT ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus")
                DO UPDATE SET "Count" = "HealthCheckStats"."Count" + 1;
                RETURN NEW;
            END
            $function$;
            CREATE TRIGGER "TR_HealthCheckResults_IncrementStats"
            AFTER INSERT ON "HealthCheckResults"
            FOR EACH ROW EXECUTE FUNCTION "FN_TR_HealthCheckResults_IncrementStats"();

            CREATE OR REPLACE FUNCTION "FN_TR_HealthCheckResults_DecrementStats"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                bucket_start bigint := floor(OLD."CreatedAt"::numeric / 86400)::bigint * 86400;
            BEGIN
                UPDATE "HealthCheckStats"
                SET "Count" = "Count" - 1
                WHERE "DateStartInclusive" = bucket_start
                  AND "DateEndExclusive" = bucket_start + 86400
                  AND "Result" = OLD."Result"
                  AND "RepairStatus" = OLD."RepairStatus";
                DELETE FROM "HealthCheckStats"
                WHERE "DateStartInclusive" = bucket_start
                  AND "DateEndExclusive" = bucket_start + 86400
                  AND "Result" = OLD."Result"
                  AND "RepairStatus" = OLD."RepairStatus"
                  AND "Count" <= 0;
                RETURN OLD;
            END
            $function$;
            CREATE TRIGGER "TR_HealthCheckResults_DecrementStats"
            AFTER DELETE ON "HealthCheckResults"
            FOR EACH ROW EXECUTE FUNCTION "FN_TR_HealthCheckResults_DecrementStats"();

            CREATE OR REPLACE FUNCTION "FN_TR_HealthCheckResults_UpdateStats"()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                old_bucket_start bigint := floor(OLD."CreatedAt"::numeric / 86400)::bigint * 86400;
                new_bucket_start bigint := floor(NEW."CreatedAt"::numeric / 86400)::bigint * 86400;
            BEGIN
                IF OLD."CreatedAt" IS DISTINCT FROM NEW."CreatedAt"
                   OR OLD."Result" IS DISTINCT FROM NEW."Result"
                   OR OLD."RepairStatus" IS DISTINCT FROM NEW."RepairStatus" THEN
                    UPDATE "HealthCheckStats"
                    SET "Count" = "Count" - 1
                    WHERE "DateStartInclusive" = old_bucket_start
                      AND "DateEndExclusive" = old_bucket_start + 86400
                      AND "Result" = OLD."Result"
                      AND "RepairStatus" = OLD."RepairStatus";
                    DELETE FROM "HealthCheckStats"
                    WHERE "DateStartInclusive" = old_bucket_start
                      AND "DateEndExclusive" = old_bucket_start + 86400
                      AND "Result" = OLD."Result"
                      AND "RepairStatus" = OLD."RepairStatus"
                      AND "Count" <= 0;
                    INSERT INTO "HealthCheckStats"
                        ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count")
                    VALUES
                        (new_bucket_start, new_bucket_start + 86400, NEW."Result", NEW."RepairStatus", 1)
                    ON CONFLICT ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus")
                    DO UPDATE SET "Count" = "HealthCheckStats"."Count" + 1;
                END IF;
                RETURN NEW;
            END
            $function$;
            CREATE TRIGGER "TR_HealthCheckResults_UpdateStats"
            AFTER UPDATE OF "CreatedAt", "Result", "RepairStatus" ON "HealthCheckResults"
            FOR EACH ROW EXECUTE FUNCTION "FN_TR_HealthCheckResults_UpdateStats"();

            REVOKE ALL ON FUNCTION "FN_TR_DavItems_Delete_AddBlobCleanup"() FROM PUBLIC;
            REVOKE ALL ON FUNCTION "FN_TR_DavItems_Delete_AddNzbBlobCleanup"() FROM PUBLIC;
            REVOKE ALL ON FUNCTION "FN_TR_DavItems_DeleteDirectory"() FROM PUBLIC;
            REVOKE ALL ON FUNCTION "FN_TR_DavItems_Update_AddBlobCleanup"() FROM PUBLIC;
            REVOKE ALL ON FUNCTION "FN_TR_HealthCheckResults_DecrementStats"() FROM PUBLIC;
            REVOKE ALL ON FUNCTION "FN_TR_HealthCheckResults_IncrementStats"() FROM PUBLIC;
            REVOKE ALL ON FUNCTION "FN_TR_HealthCheckResults_UpdateStats"() FROM PUBLIC;
            REVOKE ALL ON FUNCTION "FN_TR_HistoryItems_Delete_AddNzbBlobCleanup"() FROM PUBLIC;
            REVOKE ALL ON FUNCTION "FN_TR_QueueItems_Delete_AddNzbBlobCleanup"() FROM PUBLIC;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS "TR_HealthCheckResults_UpdateStats" ON "HealthCheckResults";
            DROP FUNCTION IF EXISTS "FN_TR_HealthCheckResults_UpdateStats"();
            DROP TRIGGER IF EXISTS "TR_HealthCheckResults_DecrementStats" ON "HealthCheckResults";
            DROP FUNCTION IF EXISTS "FN_TR_HealthCheckResults_DecrementStats"();
            DROP TRIGGER IF EXISTS "TR_HealthCheckResults_IncrementStats" ON "HealthCheckResults";
            DROP FUNCTION IF EXISTS "FN_TR_HealthCheckResults_IncrementStats"();
            DROP TRIGGER IF EXISTS "TR_QueueItems_Delete_AddNzbBlobCleanup" ON "QueueItems";
            DROP FUNCTION IF EXISTS "FN_TR_QueueItems_Delete_AddNzbBlobCleanup"();
            DROP TRIGGER IF EXISTS "TR_HistoryItems_Delete_AddNzbBlobCleanup" ON "HistoryItems";
            DROP FUNCTION IF EXISTS "FN_TR_HistoryItems_Delete_AddNzbBlobCleanup"();
            DROP TRIGGER IF EXISTS "TR_DavItems_Delete_AddNzbBlobCleanup" ON "DavItems";
            DROP FUNCTION IF EXISTS "FN_TR_DavItems_Delete_AddNzbBlobCleanup"();
            DROP TRIGGER IF EXISTS "TR_DavItems_DeleteDirectory" ON "DavItems";
            DROP FUNCTION IF EXISTS "FN_TR_DavItems_DeleteDirectory"();
            DROP TRIGGER IF EXISTS "TR_DavItems_Update_AddBlobCleanup" ON "DavItems";
            DROP FUNCTION IF EXISTS "FN_TR_DavItems_Update_AddBlobCleanup"();
            DROP TRIGGER IF EXISTS "TR_DavItems_Delete_AddBlobCleanup" ON "DavItems";
            DROP FUNCTION IF EXISTS "FN_TR_DavItems_Delete_AddBlobCleanup"();
            """);
    }
}
