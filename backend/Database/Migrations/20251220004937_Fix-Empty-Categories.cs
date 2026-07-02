using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class FixEmptyCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Function for fixing tables with empty category strings
            // Ensures, that configured category exists
            // Fallback to 'uncategorized'
            var fixEmptyCategory = (string table) => migrationBuilder.Sql(
                $"""
                UPDATE "{table}"
                SET "Category" = COALESCE(
                    (
                        SELECT d."Name"
                        FROM "ConfigItems" c
                        JOIN "DavItems" d
                        ON d."Name" = c."ConfigValue"
                        WHERE c."ConfigName" = 'api.manual-category'
                    ),
                    'uncategorized'
                )
                WHERE "Category" = '';
                """);

            // Fix HistoryItems
            fixEmptyCategory("HistoryItems");

            // Fix QueueItems
            fixEmptyCategory("QueueItems");

            // Fix DavItems
            // Move items to configured category, "uncategorized" or "content"-root
            // The NOT EXISTS check preserves the old duplicate-protection behavior.
            migrationBuilder.Sql(
                """
                UPDATE "DavItems"
                SET "ParentId" = COALESCE(
                    (
                        SELECT "Id"
                        FROM "DavItems"
                        WHERE "Name" = COALESCE(
                            (SELECT "ConfigValue" FROM "ConfigItems" WHERE "ConfigName" = 'api.manual-category'),
                            'uncategorized'
                        )
                    ),
                    '00000000-0000-0000-0000-000000000002'
                )
                WHERE "ParentId" = (SELECT "Id" FROM "DavItems" WHERE "Name" = '')
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "DavItems" existing
                      WHERE existing."ParentId" = COALESCE(
                          (
                              SELECT "Id"
                              FROM "DavItems"
                              WHERE "Name" = COALESCE(
                                  (SELECT "ConfigValue" FROM "ConfigItems" WHERE "ConfigName" = 'api.manual-category'),
                                  'uncategorized'
                              )
                          ),
                          '00000000-0000-0000-0000-000000000002'
                      )
                        AND existing."Name" = "DavItems"."Name"
                        AND existing."Id" <> "DavItems"."Id"
                  );
                """);

            // Remove empty parent
            // Previous duplicates will be removed due to 'ON DELETE CASCADE'
            migrationBuilder.Sql(
                """
                DELETE FROM "DavItems" WHERE "Name" = ''
                """);

            // Rebuild 'Path' column
            AddPathToDavItem.BuildFullPath(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally left blank
        }
    }
}
