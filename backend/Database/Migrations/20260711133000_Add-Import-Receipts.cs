using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260711133000_Add-Import-Receipts")]
    public partial class AddImportReceipts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    DavItemId = table.Column<Guid>(nullable: false),
                    HistoryItemId = table.Column<Guid>(nullable: false),
                    State = table.Column<int>(nullable: false),
                    CreatedAt = table.Column<long>(nullable: false),
                    UpdatedAt = table.Column<long>(nullable: false),
                    ImportedAt = table.Column<long>(nullable: true),
                    RemovedAt = table.Column<long>(nullable: true),
                    Detail = table.Column<string>(maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportReceipts_DavItemId_HistoryItemId",
                table: "ImportReceipts",
                columns: new[] { "DavItemId", "HistoryItemId" },
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_ImportReceipts_State",
                table: "ImportReceipts",
                column: "State");
            migrationBuilder.CreateIndex(
                name: "IX_ImportReceipts_UpdatedAt",
                table: "ImportReceipts",
                column: "UpdatedAt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ImportReceipts");
        }
    }
}
