using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitializeDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DavItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    ParentId = table.Column<Guid>(nullable: true),
                    Name = table.Column<string>(maxLength: 255, nullable: false),
                    FileSize = table.Column<long>(nullable: true),
                    Type = table.Column<int>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DavItems_DavItems_ParentId",
                        column: x => x.ParentId,
                        principalTable: "DavItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HistoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    FileName = table.Column<string>(nullable: false),
                    JobName = table.Column<string>(nullable: false),
                    Category = table.Column<string>(nullable: false),
                    DownloadStatus = table.Column<int>(nullable: false),
                    TotalSegmentBytes = table.Column<long>(nullable: false),
                    DownloadTimeSeconds = table.Column<int>(nullable: false),
                    FailMessage = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueueItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    FileName = table.Column<string>(nullable: false),
                    JobName = table.Column<string>(nullable: false),
                    NzbContents = table.Column<string>(nullable: false),
                    NzbFileSize = table.Column<long>(nullable: false),
                    TotalSegmentBytes = table.Column<long>(nullable: false),
                    Category = table.Column<string>(nullable: false),
                    Priority = table.Column<int>(nullable: false),
                    PostProcessing = table.Column<int>(nullable: false),
                    PauseUntil = table.Column<DateTime>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DavNzbFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    SegmentIds = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavNzbFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DavNzbFiles_DavItems_Id",
                        column: x => x.Id,
                        principalTable: "DavItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DavRarFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    RarParts = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavRarFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DavRarFiles_DavItems_Id",
                        column: x => x.Id,
                        principalTable: "DavItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_ParentId_Name",
                table: "DavItems",
                columns: new[] { "ParentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_Category",
                table: "HistoryItems",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_Category_CreatedAt",
                table: "HistoryItems",
                columns: new[] { "Category", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_CreatedAt",
                table: "HistoryItems",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category",
                table: "QueueItems",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category_Priority_CreatedAt",
                table: "QueueItems",
                columns: new[] { "Category", "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_CreatedAt",
                table: "QueueItems",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_FileName",
                table: "QueueItems",
                column: "FileName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority",
                table: "QueueItems",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority_CreatedAt",
                table: "QueueItems",
                columns: new[] { "Priority", "CreatedAt" });

            migrationBuilder.Sql(
                """
                INSERT INTO "DavItems" ("Id", "ParentId", "Name", "FileSize", "Type")
                VALUES
                    ('00000000-0000-0000-0000-000000000000', NULL, '/', NULL, 1),
                    ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000000', 'nzbs', NULL, 1),
                    ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000000', 'content', NULL, 1),
                    ('00000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000000', 'completed-symlinks', NULL, 2);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DavNzbFiles");

            migrationBuilder.DropTable(
                name: "DavRarFiles");

            migrationBuilder.DropTable(
                name: "HistoryItems");

            migrationBuilder.DropTable(
                name: "QueueItems");

            migrationBuilder.DropTable(
                name: "DavItems");
        }
    }
}
