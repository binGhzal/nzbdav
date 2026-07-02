using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260702153000_Add-RcloneInvalidationItems-Table")]
    public partial class AddRcloneInvalidationItemsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RcloneInvalidationItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RcloneInvalidationItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RcloneInvalidationItems_NextAttemptAt_CreatedAt",
                table: "RcloneInvalidationItems",
                columns: new[] { "NextAttemptAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RcloneInvalidationItems_Path",
                table: "RcloneInvalidationItems",
                column: "Path");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RcloneInvalidationItems");
        }
    }
}
