using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    [Migration("20260702142000_Add-QueueSelection-Index")]
    public partial class AddQueueSelectionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority_PauseUntil_CreatedAt",
                table: "QueueItems",
                columns: new[] { "Priority", "PauseUntil", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueueItems_Priority_PauseUntil_CreatedAt",
                table: "QueueItems");
        }
    }
}
