using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddNzbBlobIdAndNzbNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "NzbBlobId",
                table: "HistoryItems",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "NzbBlobId",
                table: "DavItems",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NzbBlobCleanupItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NzbBlobCleanupItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NzbNames",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    FileName = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NzbNames", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_NzbBlobId",
                table: "HistoryItems",
                column: "NzbBlobId");

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_NzbBlobId",
                table: "DavItems",
                column: "NzbBlobId");

            // Replace the old QueueItems trigger (which inserted into BlobCleanupItems)
            // with a new one that inserts into NzbBlobCleanupItems instead.
            MigrationProvider.DropTrigger(migrationBuilder, "TR_QueueItems_AddBlobCleanup", "QueueItems");

            MigrationProvider.CreateQueueItemsNzbBlobCleanupTrigger(migrationBuilder);

            // When a HistoryItem is deleted, schedule its NZB blob for cleanup.
            MigrationProvider.CreateHistoryItemsNzbBlobCleanupTrigger(migrationBuilder);

            // When a DavItem is deleted, schedule its NZB blob for cleanup.
            // INSERT OR IGNORE handles the case where multiple DavItems share the
            // same NzbBlobId (all files from the same download job).
            MigrationProvider.CreateDavItemsNzbBlobCleanupTrigger(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            MigrationProvider.DropTrigger(migrationBuilder, "TR_DavItems_Delete_AddNzbBlobCleanup", "DavItems");
            MigrationProvider.DropTrigger(migrationBuilder, "TR_HistoryItems_Delete_AddNzbBlobCleanup", "HistoryItems");
            MigrationProvider.DropTrigger(migrationBuilder, "TR_QueueItems_AddNzbBlobCleanup", "QueueItems");

            // Restore the original QueueItems trigger
            MigrationProvider.CreateQueueItemsBlobCleanupTrigger(migrationBuilder);

            migrationBuilder.DropTable(
                name: "NzbBlobCleanupItems");

            migrationBuilder.DropTable(
                name: "NzbNames");

            migrationBuilder.DropIndex(
                name: "IX_HistoryItems_NzbBlobId",
                table: "HistoryItems");

            migrationBuilder.DropIndex(
                name: "IX_DavItems_NzbBlobId",
                table: "DavItems");

            migrationBuilder.DropColumn(
                name: "NzbBlobId",
                table: "HistoryItems");

            migrationBuilder.DropColumn(
                name: "NzbBlobId",
                table: "DavItems");
        }
    }
}
