using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddDeleteMountedFilesToHistoryCleanupItemsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            MigrationProvider.DropTrigger(migrationBuilder, "TR_HistoryItems_Delete_AddHistoryCleanup", "HistoryItems");
            
            migrationBuilder.AddColumn<bool>(
                name: "DeleteMountedFiles",
                table: "HistoryCleanupItems",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeleteMountedFiles",
                table: "HistoryCleanupItems");
            
            MigrationProvider.CreateHistoryItemsCleanupTrigger(migrationBuilder);
        }
    }
}
