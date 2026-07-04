using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260704120000_Add-ArrDrivenPriority-Tables")]
    public partial class AddArrDrivenPriorityTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArrDownloadCorrelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    QueueItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HistoryItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ArrApp = table.Column<string>(maxLength: 32, nullable: false),
                    InstanceKey = table.Column<string>(maxLength: 512, nullable: false),
                    InstanceHost = table.Column<string>(maxLength: 1024, nullable: false),
                    DownloadId = table.Column<string>(maxLength: 255, nullable: true),
                    QueueRecordId = table.Column<int>(nullable: true),
                    MediaKey = table.Column<string>(maxLength: 255, nullable: true),
                    MovieId = table.Column<int>(nullable: true),
                    SeriesId = table.Column<int>(nullable: true),
                    EpisodeId = table.Column<int>(nullable: true),
                    SeasonNumber = table.Column<int>(nullable: true),
                    ArtistId = table.Column<int>(nullable: true),
                    AlbumId = table.Column<int>(nullable: true),
                    EpisodeIdsJson = table.Column<string>(nullable: true),
                    ReleaseTitle = table.Column<string>(maxLength: 1024, nullable: true),
                    Category = table.Column<string>(maxLength: 255, nullable: true),
                    Indexer = table.Column<string>(maxLength: 255, nullable: true),
                    DownloadClient = table.Column<string>(maxLength: 255, nullable: true),
                    Quality = table.Column<string>(maxLength: 255, nullable: true),
                    CustomFormatsJson = table.Column<string>(nullable: true),
                    Status = table.Column<string>(maxLength: 64, nullable: true),
                    TrackedDownloadStatus = table.Column<string>(maxLength: 64, nullable: true),
                    TrackedDownloadState = table.Column<string>(maxLength: 64, nullable: true),
                    IsUpgrade = table.Column<bool>(nullable: false),
                    IsDuplicate = table.Column<bool>(nullable: false),
                    CreatedAt = table.Column<long>(nullable: false),
                    UpdatedAt = table.Column<long>(nullable: false),
                    LastSeenAt = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArrDownloadCorrelations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArrDownloadLifecycleEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    QueueItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HistoryItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ArrApp = table.Column<string>(maxLength: 32, nullable: false),
                    InstanceKey = table.Column<string>(maxLength: 512, nullable: false),
                    DownloadId = table.Column<string>(maxLength: 255, nullable: true),
                    MediaKey = table.Column<string>(maxLength: 255, nullable: true),
                    State = table.Column<string>(maxLength: 64, nullable: false),
                    StateReason = table.Column<string>(maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArrDownloadLifecycleEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArrSearchNudgeCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArrApp = table.Column<string>(maxLength: 32, nullable: false),
                    InstanceKey = table.Column<string>(maxLength: 512, nullable: false),
                    InstanceHost = table.Column<string>(maxLength: 1024, nullable: false),
                    CommandName = table.Column<string>(maxLength: 128, nullable: false),
                    CommandId = table.Column<int>(nullable: true),
                    TargetsJson = table.Column<string>(nullable: false),
                    Mode = table.Column<string>(maxLength: 32, nullable: false),
                    Status = table.Column<string>(maxLength: 32, nullable: false),
                    CooldownKey = table.Column<string>(maxLength: 512, nullable: false),
                    Score = table.Column<int>(nullable: false),
                    ReasonsJson = table.Column<string>(nullable: false),
                    Error = table.Column<string>(maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(nullable: false),
                    CompletedAt = table.Column<long>(nullable: true),
                    NextAllowedAt = table.Column<long>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArrSearchNudgeCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueuePriorityHints",
                columns: table => new
                {
                    QueueItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Score = table.Column<int>(nullable: false),
                    EffectivePriority = table.Column<int>(nullable: false),
                    ApplyToScheduling = table.Column<bool>(nullable: false),
                    ReasonsJson = table.Column<string>(nullable: false),
                    Source = table.Column<string>(maxLength: 64, nullable: false),
                    ComputedAt = table.Column<long>(nullable: false),
                    ExpiresAt = table.Column<long>(nullable: false),
                    StaleReason = table.Column<string>(maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueuePriorityHints", x => x.QueueItemId);
                    table.ForeignKey(
                        name: "FK_QueuePriorityHints_QueueItems_QueueItemId",
                        column: x => x.QueueItemId,
                        principalTable: "QueueItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadCorrelations_QueueItemId",
                table: "ArrDownloadCorrelations",
                column: "QueueItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadCorrelations_HistoryItemId",
                table: "ArrDownloadCorrelations",
                column: "HistoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadCorrelations_ArrApp_InstanceKey_DownloadId",
                table: "ArrDownloadCorrelations",
                columns: new[] { "ArrApp", "InstanceKey", "DownloadId" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadCorrelations_ArrApp_InstanceKey_MediaKey",
                table: "ArrDownloadCorrelations",
                columns: new[] { "ArrApp", "InstanceKey", "MediaKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadCorrelations_ArrApp_InstanceKey_QueueRecordId",
                table: "ArrDownloadCorrelations",
                columns: new[] { "ArrApp", "InstanceKey", "QueueRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadCorrelations_IsDuplicate_LastSeenAt",
                table: "ArrDownloadCorrelations",
                columns: new[] { "IsDuplicate", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadLifecycleEvents_QueueItemId_CreatedAt",
                table: "ArrDownloadLifecycleEvents",
                columns: new[] { "QueueItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadLifecycleEvents_HistoryItemId_CreatedAt",
                table: "ArrDownloadLifecycleEvents",
                columns: new[] { "HistoryItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadLifecycleEvents_ArrApp_InstanceKey_State_CreatedAt",
                table: "ArrDownloadLifecycleEvents",
                columns: new[] { "ArrApp", "InstanceKey", "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrSearchNudgeCommands_ArrApp_InstanceKey_Status_CreatedAt",
                table: "ArrSearchNudgeCommands",
                columns: new[] { "ArrApp", "InstanceKey", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrSearchNudgeCommands_CooldownKey_NextAllowedAt",
                table: "ArrSearchNudgeCommands",
                columns: new[] { "CooldownKey", "NextAllowedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QueuePriorityHints_EffectivePriority_Score_ExpiresAt",
                table: "QueuePriorityHints",
                columns: new[] { "EffectivePriority", "Score", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ArrDownloadCorrelations");
            migrationBuilder.DropTable(name: "ArrDownloadLifecycleEvents");
            migrationBuilder.DropTable(name: "ArrSearchNudgeCommands");
            migrationBuilder.DropTable(name: "QueuePriorityHints");
        }
    }
}
