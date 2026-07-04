using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260704143000_Add-ArrCorrelation-Source-And-ManualLock")]
    public partial class AddArrCorrelationSourceAndManualLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "ArrDownloadCorrelations",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<bool>(
                name: "ManualLock",
                table: "ArrDownloadCorrelations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadCorrelations_Source_ManualLock",
                table: "ArrDownloadCorrelations",
                columns: new[] { "Source", "ManualLock" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArrDownloadCorrelations_Source_ManualLock",
                table: "ArrDownloadCorrelations");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "ArrDownloadCorrelations");

            migrationBuilder.DropColumn(
                name: "ManualLock",
                table: "ArrDownloadCorrelations");
        }
    }
}
