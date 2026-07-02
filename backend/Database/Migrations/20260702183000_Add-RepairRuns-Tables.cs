using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260702183000_Add-RepairRuns-Tables")]
    public partial class AddRepairRunsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepairRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Status = table.Column<int>(nullable: false),
                    Stage = table.Column<string>(maxLength: 64, nullable: false),
                    StartedAt = table.Column<long>(nullable: false),
                    UpdatedAt = table.Column<long>(nullable: false),
                    CompletedAt = table.Column<long>(nullable: true),
                    CancelledAt = table.Column<long>(nullable: true),
                    NextDueAt = table.Column<long>(nullable: true),
                    Total = table.Column<int>(nullable: false),
                    Checked = table.Column<int>(nullable: false),
                    Missing = table.Column<int>(nullable: false),
                    ProviderErrors = table.Column<int>(nullable: false),
                    Unknown = table.Column<int>(nullable: false),
                    Repaired = table.Column<int>(nullable: false),
                    Deleted = table.Column<int>(nullable: false),
                    ActionNeeded = table.Column<int>(nullable: false),
                    BrokenFiles = table.Column<int>(nullable: false),
                    Message = table.Column<string>(maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RepairEntryHealth",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    RepairRunId = table.Column<Guid>(nullable: false),
                    DavItemId = table.Column<Guid>(nullable: false),
                    Path = table.Column<string>(nullable: false),
                    State = table.Column<int>(nullable: false),
                    Message = table.Column<string>(maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(nullable: false),
                    UpdatedAt = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairEntryHealth", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepairEntryHealth_RepairRuns_RepairRunId",
                        column: x => x.RepairRunId,
                        principalTable: "RepairRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepairBrokenFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    RepairRunId = table.Column<Guid>(nullable: false),
                    DavItemId = table.Column<Guid>(nullable: false),
                    Path = table.Column<string>(nullable: false),
                    Reason = table.Column<string>(maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<long>(nullable: false),
                    Cleared = table.Column<bool>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairBrokenFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepairBrokenFiles_RepairRuns_RepairRunId",
                        column: x => x.RepairRunId,
                        principalTable: "RepairRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepairRuns_Status_StartedAt",
                table: "RepairRuns",
                columns: new[] { "Status", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepairEntryHealth_RepairRunId_DavItemId",
                table: "RepairEntryHealth",
                columns: new[] { "RepairRunId", "DavItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepairEntryHealth_RepairRunId_State_UpdatedAt",
                table: "RepairEntryHealth",
                columns: new[] { "RepairRunId", "State", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepairBrokenFiles_RepairRunId_Cleared_CreatedAt",
                table: "RepairBrokenFiles",
                columns: new[] { "RepairRunId", "Cleared", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepairBrokenFiles_DavItemId_Cleared",
                table: "RepairBrokenFiles",
                columns: new[] { "DavItemId", "Cleared" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepairBrokenFiles");

            migrationBuilder.DropTable(
                name: "RepairEntryHealth");

            migrationBuilder.DropTable(
                name: "RepairRuns");
        }
    }
}
