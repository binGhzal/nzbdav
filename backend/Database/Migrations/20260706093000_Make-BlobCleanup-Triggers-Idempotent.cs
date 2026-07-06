using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260706093000_Make-BlobCleanup-Triggers-Idempotent")]
    public partial class MakeBlobCleanupTriggersIdempotent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            MigrationProvider.DropTrigger(migrationBuilder, "TR_DavItems_Delete_AddBlobCleanup", "DavItems");
            MigrationProvider.DropTrigger(migrationBuilder, "TR_DavItems_Update_AddBlobCleanup", "DavItems");
            MigrationProvider.CreateDavItemsBlobCleanupTriggers(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            MigrationProvider.DropTrigger(migrationBuilder, "TR_DavItems_Delete_AddBlobCleanup", "DavItems");
            MigrationProvider.DropTrigger(migrationBuilder, "TR_DavItems_Update_AddBlobCleanup", "DavItems");

            if (MigrationProvider.IsPostgreSql(migrationBuilder))
            {
                MigrationProvider.CreateDavItemsBlobCleanupTriggers(migrationBuilder);
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
    }
}
