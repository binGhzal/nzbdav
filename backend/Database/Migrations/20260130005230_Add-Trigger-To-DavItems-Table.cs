using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggerToDavItemsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create trigger to automatically add a BlobCleanupItem when a DavItem with a FileBlobId is deleted
            MigrationProvider.CreateDavItemsBlobCleanupTriggers(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            MigrationProvider.DropTrigger(migrationBuilder, "TR_DavItems_Delete_AddBlobCleanup", "DavItems");
            MigrationProvider.DropTrigger(migrationBuilder, "TR_DavItems_Update_AddBlobCleanup", "DavItems");
        }
    }
}
