using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.PostgreSqlMigrations
{
    /// <inheritdoc />
    public partial class PostgreSqlNativeBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $nzbdav$
                DECLARE
                    target_schema name := current_schema();
                    history_oid oid;
                    history_has_rows boolean;
                BEGIN
                    IF current_setting('server_version') <> '16.14'
                       OR current_setting('server_version_num')::integer <> 160014 THEN
                        RAISE EXCEPTION 'NZBDav PostgreSQL native baseline requires exact PostgreSQL 16.14 (160014)';
                    END IF;

                    IF target_schema IS NULL
                       OR current_schemas(false) IS DISTINCT FROM ARRAY[target_schema]::name[]
                       OR current_schemas(true) IS DISTINCT FROM ARRAY['pg_catalog'::name, target_schema]::name[]
                       OR pg_my_temp_schema() <> 0 THEN
                        RAISE EXCEPTION 'NZBDav PostgreSQL native baseline requires one target schema and no temporary schema';
                    END IF;

                    IF NOT EXISTS (
                           SELECT 1 FROM pg_database AS d
                           WHERE d.datname = current_database()
                             AND d.datdba = current_user::regrole::oid
                             AND d.datacl IS NULL)
                       OR NOT EXISTS (
                           SELECT 1 FROM pg_namespace AS n
                           WHERE n.nspname = target_schema
                             AND n.nspowner = current_user::regrole::oid
                             AND n.nspacl IS NULL)
                       OR EXISTS (
                           SELECT 1 FROM pg_default_acl AS da
                           JOIN pg_namespace AS n ON n.nspname = target_schema
                           WHERE da.defaclrole = current_user::regrole::oid
                             AND (da.defaclnamespace = 0 OR da.defaclnamespace = n.oid)) THEN
                        RAISE EXCEPTION 'NZBDav PostgreSQL native baseline requires exact owner and default ACL state';
                    END IF;

                    IF EXISTS (SELECT 1 FROM pg_event_trigger)
                       OR EXISTS (SELECT 1 FROM pg_publication)
                       OR EXISTS (SELECT 1 FROM pg_publication_namespace)
                       OR EXISTS (
                           SELECT 1 FROM pg_subscription AS s
                           WHERE s.subdbid = (
                               SELECT d.oid FROM pg_database AS d
                               WHERE d.datname = current_database())) THEN
                        RAISE EXCEPTION 'NZBDav PostgreSQL native baseline refuses event triggers and logical replication objects';
                    END IF;

                    SELECT c.oid INTO history_oid
                    FROM pg_class AS c
                    JOIN pg_namespace AS n ON n.oid = c.relnamespace
                    WHERE n.nspname = target_schema
                      AND c.relname = '__EFMigrationsHistory_PostgreSql';

                    IF history_oid IS NULL
                       OR NOT EXISTS (
                           SELECT 1 FROM pg_class AS c
                           WHERE c.oid = history_oid
                             AND c.relkind = 'r'
                             AND c.relpersistence = 'p'
                             AND c.relowner = current_user::regrole::oid
                             AND c.relam = (SELECT oid FROM pg_am WHERE amname = 'heap')
                             AND c.reltablespace = 0
                             AND c.relchecks = 0
                             AND c.relhasindex
                             AND NOT c.relhasrules
                             AND NOT c.relhastriggers
                             AND NOT c.relrowsecurity
                             AND NOT c.relforcerowsecurity
                             AND c.relreplident = 'd'
                             AND NOT c.relhassubclass
                             AND NOT c.relispartition
                             AND c.reltoastrelid = 0
                             AND c.reloptions IS NULL
                             AND c.relacl IS NULL)
                       OR (SELECT count(*) FROM pg_class AS c
                           JOIN pg_namespace AS n ON n.oid = c.relnamespace
                           WHERE n.nspname = target_schema) <> 2
                       OR (SELECT count(*) FROM pg_attribute AS a
                           WHERE a.attrelid = history_oid AND a.attnum > 0) <> 2
                       OR NOT EXISTS (
                           SELECT 1 FROM pg_attribute AS a
                           WHERE a.attrelid = history_oid AND a.attnum = 1
                             AND a.attname = 'MigrationId'
                             AND NOT a.attisdropped
                             AND pg_catalog.format_type(a.atttypid, a.atttypmod) = 'character varying(150)'
                             AND a.atttypmod = 154 AND a.attndims = 0 AND a.attnotnull
                             AND NOT a.atthasdef AND NOT a.atthasmissing
                             AND a.attidentity = '' AND a.attgenerated = ''
                             AND a.attcollation = 'pg_catalog.default'::regcollation
                             AND a.attstorage = 'x' AND a.attcompression = ''
                             AND a.attstattarget = -1 AND a.attinhcount = 0 AND a.attislocal
                             AND a.attoptions IS NULL AND a.attacl IS NULL)
                       OR NOT EXISTS (
                           SELECT 1 FROM pg_attribute AS a
                           WHERE a.attrelid = history_oid AND a.attnum = 2
                             AND a.attname = 'ProductVersion'
                             AND NOT a.attisdropped
                             AND pg_catalog.format_type(a.atttypid, a.atttypmod) = 'character varying(32)'
                             AND a.atttypmod = 36 AND a.attndims = 0 AND a.attnotnull
                             AND NOT a.atthasdef AND NOT a.atthasmissing
                             AND a.attidentity = '' AND a.attgenerated = ''
                             AND a.attcollation = 'pg_catalog.default'::regcollation
                             AND a.attstorage = 'x' AND a.attcompression = ''
                             AND a.attstattarget = -1 AND a.attinhcount = 0 AND a.attislocal
                             AND a.attoptions IS NULL AND a.attacl IS NULL)
                       OR (SELECT count(*) FROM pg_constraint AS con
                           WHERE con.connamespace = (
                               SELECT oid FROM pg_namespace WHERE nspname = target_schema)) <> 1
                       OR NOT EXISTS (
                           SELECT 1 FROM pg_constraint AS con
                           WHERE con.conrelid = history_oid
                             AND con.conname = 'PK___EFMigrationsHistory_PostgreSql'
                             AND con.contype = 'p'
                             AND NOT con.condeferrable AND NOT con.condeferred
                             AND con.convalidated AND con.conislocal
                             AND con.coninhcount = 0 AND con.connoinherit
                             AND con.conparentid = 0
                             AND con.conkey = ARRAY[1]::smallint[])
                       OR (SELECT count(*) FROM pg_index AS idx
                           WHERE idx.indrelid = history_oid) <> 1
                       OR NOT EXISTS (
                           SELECT 1 FROM pg_index AS idx
                           JOIN pg_class AS index_class ON index_class.oid = idx.indexrelid
                           WHERE idx.indrelid = history_oid
                             AND index_class.relname = 'PK___EFMigrationsHistory_PostgreSql'
                             AND index_class.relkind = 'i'
                             AND index_class.relpersistence = 'p'
                             AND index_class.relowner = current_user::regrole::oid
                             AND index_class.relam = (SELECT oid FROM pg_am WHERE amname = 'btree')
                             AND index_class.reltablespace = 0
                             AND index_class.reloptions IS NULL
                             AND index_class.relacl IS NULL
                             AND idx.indisunique AND NOT idx.indnullsnotdistinct
                             AND idx.indisprimary AND NOT idx.indisexclusion
                             AND idx.indimmediate AND NOT idx.indisclustered
                             AND idx.indisvalid AND NOT idx.indcheckxmin
                             AND idx.indisready AND idx.indislive
                             AND NOT idx.indisreplident
                             AND idx.indnkeyatts = 1 AND idx.indnatts = 1
                             AND idx.indkey::text = '1'
                             AND idx.indcollation[0] = 'pg_catalog.default'::regcollation
                             AND idx.indclass[0] = (
                                 SELECT opclass.oid
                                 FROM pg_opclass AS opclass
                                 JOIN pg_namespace AS opclass_ns ON opclass_ns.oid = opclass.opcnamespace
                                 JOIN pg_am AS opclass_am ON opclass_am.oid = opclass.opcmethod
                                 WHERE opclass_ns.nspname = 'pg_catalog'
                                   AND opclass_am.amname = 'btree'
                                   AND opclass.opcname = 'text_ops'
                                   AND opclass.opcintype = 'text'::regtype)
                             AND idx.indoption::text = '0'
                             AND idx.indexprs IS NULL AND idx.indpred IS NULL)
                       OR (SELECT count(*) FROM pg_type AS t
                           JOIN pg_namespace AS n ON n.oid = t.typnamespace
                           WHERE n.nspname = target_schema) <> 2
                       OR EXISTS (
                           SELECT 1 FROM pg_type AS t
                           JOIN pg_namespace AS n ON n.oid = t.typnamespace
                           WHERE n.nspname = target_schema
                             AND (t.typowner <> current_user::regrole::oid
                                  OR t.typacl IS NOT NULL
                                  OR t.typname NOT IN (
                                      '__EFMigrationsHistory_PostgreSql',
                                      '___EFMigrationsHistory_PostgreSql')))
                       OR EXISTS (SELECT 1 FROM pg_proc AS p
                           JOIN pg_namespace AS n ON n.oid = p.pronamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_trigger AS tr
                           JOIN pg_class AS c ON c.oid = tr.tgrelid
                           JOIN pg_namespace AS n ON n.oid = c.relnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_rewrite AS r
                           JOIN pg_class AS c ON c.oid = r.ev_class
                           JOIN pg_namespace AS n ON n.oid = c.relnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_inherits AS inh
                           JOIN pg_class AS c ON c.oid = inh.inhrelid
                           JOIN pg_namespace AS n ON n.oid = c.relnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_policy AS p
                           JOIN pg_class AS c ON c.oid = p.polrelid
                           JOIN pg_namespace AS n ON n.oid = c.relnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_statistic_ext AS s
                           JOIN pg_namespace AS n ON n.oid = s.stxnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_collation AS c
                           JOIN pg_namespace AS n ON n.oid = c.collnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_operator AS o
                           JOIN pg_namespace AS n ON n.oid = o.oprnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_opclass AS o
                           JOIN pg_namespace AS n ON n.oid = o.opcnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_opfamily AS o
                           JOIN pg_namespace AS n ON n.oid = o.opfnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_conversion AS c
                           JOIN pg_namespace AS n ON n.oid = c.connamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_ts_config AS t
                           JOIN pg_namespace AS n ON n.oid = t.cfgnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_ts_dict AS t
                           JOIN pg_namespace AS n ON n.oid = t.dictnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_ts_parser AS t
                           JOIN pg_namespace AS n ON n.oid = t.prsnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_ts_template AS t
                           JOIN pg_namespace AS n ON n.oid = t.tmplnamespace
                           WHERE n.nspname = target_schema)
                       OR EXISTS (SELECT 1 FROM pg_extension AS e
                           JOIN pg_namespace AS n ON n.oid = e.extnamespace
                           WHERE n.nspname = target_schema) THEN
                        RAISE EXCEPTION 'NZBDav PostgreSQL native baseline requires exact EF-created empty migration history';
                    END IF;

                    EXECUTE format(
                        'SELECT EXISTS (SELECT 1 FROM %I.%I)',
                        target_schema,
                        '__EFMigrationsHistory_PostgreSql')
                    INTO history_has_rows;
                    IF history_has_rows THEN
                        RAISE EXCEPTION 'NZBDav PostgreSQL native baseline requires exact EF-created empty migration history';
                    END IF;
                END
                $nzbdav$;
                """);

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    RandomSalt = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => new { x.Type, x.Username });
                });

            migrationBuilder.CreateTable(
                name: "ArrDownloadCorrelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QueueItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    HistoryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArrApp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    InstanceKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    InstanceHost = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    DownloadId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true, collation: "C"),
                    QueueRecordId = table.Column<int>(type: "integer", nullable: true),
                    MediaKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true, collation: "C"),
                    MovieId = table.Column<int>(type: "integer", nullable: true),
                    SeriesId = table.Column<int>(type: "integer", nullable: true),
                    EpisodeId = table.Column<int>(type: "integer", nullable: true),
                    SeasonNumber = table.Column<int>(type: "integer", nullable: true),
                    ArtistId = table.Column<int>(type: "integer", nullable: true),
                    AlbumId = table.Column<int>(type: "integer", nullable: true),
                    EpisodeIdsJson = table.Column<string>(type: "text", nullable: true),
                    ReleaseTitle = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Category = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Indexer = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DownloadClient = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Quality = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CustomFormatsJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TrackedDownloadStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TrackedDownloadState = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    ManualLock = table.Column<bool>(type: "boolean", nullable: false),
                    IsUpgrade = table.Column<bool>(type: "boolean", nullable: false),
                    IsDuplicate = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    LastSeenAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArrDownloadCorrelations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArrDownloadLifecycleEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QueueItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    HistoryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArrApp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    InstanceKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    DownloadId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    MediaKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    State = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    StateReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArrDownloadLifecycleEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArrSearchNudgeCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArrApp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    InstanceKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    InstanceHost = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CommandName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CommandId = table.Column<int>(type: "integer", nullable: true),
                    TargetsJson = table.Column<string>(type: "text", nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    CooldownKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    ReasonsJson = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                    NextAllowedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArrSearchNudgeCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BlobCleanupItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlobCleanupItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigItems",
                columns: table => new
                {
                    ConfigName = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ConfigValue = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigItems", x => x.ConfigName);
                });

            migrationBuilder.CreateTable(
                name: "DavCleanupItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavCleanupItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DavItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdPrefix = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, collation: "C"),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    SubType = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    ReleaseDate = table.Column<long>(type: "bigint", nullable: true),
                    LastHealthCheck = table.Column<long>(type: "bigint", nullable: true),
                    NextHealthCheck = table.Column<long>(type: "bigint", nullable: true),
                    HistoryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileBlobId = table.Column<Guid>(type: "uuid", nullable: true),
                    NzbBlobId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthCheckResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DavItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    RepairStatus = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthCheckStats",
                columns: table => new
                {
                    DateStartInclusive = table.Column<long>(type: "bigint", nullable: false),
                    DateEndExclusive = table.Column<long>(type: "bigint", nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    RepairStatus = table.Column<int>(type: "integer", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckStats", x => new { x.DateStartInclusive, x.DateEndExclusive, x.Result, x.RepairStatus });
                });

            migrationBuilder.CreateTable(
                name: "HistoryCleanupItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeleteMountedFiles = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryCleanupItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HistoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    JobName = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    DownloadStatus = table.Column<int>(type: "integer", nullable: false),
                    TotalSegmentBytes = table.Column<long>(type: "bigint", nullable: false),
                    DownloadTimeSeconds = table.Column<int>(type: "integer", nullable: false),
                    FailMessage = table.Column<string>(type: "text", nullable: true),
                    DownloadDirId = table.Column<Guid>(type: "uuid", nullable: true),
                    NzbBlobId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DavItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    HistoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    ImportedAt = table.Column<long>(type: "bigint", nullable: true),
                    RemovedAt = table.Column<long>(type: "bigint", nullable: true),
                    Detail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ActiveSlot = table.Column<int>(type: "integer", nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                    CancellationRequestedAt = table.Column<long>(type: "bigint", nullable: true),
                    ProgressCurrent = table.Column<int>(type: "integer", nullable: false),
                    ProgressTotal = table.Column<int>(type: "integer", nullable: true),
                    Message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Error = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NzbBlobCleanupItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NzbBlobCleanupItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NzbNames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NzbNames", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueueItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    JobName = table.Column<string>(type: "text", nullable: false),
                    NzbFileSize = table.Column<long>(type: "bigint", nullable: false),
                    TotalSegmentBytes = table.Column<long>(type: "bigint", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    ArchivePassword = table.Column<string>(type: "text", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    PostProcessing = table.Column<int>(type: "integer", nullable: false),
                    PauseUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RcloneInvalidationItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "bigint", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "bigint", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RcloneInvalidationItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RepairRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                    CancelledAt = table.Column<long>(type: "bigint", nullable: true),
                    NextDueAt = table.Column<long>(type: "bigint", nullable: true),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Checked = table.Column<int>(type: "integer", nullable: false),
                    Missing = table.Column<int>(type: "integer", nullable: false),
                    ProviderErrors = table.Column<int>(type: "integer", nullable: false),
                    Unknown = table.Column<int>(type: "integer", nullable: false),
                    Repaired = table.Column<int>(type: "integer", nullable: false),
                    Deleted = table.Column<int>(type: "integer", nullable: false),
                    ActionNeeded = table.Column<int>(type: "integer", nullable: false),
                    BrokenFiles = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkerJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    AvailableAt = table.Column<long>(type: "bigint", nullable: false),
                    LeaseExpiresAt = table.Column<long>(type: "bigint", nullable: true),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LastHeartbeatAt = table.Column<long>(type: "bigint", nullable: true),
                    StartedAt = table.Column<long>(type: "bigint", nullable: true),
                    CancelRequestedAt = table.Column<long>(type: "bigint", nullable: true),
                    FailureKind = table.Column<int>(type: "integer", nullable: true),
                    ProgressJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    ProgressUpdatedAt = table.Column<long>(type: "bigint", nullable: true),
                    ResultJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DavMultipartFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Metadata = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavMultipartFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DavMultipartFiles_DavItems_Id",
                        column: x => x.Id,
                        principalTable: "DavItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DavNzbFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SegmentIds = table.Column<string>(type: "text", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RarParts = table.Column<string>(type: "text", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "ArrImportCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HistoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RequiredInvalidationPathsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "bigint", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "bigint", nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: "bigint", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    VisibleAt = table.Column<long>(type: "bigint", nullable: true),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                    ResultsJson = table.Column<string>(type: "text", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
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

            migrationBuilder.CreateTable(
                name: "QueueNzbContents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NzbContents = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueNzbContents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueueNzbContents_QueueItems_Id",
                        column: x => x.Id,
                        principalTable: "QueueItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QueuePriorityHints",
                columns: table => new
                {
                    QueueItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    EffectivePriority = table.Column<int>(type: "integer", nullable: false),
                    ApplyToScheduling = table.Column<bool>(type: "boolean", nullable: false),
                    ReasonsJson = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ComputedAt = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: false),
                    StaleReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
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

            migrationBuilder.CreateTable(
                name: "RepairBrokenFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepairRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    DavItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    Cleared = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairBrokenFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepairBrokenFiles_RepairRuns_RepairRunId",
                        column: x => x.RepairRunId,
                        principalTable: "RepairRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RepairEntryHealth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepairRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    DavItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairEntryHealth", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepairEntryHealth_RepairRuns_RepairRunId",
                        column: x => x.RepairRunId,
                        principalTable: "RepairRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "IX_ArrDownloadCorrelations_HistoryItemId",
                table: "ArrDownloadCorrelations",
                column: "HistoryItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadCorrelations_IsDuplicate_LastSeenAt",
                table: "ArrDownloadCorrelations",
                columns: new[] { "IsDuplicate", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadCorrelations_QueueItemId",
                table: "ArrDownloadCorrelations",
                column: "QueueItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadCorrelations_Source_ManualLock",
                table: "ArrDownloadCorrelations",
                columns: new[] { "Source", "ManualLock" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadLifecycleEvents_HistoryItemId_CreatedAt",
                table: "ArrDownloadLifecycleEvents",
                columns: new[] { "HistoryItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrDownloadLifecycleEvents_QueueItemId_CreatedAt",
                table: "ArrDownloadLifecycleEvents",
                columns: new[] { "QueueItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrLifecycle_Instance_State_CreatedAt",
                table: "ArrDownloadLifecycleEvents",
                columns: new[] { "ArrApp", "InstanceKey", "State", "CreatedAt" });

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

            migrationBuilder.CreateIndex(
                name: "IX_ArrSearchNudgeCommands_ArrApp_InstanceKey_Status_CreatedAt",
                table: "ArrSearchNudgeCommands",
                columns: new[] { "ArrApp", "InstanceKey", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArrSearchNudgeCommands_CooldownKey_NextAllowedAt",
                table: "ArrSearchNudgeCommands",
                columns: new[] { "CooldownKey", "NextAllowedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_HistoryItemId_SubType_CreatedAt",
                table: "DavItems",
                columns: new[] { "HistoryItemId", "SubType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_HistoryItemId_Type_CreatedAt",
                table: "DavItems",
                columns: new[] { "HistoryItemId", "Type", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_IdPrefix_Type",
                table: "DavItems",
                columns: new[] { "IdPrefix", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_NzbBlobId",
                table: "DavItems",
                column: "NzbBlobId");

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_ParentId_Name",
                table: "DavItems",
                columns: new[] { "ParentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_Type_HistoryItemId_NextHealthCheck_ReleaseDate_Id",
                table: "DavItems",
                columns: new[] { "Type", "HistoryItemId", "NextHealthCheck", "ReleaseDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckResults_CreatedAt",
                table: "HealthCheckResults",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckResults_DavItemId",
                table: "HealthCheckResults",
                column: "DavItemId",
                filter: "\"RepairStatus\" = 3");

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckResults_DavItemId_CreatedAt_Id",
                table: "HealthCheckResults",
                columns: new[] { "DavItemId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckResults_Result_RepairStatus_CreatedAt",
                table: "HealthCheckResults",
                columns: new[] { "Result", "RepairStatus", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_Category",
                table: "HistoryItems",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_Category_CreatedAt",
                table: "HistoryItems",
                columns: new[] { "Category", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_Category_DownloadDirId",
                table: "HistoryItems",
                columns: new[] { "Category", "DownloadDirId" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_CreatedAt",
                table: "HistoryItems",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_NzbBlobId",
                table: "HistoryItems",
                column: "NzbBlobId");

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

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRuns_ActiveSlot",
                table: "MaintenanceRuns",
                column: "ActiveSlot",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRuns_Kind_CreatedAt",
                table: "MaintenanceRuns",
                columns: new[] { "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRuns_Status_CreatedAt",
                table: "MaintenanceRuns",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category",
                table: "QueueItems",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category_FileName",
                table: "QueueItems",
                columns: new[] { "Category", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category_Priority_CreatedAt",
                table: "QueueItems",
                columns: new[] { "Category", "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_CreatedAt",
                table: "QueueItems",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority",
                table: "QueueItems",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority_CreatedAt",
                table: "QueueItems",
                columns: new[] { "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority_PauseUntil_CreatedAt",
                table: "QueueItems",
                columns: new[] { "Priority", "PauseUntil", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QueuePriorityHints_EffectivePriority_Score_ExpiresAt",
                table: "QueuePriorityHints",
                columns: new[] { "EffectivePriority", "Score", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RcloneInvalidationItems_NextAttemptAt_CreatedAt",
                table: "RcloneInvalidationItems",
                columns: new[] { "NextAttemptAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RcloneInvalidationItems_Path",
                table: "RcloneInvalidationItems",
                column: "Path");

            migrationBuilder.CreateIndex(
                name: "IX_RepairBrokenFiles_DavItemId_Cleared",
                table: "RepairBrokenFiles",
                columns: new[] { "DavItemId", "Cleared" });

            migrationBuilder.CreateIndex(
                name: "IX_RepairBrokenFiles_RepairRunId_Cleared_CreatedAt",
                table: "RepairBrokenFiles",
                columns: new[] { "RepairRunId", "Cleared", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepairEntryHealth_RepairRunId_DavItemId",
                table: "RepairEntryHealth",
                columns: new[] { "RepairRunId", "DavItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepairEntryHealth_RepairRunId_State_UpdatedAt",
                table: "RepairEntryHealth",
                columns: new[] { "RepairRunId", "State", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepairRuns_Status_StartedAt",
                table: "RepairRuns",
                columns: new[] { "Status", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerJobs_ClaimOrder",
                table: "WorkerJobs",
                columns: new[] { "Kind", "Status", "AvailableAt", "LeaseExpiresAt", "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerJobs_Kind_Status_LeaseExpiresAt",
                table: "WorkerJobs",
                columns: new[] { "Kind", "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerJobs_Kind_Status_Priority_AvailableAt_CreatedAt",
                table: "WorkerJobs",
                columns: new[] { "Kind", "Status", "Priority", "AvailableAt", "CreatedAt" },
                descending: new[] { false, false, true, false, false });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerJobs_Kind_TargetId",
                table: "WorkerJobs",
                columns: new[] { "Kind", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerJobs_Status_LeaseExpiresAt_LeaseGeneration",
                table: "WorkerJobs",
                columns: new[] { "Status", "LeaseExpiresAt", "LeaseGeneration" });

            migrationBuilder.InsertData(
                table: "DavItems",
                columns: new[]
                {
                    "Id", "IdPrefix", "CreatedAt", "ParentId", "Name", "FileSize", "Type", "SubType", "Path"
                },
                values: new object[,]
                {
                    {
                        new Guid("00000000-0000-0000-0000-000000000000"), "00000", DateTime.MinValue,
                        null, "/", null, 1, 102, "/"
                    },
                    {
                        new Guid("00000000-0000-0000-0000-000000000001"), "00000", DateTime.MinValue,
                        new Guid("00000000-0000-0000-0000-000000000000"), "nzbs", null, 1, 103, "/nzbs"
                    },
                    {
                        new Guid("00000000-0000-0000-0000-000000000002"), "00000", DateTime.MinValue,
                        new Guid("00000000-0000-0000-0000-000000000000"), "content", null, 1, 104, "/content"
                    },
                    {
                        new Guid("00000000-0000-0000-0000-000000000003"), "00000", DateTime.MinValue,
                        new Guid("00000000-0000-0000-0000-000000000000"), "completed-symlinks", null, 1, 105,
                        "/completed-symlinks"
                    },
                    {
                        new Guid("00000000-0000-0000-0000-000000000004"), "00000", DateTime.MinValue,
                        new Guid("00000000-0000-0000-0000-000000000000"), ".ids", null, 1, 106, "/.ids"
                    }
                });

            migrationBuilder.Sql(
                """
                DO $nzbdav$
                DECLARE
                    api_key text;
                    strm_key text;
                BEGIN
                    LOOP
                        api_key := replace(gen_random_uuid()::text, '-', '');
                        strm_key := replace(gen_random_uuid()::text, '-', '');
                        EXIT WHEN api_key <> strm_key;
                    END LOOP;

                    INSERT INTO "ConfigItems" ("ConfigName", "ConfigValue")
                    VALUES
                        ('api.key', api_key),
                        ('api.strm-key', strm_key),
                        ('database.import-state', '{"formatVersion":3,"state":"fresh"}');
                END
                $nzbdav$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "ArrDownloadCorrelations");

            migrationBuilder.DropTable(
                name: "ArrDownloadLifecycleEvents");

            migrationBuilder.DropTable(
                name: "ArrImportCommands");

            migrationBuilder.DropTable(
                name: "ArrSearchNudgeCommands");

            migrationBuilder.DropTable(
                name: "BlobCleanupItems");

            migrationBuilder.DropTable(
                name: "ConfigItems");

            migrationBuilder.DropTable(
                name: "DavCleanupItems");

            migrationBuilder.DropTable(
                name: "DavMultipartFiles");

            migrationBuilder.DropTable(
                name: "DavNzbFiles");

            migrationBuilder.DropTable(
                name: "DavRarFiles");

            migrationBuilder.DropTable(
                name: "HealthCheckResults");

            migrationBuilder.DropTable(
                name: "HealthCheckStats");

            migrationBuilder.DropTable(
                name: "HistoryCleanupItems");

            migrationBuilder.DropTable(
                name: "ImportReceipts");

            migrationBuilder.DropTable(
                name: "MaintenanceRuns");

            migrationBuilder.DropTable(
                name: "NzbBlobCleanupItems");

            migrationBuilder.DropTable(
                name: "NzbNames");

            migrationBuilder.DropTable(
                name: "QueueNzbContents");

            migrationBuilder.DropTable(
                name: "QueuePriorityHints");

            migrationBuilder.DropTable(
                name: "RcloneInvalidationItems");

            migrationBuilder.DropTable(
                name: "RepairBrokenFiles");

            migrationBuilder.DropTable(
                name: "RepairEntryHealth");

            migrationBuilder.DropTable(
                name: "WorkerJobs");

            migrationBuilder.DropTable(
                name: "HistoryItems");

            migrationBuilder.DropTable(
                name: "DavItems");

            migrationBuilder.DropTable(
                name: "QueueItems");

            migrationBuilder.DropTable(
                name: "RepairRuns");
        }
    }
}
