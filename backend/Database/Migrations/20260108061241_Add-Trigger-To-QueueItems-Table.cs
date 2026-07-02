using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggerToQueueItemsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create trigger to automatically add a BlobCleanupItem when a QueueItem is deleted
            MigrationProvider.CreateQueueItemsBlobCleanupTrigger(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            MigrationProvider.DropTrigger(migrationBuilder, "TR_QueueItems_AddBlobCleanup", "QueueItems");
        }
    }
}
