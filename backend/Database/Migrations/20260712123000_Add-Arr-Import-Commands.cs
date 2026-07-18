using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260712123000_Add-Arr-Import-Commands")]
    public partial class AddArrImportCommands : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArrImportCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    HistoryItemId = table.Column<Guid>(nullable: false),
                    Category = table.Column<string>(maxLength: 255, nullable: false),
                    RequiredInvalidationPathsJson = table.Column<string>(nullable: false),
                    Status = table.Column<int>(nullable: false),
                    Attempts = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<long>(nullable: false),
                    UpdatedAt = table.Column<long>(nullable: false),
                    NextAttemptAt = table.Column<long>(nullable: false),
                    LastAttemptAt = table.Column<long>(nullable: true),
                    LeaseExpiresAt = table.Column<long>(nullable: true),
                    LeaseToken = table.Column<Guid>(nullable: true),
                    VisibleAt = table.Column<long>(nullable: true),
                    CompletedAt = table.Column<long>(nullable: true),
                    ResultsJson = table.Column<string>(nullable: false),
                    LastError = table.Column<string>(maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArrImportCommands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArrImportCommands_HistoryItems_HistoryItemId",
                        column: x => x.HistoryItemId,
                        principalTable: "HistoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArrImportCommands_HistoryItemId",
                table: "ArrImportCommands",
                column: "HistoryItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArrImportCommands_Status_LeaseExpiresAt",
                table: "ArrImportCommands",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrImportCommands_Status_NextAttemptAt_CreatedAt",
                table: "ArrImportCommands",
                columns: new[] { "Status", "NextAttemptAt", "CreatedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ArrImportCommands");
        }
    }
}
