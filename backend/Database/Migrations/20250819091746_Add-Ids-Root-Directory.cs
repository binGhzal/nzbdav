using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddIdsRootDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO "DavItems" ("Id", "ParentId", "Name", "FileSize", "Type")
                VALUES ('00000000-0000-0000-0000-000000000004', '00000000-0000-0000-0000-000000000000', '.ids', NULL, 5);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM "DavItems"
                WHERE "Id" = '00000000-0000-0000-0000-000000000004';
                """);
        }
    }
}
