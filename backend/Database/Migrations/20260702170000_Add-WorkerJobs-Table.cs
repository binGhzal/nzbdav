using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260702170000_Add-WorkerJobs-Table")]
    public partial class AddWorkerJobsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkerJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Kind = table.Column<int>(nullable: false),
                    Status = table.Column<int>(nullable: false),
                    TargetId = table.Column<Guid>(nullable: false),
                    Priority = table.Column<int>(nullable: false),
                    Attempts = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<long>(nullable: false),
                    UpdatedAt = table.Column<long>(nullable: false),
                    AvailableAt = table.Column<long>(nullable: false),
                    LeaseExpiresAt = table.Column<long>(nullable: true),
                    CompletedAt = table.Column<long>(nullable: true),
                    LeaseOwner = table.Column<string>(maxLength: 255, nullable: true),
                    LastError = table.Column<string>(maxLength: 1024, nullable: true),
                    PayloadJson = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerJobs_Kind_Status_AvailableAt_LeaseExpiresAt_Priority_CreatedAt",
                table: "WorkerJobs",
                columns: new[] { "Kind", "Status", "AvailableAt", "LeaseExpiresAt", "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerJobs_Kind_TargetId",
                table: "WorkerJobs",
                columns: new[] { "Kind", "TargetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerJobs");
        }
    }
}
