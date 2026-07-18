using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260712113000_Add-Maintenance-Runs")]
    public partial class AddMaintenanceRuns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Kind = table.Column<int>(nullable: false),
                    Status = table.Column<int>(nullable: false),
                    ActiveSlot = table.Column<int>(nullable: true),
                    RequestedBy = table.Column<string>(maxLength: 32, nullable: false),
                    CreatedAt = table.Column<long>(nullable: false),
                    StartedAt = table.Column<long>(nullable: true),
                    UpdatedAt = table.Column<long>(nullable: false),
                    CompletedAt = table.Column<long>(nullable: true),
                    CancellationRequestedAt = table.Column<long>(nullable: true),
                    ProgressCurrent = table.Column<int>(nullable: false),
                    ProgressTotal = table.Column<int>(nullable: true),
                    Message = table.Column<string>(maxLength: 2048, nullable: true),
                    Error = table.Column<string>(maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRuns_ActiveSlot",
                table: "MaintenanceRuns",
                column: "ActiveSlot",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRuns_Kind_CreatedAt",
                table: "MaintenanceRuns",
                columns: new[] { "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRuns_Status_CreatedAt",
                table: "MaintenanceRuns",
                columns: new[] { "Status", "CreatedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MaintenanceRuns");
        }
    }
}
