using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260705023000_Make-DavCleanup-Trigger-Idempotent")]
    public partial class MakeDavCleanupTriggerIdempotent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            MigrationProvider.DropTrigger(migrationBuilder, "TR_DavItems_DeleteDirectory", "DavItems");
            MigrationProvider.CreateDavItemsDirectoryCleanupTrigger(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            MigrationProvider.DropTrigger(migrationBuilder, "TR_DavItems_DeleteDirectory", "DavItems");

            if (MigrationProvider.IsPostgreSql(migrationBuilder))
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
    }
}
