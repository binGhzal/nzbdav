using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260705033000_Add-HealthCheckResult-Latest-Index")]
    public partial class AddHealthCheckResultLatestIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckResults_DavItemId_CreatedAt_Id",
                table: "HealthCheckResults",
                columns: new[] { "DavItemId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HealthCheckResults_DavItemId_CreatedAt_Id",
                table: "HealthCheckResults");
        }
    }
}
