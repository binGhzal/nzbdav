using Microsoft.EntityFrameworkCore.Migrations;

namespace NzbWebDAV.Database.Migrations;

internal static class MigrationProvider
{
    public const string PostgreSql = "Npgsql.EntityFrameworkCore.PostgreSQL";
    public const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";

    public static bool IsPostgreSql(MigrationBuilder migrationBuilder) =>
        migrationBuilder.ActiveProvider == PostgreSql;

    public static bool IsSqlite(MigrationBuilder migrationBuilder) =>
        migrationBuilder.ActiveProvider == Sqlite;

    public static void DropTrigger(
        MigrationBuilder migrationBuilder,
        string triggerName,
        string tableName,
        string? functionName = null)
    {
        if (IsPostgreSql(migrationBuilder))
        {
            functionName ??= $"FN_{triggerName}";
            migrationBuilder.Sql($"""DROP TRIGGER IF EXISTS "{triggerName}" ON "{tableName}";""");
            migrationBuilder.Sql($"""DROP FUNCTION IF EXISTS "{functionName}"();""");
            return;
        }

        migrationBuilder.Sql($"""DROP TRIGGER IF EXISTS {triggerName};""");
    }

    public static void CreateQueueItemsBlobCleanupTrigger(MigrationBuilder migrationBuilder)
    {
        if (IsPostgreSql(migrationBuilder))
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION "FN_TR_QueueItems_AddBlobCleanup"()
                RETURNS trigger AS $$
                BEGIN
                    INSERT INTO "BlobCleanupItems" ("Id")
                    VALUES (OLD."Id")
                    ON CONFLICT DO NOTHING;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "TR_QueueItems_AddBlobCleanup"
                AFTER DELETE ON "QueueItems"
                FOR EACH ROW
                EXECUTE FUNCTION "FN_TR_QueueItems_AddBlobCleanup"();
                """);
            return;
        }

        migrationBuilder.Sql(
            """
            CREATE TRIGGER TR_QueueItems_AddBlobCleanup
            AFTER DELETE ON QueueItems
            BEGIN
                INSERT INTO BlobCleanupItems (Id)
                VALUES (OLD.Id);
            END
            """);
    }

    public static void CreateQueueItemsNzbBlobCleanupTrigger(MigrationBuilder migrationBuilder)
    {
        if (IsPostgreSql(migrationBuilder))
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION "FN_TR_QueueItems_AddNzbBlobCleanup"()
                RETURNS trigger AS $$
                BEGIN
                    INSERT INTO "NzbBlobCleanupItems" ("Id")
                    VALUES (OLD."Id")
                    ON CONFLICT DO NOTHING;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "TR_QueueItems_AddNzbBlobCleanup"
                AFTER DELETE ON "QueueItems"
                FOR EACH ROW
                EXECUTE FUNCTION "FN_TR_QueueItems_AddNzbBlobCleanup"();
                """);
            return;
        }

        migrationBuilder.Sql(
            """
            CREATE TRIGGER TR_QueueItems_AddNzbBlobCleanup
            AFTER DELETE ON QueueItems
            BEGIN
                INSERT OR IGNORE INTO NzbBlobCleanupItems (Id)
                VALUES (OLD.Id);
            END
            """);
    }

    public static void CreateDavItemsBlobCleanupTriggers(MigrationBuilder migrationBuilder)
    {
        if (IsPostgreSql(migrationBuilder))
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION "FN_TR_DavItems_Delete_AddBlobCleanup"()
                RETURNS trigger AS $$
                BEGIN
                    INSERT INTO "BlobCleanupItems" ("Id")
                    VALUES (OLD."FileBlobId")
                    ON CONFLICT DO NOTHING;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "TR_DavItems_Delete_AddBlobCleanup"
                AFTER DELETE ON "DavItems"
                FOR EACH ROW
                WHEN (OLD."FileBlobId" IS NOT NULL)
                EXECUTE FUNCTION "FN_TR_DavItems_Delete_AddBlobCleanup"();

                CREATE OR REPLACE FUNCTION "FN_TR_DavItems_Update_AddBlobCleanup"()
                RETURNS trigger AS $$
                BEGIN
                    INSERT INTO "BlobCleanupItems" ("Id")
                    VALUES (OLD."FileBlobId")
                    ON CONFLICT DO NOTHING;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "TR_DavItems_Update_AddBlobCleanup"
                AFTER UPDATE OF "FileBlobId" ON "DavItems"
                FOR EACH ROW
                WHEN (OLD."FileBlobId" IS NOT NULL AND OLD."FileBlobId" IS DISTINCT FROM NEW."FileBlobId")
                EXECUTE FUNCTION "FN_TR_DavItems_Update_AddBlobCleanup"();
                """);
            return;
        }

        migrationBuilder.Sql(
            """
            CREATE TRIGGER TR_DavItems_Delete_AddBlobCleanup
            AFTER DELETE ON DavItems
            WHEN OLD.FileBlobId IS NOT NULL
            BEGIN
                INSERT INTO BlobCleanupItems (Id)
                VALUES (OLD.FileBlobId);
            END
            """);

        migrationBuilder.Sql(
            """
            CREATE TRIGGER TR_DavItems_Update_AddBlobCleanup
            AFTER UPDATE OF FileBlobId ON DavItems
            WHEN OLD.FileBlobId IS NOT NULL AND OLD.FileBlobId != NEW.FileBlobId
            BEGIN
                INSERT INTO BlobCleanupItems (Id)
                VALUES (OLD.FileBlobId);
            END
            """);
    }

    public static void CreateDavItemsDirectoryCleanupTrigger(MigrationBuilder migrationBuilder)
    {
        if (IsPostgreSql(migrationBuilder))
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION "FN_TR_DavItems_DeleteDirectory"()
                RETURNS trigger AS $$
                BEGIN
                    INSERT INTO "DavCleanupItems" ("Id")
                    VALUES (OLD."Id")
                    ON CONFLICT DO NOTHING;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "TR_DavItems_DeleteDirectory"
                AFTER DELETE ON "DavItems"
                FOR EACH ROW
                WHEN (OLD."SubType" = 101)
                EXECUTE FUNCTION "FN_TR_DavItems_DeleteDirectory"();
                """);
            return;
        }

        migrationBuilder.Sql(
            """
            CREATE TRIGGER TR_DavItems_DeleteDirectory
            AFTER DELETE ON DavItems
            WHEN OLD.SubType = 101
            BEGIN
                INSERT INTO DavCleanupItems (Id)
                VALUES (OLD.Id);
            END
            """);
    }

    public static void CreateHistoryItemsCleanupTrigger(MigrationBuilder migrationBuilder)
    {
        if (IsPostgreSql(migrationBuilder))
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION "FN_TR_HistoryItems_Delete_AddHistoryCleanup"()
                RETURNS trigger AS $$
                BEGIN
                    INSERT INTO "HistoryCleanupItems" ("Id")
                    VALUES (OLD."Id")
                    ON CONFLICT DO NOTHING;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "TR_HistoryItems_Delete_AddHistoryCleanup"
                AFTER DELETE ON "HistoryItems"
                FOR EACH ROW
                EXECUTE FUNCTION "FN_TR_HistoryItems_Delete_AddHistoryCleanup"();
                """);
            return;
        }

        migrationBuilder.Sql(
            """
            CREATE TRIGGER TR_HistoryItems_Delete_AddHistoryCleanup
            AFTER DELETE ON HistoryItems
            BEGIN
                INSERT INTO HistoryCleanupItems (Id)
                VALUES (OLD.Id);
            END
            """);
    }

    public static void CreateHistoryItemsNzbBlobCleanupTrigger(MigrationBuilder migrationBuilder)
    {
        if (IsPostgreSql(migrationBuilder))
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION "FN_TR_HistoryItems_Delete_AddNzbBlobCleanup"()
                RETURNS trigger AS $$
                BEGIN
                    INSERT INTO "NzbBlobCleanupItems" ("Id")
                    VALUES (OLD."NzbBlobId")
                    ON CONFLICT DO NOTHING;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "TR_HistoryItems_Delete_AddNzbBlobCleanup"
                AFTER DELETE ON "HistoryItems"
                FOR EACH ROW
                WHEN (OLD."NzbBlobId" IS NOT NULL)
                EXECUTE FUNCTION "FN_TR_HistoryItems_Delete_AddNzbBlobCleanup"();
                """);
            return;
        }

        migrationBuilder.Sql(
            """
            CREATE TRIGGER TR_HistoryItems_Delete_AddNzbBlobCleanup
            AFTER DELETE ON HistoryItems
            WHEN OLD.NzbBlobId IS NOT NULL
            BEGIN
                INSERT OR IGNORE INTO NzbBlobCleanupItems (Id)
                VALUES (OLD.NzbBlobId);
            END
            """);
    }

    public static void CreateDavItemsNzbBlobCleanupTrigger(MigrationBuilder migrationBuilder)
    {
        if (IsPostgreSql(migrationBuilder))
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION "FN_TR_DavItems_Delete_AddNzbBlobCleanup"()
                RETURNS trigger AS $$
                BEGIN
                    INSERT INTO "NzbBlobCleanupItems" ("Id")
                    VALUES (OLD."NzbBlobId")
                    ON CONFLICT DO NOTHING;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "TR_DavItems_Delete_AddNzbBlobCleanup"
                AFTER DELETE ON "DavItems"
                FOR EACH ROW
                WHEN (OLD."NzbBlobId" IS NOT NULL)
                EXECUTE FUNCTION "FN_TR_DavItems_Delete_AddNzbBlobCleanup"();
                """);
            return;
        }

        migrationBuilder.Sql(
            """
            CREATE TRIGGER TR_DavItems_Delete_AddNzbBlobCleanup
            AFTER DELETE ON DavItems
            WHEN OLD.NzbBlobId IS NOT NULL
            BEGIN
                INSERT OR IGNORE INTO NzbBlobCleanupItems (Id)
                VALUES (OLD.NzbBlobId);
            END
            """);
    }
}
