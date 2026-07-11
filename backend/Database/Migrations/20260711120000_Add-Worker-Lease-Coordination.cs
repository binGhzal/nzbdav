using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260711120000_Add-Worker-Lease-Coordination")]
    public partial class AddWorkerLeaseCoordination : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CancelRequestedAt",
                table: "WorkerJobs",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailureKind",
                table: "WorkerJobs",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastHeartbeatAt",
                table: "WorkerJobs",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LeaseGeneration",
                table: "WorkerJobs",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                table: "WorkerJobs",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProgressJson",
                table: "WorkerJobs",
                maxLength: 16384,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProgressUpdatedAt",
                table: "WorkerJobs",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                table: "WorkerJobs",
                maxLength: 16384,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StartedAt",
                table: "WorkerJobs",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerJobs_Status_LeaseExpiresAt_LeaseGeneration",
                table: "WorkerJobs",
                columns: new[] { "Status", "LeaseExpiresAt", "LeaseGeneration" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkerJobs_Status_LeaseExpiresAt_LeaseGeneration",
                table: "WorkerJobs");

            migrationBuilder.DropColumn(name: "CancelRequestedAt", table: "WorkerJobs");
            migrationBuilder.DropColumn(name: "FailureKind", table: "WorkerJobs");
            migrationBuilder.DropColumn(name: "LastHeartbeatAt", table: "WorkerJobs");
            migrationBuilder.DropColumn(name: "LeaseGeneration", table: "WorkerJobs");
            migrationBuilder.DropColumn(name: "LeaseToken", table: "WorkerJobs");
            migrationBuilder.DropColumn(name: "ProgressJson", table: "WorkerJobs");
            migrationBuilder.DropColumn(name: "ProgressUpdatedAt", table: "WorkerJobs");
            migrationBuilder.DropColumn(name: "ResultJson", table: "WorkerJobs");
            migrationBuilder.DropColumn(name: "StartedAt", table: "WorkerJobs");
        }
    }
}
