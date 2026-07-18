using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260712120000_Add-Rclone-Invalidation-Revision")]
    public partial class AddRcloneInvalidationRevision : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "RcloneInvalidationItems",
                nullable: false,
                defaultValue: 1L);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                table: "RcloneInvalidationItems");
        }
    }
}
