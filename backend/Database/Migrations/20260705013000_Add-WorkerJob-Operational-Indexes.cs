using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260705013000_Add-WorkerJob-Operational-Indexes")]
    public partial class AddWorkerJobOperationalIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_WorkerJobs_Kind_Status_Priority_AvailableAt_CreatedAt",
                table: "WorkerJobs",
                columns: new[] { "Kind", "Status", "Priority", "AvailableAt", "CreatedAt" },
                descending: new[] { false, false, true, false, false });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerJobs_Kind_Status_LeaseExpiresAt",
                table: "WorkerJobs",
                columns: new[] { "Kind", "Status", "LeaseExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkerJobs_Kind_Status_Priority_AvailableAt_CreatedAt",
                table: "WorkerJobs");

            migrationBuilder.DropIndex(
                name: "IX_WorkerJobs_Kind_Status_LeaseExpiresAt",
                table: "WorkerJobs");
        }
    }
}
