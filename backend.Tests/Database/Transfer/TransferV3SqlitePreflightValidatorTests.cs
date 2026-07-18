using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;
using System.Security.Cryptography;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SqlitePreflightValidatorTests
{
    [Fact]
    public void ProductionAssembly_ContainsRawSqlitePreflightValidator()
    {
        Assert.NotNull(typeof(DavDatabaseContext).Assembly.GetType(
            "NzbWebDAV.Database.Transfer.TransferV3SqlitePreflightValidator",
            throwOnError: false));
    }

    [Fact]
    public async Task ValidateAsync_AcceptsExactFreshSourceBeforeAnyEfMaterialization()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var validator = new TransferV3SqlitePreflightValidator();

        var result = await validator.ValidateAsync(
            source.DatabasePath,
            source.Options(MaxRowsPerBatch: 2, MaxBytesPerBatch: 1024));

        Assert.Equal(27, result.Tables.Count);
        Assert.Equal(5, result.Tables.Single(table => table.Name == "DavItems").RowCount);
        Assert.Equal(0, result.Blobs.Count);
        Assert.InRange(result.MaxObservedRowsPerBatch, 1, 2);
        Assert.InRange(result.MaxObservedIoBufferBytes, 1, 64 * 1024);
    }

    [Fact]
    public async Task ValidateAsync_RejectsMissingFutureDuplicateAndUnsupportedHistoryBeforeSchemaScanning()
    {
        await AssertHistoryFailureAsync(
            "DELETE FROM __EFMigrationsHistory WHERE MigrationId = '20250529081501_InitializeDatabase';",
            "history-missing");
        await AssertHistoryFailureAsync(
            "INSERT INTO __EFMigrationsHistory(MigrationId, ProductVersion) VALUES ('20990101000000_Future', '10.0.9');",
            "history-future");
        await AssertHistoryFailureAsync(
            "UPDATE __EFMigrationsHistory SET ProductVersion = '99.0.0' WHERE MigrationId = '20250529081501_InitializeDatabase';",
            "history-product-version");
        await AssertHistoryFailureAsync(
            """
            ALTER TABLE __EFMigrationsHistory RENAME TO old_history;
            CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL, ProductVersion TEXT NOT NULL);
            INSERT INTO __EFMigrationsHistory SELECT * FROM old_history;
            INSERT INTO __EFMigrationsHistory(MigrationId, ProductVersion)
            VALUES ('20250529081501_InitializeDatabase', '10.0.9');
            DROP TABLE old_history;
            """,
            "history-duplicate");
    }

    [Fact]
    public async Task ValidateAsync_AcceptsReviewedHistoricalProductVersionForItsExactMigration()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            "UPDATE __EFMigrationsHistory SET ProductVersion = '9.0.4' WHERE MigrationId = '20250529081501_InitializeDatabase';");

        var result = await source.ValidateAsync();

        Assert.Equal(27, result.Tables.Count);
    }

    [Fact]
    public async Task ValidateAsync_BoundsHistoryRowsAndRawTextBeforeMaterialization()
    {
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                "UPDATE __EFMigrationsHistory "
                + "SET MigrationId = hex(zeroblob(1048576)) "
                + "WHERE MigrationId = '20250529081501_InitializeDatabase';");

            await AssertFailureAsync(source, "history-shape");
        }

        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                """
                ALTER TABLE __EFMigrationsHistory RENAME TO old_history;
                CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL, ProductVersion TEXT NOT NULL);
                INSERT INTO __EFMigrationsHistory SELECT * FROM old_history;
                WITH RECURSIVE rows(value) AS (
                    SELECT 1
                    UNION ALL
                    SELECT value + 1 FROM rows WHERE value < 4096
                )
                INSERT INTO __EFMigrationsHistory(MigrationId, ProductVersion)
                SELECT printf('20990101%06d_Future', value), '10.0.9' FROM rows;
                DROP TABLE old_history;
                """);

            var exception = await AssertFailureAsync(source, "history-future");
            Assert.Contains("row=50", exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("CREATE TABLE ManualDrift(Id INTEGER);", "schema-drift")]
    [InlineData("CREATE INDEX IX_Manual_Drift ON ConfigItems(ConfigValue);", "schema-drift")]
    public async Task ValidateAsync_RejectsUnknownOrDriftedPhysicalSchema(string sql, string code)
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(sql);

        await AssertFailureAsync(source, code);
    }

    [Fact]
    public async Task ValidateAsync_BoundsSchemaSqlAndObjectCountBeforePragmaFanout()
    {
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                """
                PRAGMA writable_schema=ON;
                UPDATE sqlite_schema
                SET sql = sql || ' /*' || hex(zeroblob(1048576)) || '*/'
                WHERE type = 'table' AND name = 'ConfigItems';
                PRAGMA writable_schema=OFF;
                PRAGMA schema_version=5000;
                """);

            await AssertFailureAsync(source, "schema-drift");
        }

        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            var objects = string.Join(
                ";",
                Enumerable.Range(0, 1024).Select(index =>
                    $"CREATE TABLE \"AttackerObject{index:D4}\"(value TEXT)"));
            await source.ExecuteAsync(objects + ";");

            await AssertFailureAsync(source, "schema-drift");
        }
    }

    [Fact]
    public void SchemaValidatorSource_UsesExpectedOnlyBoundedStreamingReads()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SqliteSchemaManifest.cs"));

        Assert.Contains("expected.Count + 1", source, StringComparison.Ordinal);
        Assert.Contains("expected.Rows.Count + 1", source, StringComparison.Ordinal);
        Assert.Contains("length(CAST(", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var capture in expected.Physical.TableXInfo)", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var capture in expected.Physical.IndexList)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CapturePhysicalAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tableNames", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SerializeToElement(actual", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UPDATE DavItems SET Type = 'not-an-integer' WHERE Id = '00000000-0000-0000-0000-000000000000';", "storage-class")]
    [InlineData("UPDATE HistoryCleanupItems SET DeleteMountedFiles = 2;", "boolean-domain")]
    [InlineData("UPDATE HistoryCleanupItems SET DeleteMountedFiles = -1;", "boolean-domain")]
    [InlineData("UPDATE DavItems SET Type = 2147483648 WHERE Id = '00000000-0000-0000-0000-000000000000';", "int32-range")]
    [InlineData("UPDATE DavItems SET Type = 99 WHERE Id = '00000000-0000-0000-0000-000000000000';", "enum-domain")]
    [InlineData("UPDATE DavItems SET CreatedAt = '2026-01-01 00:00:00.0000001' WHERE Id = '00000000-0000-0000-0000-000000000000';", "timestamp-microseconds")]
    [InlineData("UPDATE DavItems SET CreatedAt = '2026-01-01T00:00:00Z' WHERE Id = '00000000-0000-0000-0000-000000000000';", "timestamp-local-wall")]
    [InlineData("UPDATE DavItems SET CreatedAt = '2026-01-01 00:00:00+04:00' WHERE Id = '00000000-0000-0000-0000-000000000000';", "timestamp-local-wall")]
    public async Task ValidateAsync_RejectsRawStorageRangeEnumBooleanAndTimestampViolations(
        string sql,
        string code)
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        if (sql.Contains("HistoryCleanupItems", StringComparison.Ordinal))
        {
            await source.ExecuteAsync(
                "INSERT INTO HistoryCleanupItems(Id, DeleteMountedFiles) VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 0);");
        }
        await source.ExecuteAsync(sql);

        await AssertFailureAsync(source, code);
    }

    [Fact]
    public async Task ValidateAsync_AcceptsUppercaseUuidAndRejectsNormalizedCollision()
    {
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                "INSERT INTO BlobCleanupItems(Id) VALUES ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA');");
            var result = await source.ValidateAsync();
            Assert.Equal(1, result.Tables.Single(table => table.Name == "BlobCleanupItems").RowCount);
        }

        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                """
                INSERT INTO BlobCleanupItems(Id) VALUES ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA');
                INSERT INTO BlobCleanupItems(Id) VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
                """);
            await AssertFailureAsync(source, "uuid-normalized-collision");
        }
    }

    [Theory]
    [InlineData("CAST(X'80' AS TEXT)", "utf8")]
    [InlineData("CAST(X'610062' AS TEXT)", "text-nul")]
    public async Task ValidateAsync_RejectsInvalidUtf8AndNulWithoutLeakingRawValue(
        string expression,
        string code)
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string secret = "transfer-secret-must-not-leak";
        await source.ExecuteAsync(
            $"INSERT INTO ConfigItems(ConfigName, ConfigValue) VALUES ('{secret}', {expression});");

        var exception = await AssertFailureAsync(source, code);

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.InRange(exception.Message.Length, 1, 512);
        Assert.Matches("table=ConfigItems column=ConfigValue row=[0-9]+ digest=[0-9a-f]{12}", exception.Message);
    }

    [Fact]
    public async Task ValidateAsync_UsesUnicodeScalarCountForVarcharLimits()
    {
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            var value = string.Concat(Enumerable.Repeat("😀", 255));
            await source.ExecuteAsync(
                $"INSERT INTO Accounts(Type, Username, PasswordHash, RandomSalt) VALUES (1, '{value}', 'hash', 'salt');");
            await source.ValidateAsync();
        }

        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            var value = new string('x', 256);
            await source.ExecuteAsync(
                $"INSERT INTO Accounts(Type, Username, PasswordHash, RandomSalt) VALUES (1, '{value}', 'hash', 'salt');");
            await AssertFailureAsync(source, "varchar-runes");
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsForeignKeyAndBootstrapViolations()
    {
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                "PRAGMA foreign_keys=OFF; "
                + "INSERT INTO QueueNzbContents(Id, NzbContents) VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '<nzb/>');");
            await AssertFailureAsync(source, "foreign-key");
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync("DELETE FROM ConfigItems WHERE ConfigName = 'api.key';");
            var exception = await AssertFailureAsync(source, "bootstrap-config");
            Assert.Contains("table=ConfigItems", exception.Message, StringComparison.Ordinal);
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync("UPDATE ConfigItems SET ConfigValue = '' WHERE ConfigName = 'api.strm-key';");
            await AssertFailureAsync(source, "bootstrap-config");
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                "UPDATE ConfigItems SET ConfigValue = (SELECT ConfigValue FROM ConfigItems WHERE ConfigName = 'api.key') WHERE ConfigName = 'api.strm-key';");
            await AssertFailureAsync(source, "bootstrap-config");
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                "UPDATE DavItems SET Path = '/changed' WHERE Id = '00000000-0000-0000-0000-000000000002';");
            var exception = await AssertFailureAsync(source, "bootstrap-root");
            Assert.Contains("table=DavItems", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsBootstrapSecretWithRegexAnchorSuffix()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            "UPDATE ConfigItems SET ConfigValue = ConfigValue || char(10) "
            + "WHERE ConfigName = 'api.key';");

        await AssertFailureAsync(source, "bootstrap-config");
    }

    [Fact]
    public async Task ValidateAsync_UsesConnectionOwnedUnnamedTempIndexAndReadOnlySource()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await using (var connection = await TransferV3SqlitePreflightValidator.OpenPrivateIndexAsync(
                         CancellationToken.None))
        {
            await using (var databaseList = connection.CreateCommand())
            {
                databaseList.CommandText =
                    "SELECT file FROM pragma_database_list WHERE name = 'scratch';";
                Assert.Equal("", await databaseList.ExecuteScalarAsync());
                databaseList.CommandText =
                    "SELECT count(*) FROM main.sqlite_schema WHERE type = 'table';";
                Assert.Equal(0L, Convert.ToInt64(await databaseList.ExecuteScalarAsync()));
                databaseList.CommandText =
                    "SELECT group_concat(name, ',') FROM ("
                    + "SELECT name FROM scratch.sqlite_schema WHERE type = 'table' ORDER BY name);";
                Assert.Equal(
                    "blob_inventory,normalized_keysets,scan_ordinals,unique_values,uuid_values",
                    await databaseList.ExecuteScalarAsync());
            }
            await TransferV3SqlitePreflightValidator.AttachReadOnlySourceAsync(
                connection, source.DatabasePath, CancellationToken.None);
            Assert.Equal(0, SQLitePCL.raw.sqlite3_db_readonly(connection.Handle, "scratch"));
            Assert.Equal(1, SQLitePCL.raw.sqlite3_db_readonly(connection.Handle, "source"));
            await using (var writeSource = connection.CreateCommand())
            {
                writeSource.CommandText =
                    "UPDATE source.ConfigItems SET ConfigValue = 'changed' WHERE ConfigName = 'api.key';";
                var exception = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
                    () => writeSource.ExecuteNonQueryAsync());
                Assert.Contains("readonly", exception.Message, StringComparison.OrdinalIgnoreCase);
            }

            await using var transaction = await connection.BeginTransactionAsync();
            await using var spill = connection.CreateCommand();
            spill.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            spill.CommandText =
                """
                CREATE TABLE scratch.spill(value BLOB NOT NULL);
                WITH RECURSIVE rows(value) AS (
                    SELECT 1 UNION ALL SELECT value + 1 FROM rows WHERE value < 12000
                )
                INSERT INTO scratch.spill(value) SELECT zeroblob(1024) FROM rows;
                """;
            await spill.ExecuteNonQueryAsync();
            spill.CommandText = "PRAGMA scratch.page_count;";
            var pageCount = Convert.ToInt64(await spill.ExecuteScalarAsync());
            spill.CommandText = "PRAGMA scratch.page_size;";
            var pageSize = Convert.ToInt64(await spill.ExecuteScalarAsync());
            Assert.True(pageCount * pageSize > 8 * 1024 * 1024);
            spill.CommandText = "SELECT count(*) FROM source.ConfigItems;";
            Assert.True(Convert.ToInt64(await spill.ExecuteScalarAsync()) > 0);
            await transaction.CommitAsync();
        }

        await using var fresh = await TransferV3SqlitePreflightValidator.OpenPrivateIndexAsync(
            CancellationToken.None);
        await using var noResidue = fresh.CreateCommand();
        noResidue.CommandText = "SELECT count(*) FROM scratch.sqlite_schema WHERE name = 'spill';";
        Assert.Equal(0L, Convert.ToInt64(await noResidue.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ValidateAsync_ThrowingProgressAndParallelRunsHaveNoSharedScratchState()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var progress = new ImmediateProgress<TransferV3ValidationProgress>(value =>
        {
            if (value.Phase == "private-index-ready")
                throw new ProgressProbeException();
        });

        await Assert.ThrowsAsync<ProgressProbeException>(() =>
            source.ValidateAsync(source.Options(Progress: progress)));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => source.ValidateAsync()));
        Assert.All(results, result =>
            Assert.Equal(5, result.Tables.Single(table => table.Name == "DavItems").RowCount));
    }

    [Fact]
    public async Task ProviderContract_EmptyMainDoesNotEnableReadOnlyAttachUri()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = "",
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var attach = connection.CreateCommand();
        attach.CommandText = "ATTACH DATABASE $path AS source;";
        var escaped = new Uri(source.DatabasePath).GetComponents(
            UriComponents.Path, UriFormat.UriEscaped);
        attach.Parameters.AddWithValue(
            "$path", "file:/" + escaped.TrimStart('/') + "?mode=ro");

        var exception = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => attach.ExecuteNonQueryAsync());
        Assert.Equal(14, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task ValidateAsync_NeverTouchesCallerPathAcrossReplacementFailureAndCancellation()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var external = Path.Combine(Path.GetTempPath(), $"nzbdav-transfer-canary-{Guid.NewGuid():N}");
        var decoy = Path.Combine(source.ValidationWorkspaceRoot, "validate-race");
        var renamed = Path.Combine(source.ValidationWorkspaceRoot, "validate-renamed");
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(decoy);
        var canary = Path.Combine(external, "canary");
        await File.WriteAllTextAsync(canary, "must-survive");
        try
        {
            var replace = new ImmediateProgress<TransferV3ValidationProgress>(value =>
            {
                if (value.Phase != "private-index-ready") return;
                Directory.Move(decoy, renamed);
                Directory.CreateSymbolicLink(decoy, external);
            });
            await source.ValidateAsync(source.Options(Progress: replace));
            Assert.Equal("must-survive", await File.ReadAllTextAsync(canary));

            await source.ExecuteAsync(
                "UPDATE ConfigItems SET ConfigValue = '' WHERE ConfigName = 'api.key';");
            await AssertFailureAsync(source, "bootstrap-config");
            Assert.Equal("must-survive", await File.ReadAllTextAsync(canary));

            using var cancellation = new CancellationTokenSource();
            var cancel = new ImmediateProgress<TransferV3ValidationProgress>(value =>
            {
                if (value.Phase == "private-index-ready") cancellation.Cancel();
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                source.ValidateAsync(source.Options(Progress: cancel), cancellation.Token));
            Assert.Equal("must-survive", await File.ReadAllTextAsync(canary));
        }
        finally
        {
            if (Directory.Exists(decoy) || File.Exists(decoy)) Directory.Delete(decoy);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public void ValidatorSource_HasNoCallerControlledWorkspaceOrPathCleanup()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SqlitePreflightValidator.cs"));

        Assert.DoesNotContain("ValidationWorkspaceRoot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateIndexPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDeletePrivateContents", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.EnumerateFiles", source, StringComparison.Ordinal);
        Assert.Contains("ATTACH DATABASE '' AS scratch", source, StringComparison.Ordinal);
        Assert.Contains("Pooling = false", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Result", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait(", source, StringComparison.Ordinal);

        foreach (var path in new[]
                 {
                     "backend/Database/Transfer/TransferV3SqliteRawScanner.cs",
                     "backend/Database/Transfer/TransferV3ReferenceValidator.cs",
                     "backend/Database/Transfer/TransferV3BlobInventoryScanner.cs",
                 })
        {
            var implementation = File.ReadAllText(RepositoryPath(path));
            foreach (var table in new[]
                     {
                         "normalized_keysets", "uuid_values", "unique_values", "scan_ordinals",
                         "blob_inventory",
                     })
            {
                Assert.DoesNotContain($"main.{table}", implementation, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsRequiredNullEvenWhenPhysicalSchemaTextIsRestored()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            PRAGMA writable_schema=ON;
            UPDATE sqlite_schema
            SET sql = replace(sql, '"ConfigValue" TEXT NOT NULL', '"ConfigValue" TEXT')
            WHERE type='table' AND name='ConfigItems';
            PRAGMA writable_schema=OFF;
            PRAGMA schema_version=4000;
            """);
        await source.ExecuteAsync(
            "INSERT INTO ConfigItems(ConfigName, ConfigValue) VALUES ('nullable-corruption', NULL);");
        await source.ExecuteAsync(
            """
            PRAGMA writable_schema=ON;
            UPDATE sqlite_schema
            SET sql = replace(sql, '"ConfigValue" TEXT', '"ConfigValue" TEXT NOT NULL')
            WHERE type='table' AND name='ConfigItems';
            PRAGMA writable_schema=OFF;
            PRAGMA schema_version=4001;
            """);

        await AssertFailureAsync(source, "required-null");
    }

    [Fact]
    public async Task ValidateAsync_UsesNormalizedUuidForCompositeUniqueKeysAcrossBatchBoundaries()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            INSERT INTO DavItems(Id, CreatedAt, ParentId, Name, Type, SubType, Path, IdPrefix)
            VALUES ('abcdefab-cdef-abcd-efab-cdefabcdefab', '2026-01-01 00:00:00',
                    '00000000-0000-0000-0000-000000000000', 'parent', 1, 101, '/parent', 'abcde');
            INSERT INTO DavItems(Id, CreatedAt, ParentId, Name, Type, SubType, Path, IdPrefix)
            VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1', '2026-01-01 00:00:00',
                    'abcdefab-cdef-abcd-efab-cdefabcdefab', 'collision', 1, 101, '/collision-a', 'aaaaa');
            INSERT INTO DavItems(Id, CreatedAt, ParentId, Name, Type, SubType, Path, IdPrefix)
            VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2', '2026-01-01 00:00:00',
                    'ABCDEFAB-CDEF-ABCD-EFAB-CDEFABCDEFAB', 'collision', 1, 101, '/collision-b', 'aaaab');
            """);

        await AssertFailureAsync(source, "unique-normalized-collision");
    }

    [Fact]
    public async Task ValidateAsync_StateAwareParentAndHistoryReferencesRequireLiveRowsOrTombstones()
    {
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                """
                INSERT INTO DavItems(Id, CreatedAt, ParentId, Name, Type, SubType, Path, IdPrefix)
                VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '2026-01-01 00:00:00',
                        'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'orphan', 1, 101, '/orphan', 'aaaaa');
                """);
            await AssertFailureAsync(source, "reference-state");
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                """
                INSERT INTO DavItems(Id, CreatedAt, ParentId, Name, Type, SubType, Path, IdPrefix, HistoryItemId)
                VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '2026-01-01 00:00:00',
                        'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'tombstoned', 1, 101, '/tombstoned', 'aaaaa',
                        'cccccccc-cccc-cccc-cccc-cccccccccccc');
                INSERT INTO DavCleanupItems(Id) VALUES ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');
                INSERT INTO HistoryCleanupItems(Id, DeleteMountedFiles)
                VALUES ('cccccccc-cccc-cccc-cccc-cccccccccccc', 0);
                """);
            await source.ValidateAsync();
        }
    }

    [Fact]
    public async Task ValidateAsync_RequiresSubtypeMetadataUnlessMigratedFileBlobExists()
    {
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.InsertUsenetDavItemAsync(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", subType: 201, fileBlobId: null);
            await AssertFailureAsync(source, "metadata-source");
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
            await source.InsertUsenetDavItemAsync(id, subType: 201, fileBlobId: null);
            await source.ExecuteAsync($"INSERT INTO DavNzbFiles(Id, SegmentIds) VALUES ('{id}', '[]');");
            await source.ValidateAsync();
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
            await source.InsertUsenetDavItemAsync(id, subType: 201, fileBlobId: null);
            await source.ExecuteAsync($"INSERT INTO DavRarFiles(Id, RarParts) VALUES ('{id}', '[]');");
            await AssertFailureAsync(source, "metadata-subtype");
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
            const string blob = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
            await source.InsertUsenetDavItemAsync(id, subType: 201, fileBlobId: blob);
            await source.WriteBlobAsync(blob, "migrated-metadata"u8.ToArray());
            await source.ValidateAsync();
        }
    }

    [Theory]
    [InlineData(1, 201)]
    [InlineData(1, 202)]
    [InlineData(1, 203)]
    [InlineData(2, 101)]
    [InlineData(2, 102)]
    [InlineData(2, 103)]
    [InlineData(2, 104)]
    [InlineData(2, 105)]
    [InlineData(2, 106)]
    public async Task ValidateAsync_EnforcesBidirectionalDavItemTypeSubtypeDomain(
        int type,
        int subType)
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            $"""
            INSERT INTO DavItems(Id, CreatedAt, ParentId, Name, Type, SubType, Path, IdPrefix)
            VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '2026-01-01 00:00:00',
                    '00000000-0000-0000-0000-000000000002', 'mismatch',
                    {type}, {subType}, '/content/mismatch', 'aaaaa');
            """);

        var exception = await AssertFailureAsync(source, "type-subtype-domain");
        Assert.Contains("row=6", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_MetadataReferenceValidationRemainsBoundedAcrossManyRows()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            WITH RECURSIVE numbers(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM numbers WHERE value < 129
            )
            INSERT INTO DavItems(Id, CreatedAt, ParentId, Name, Type, SubType, Path, IdPrefix)
            SELECT printf('10000000-0000-0000-0000-%012x', value),
                   '2026-01-01 00:00:00',
                   '00000000-0000-0000-0000-000000000002',
                   printf('media-%d.mkv', value),
                   2,
                   201,
                   printf('/content/media-%d.mkv', value),
                   '10000'
            FROM numbers;
            INSERT INTO DavNzbFiles(Id, SegmentIds)
            SELECT Id, '[]' FROM DavItems WHERE IdPrefix = '10000';
            """);

        var result = await source.ValidateAsync(source.Options(MaxRowsPerBatch: 7));

        Assert.Equal(134, result.Tables.Single(table => table.Name == "DavItems").RowCount);
    }

    [Fact]
    public async Task ValidateAsync_RequiresQueueAndLiveNzbReferencesToHaveBlobAndName()
    {
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.InsertQueueItemAsync("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            await AssertFailureAsync(source, "reference-hard");
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
            await source.InsertQueueItemAsync(id);
            await source.ExecuteAsync($"INSERT INTO NzbNames(Id, FileName) VALUES ('{id}', 'release.nzb');");
            await source.WriteBlobAsync(id, "nzb"u8.ToArray());
            await source.ValidateAsync();
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
            await source.ExecuteAsync(
                $"""
                INSERT INTO HistoryItems(Id, CreatedAt, FileName, JobName, Category, DownloadStatus,
                                         TotalSegmentBytes, DownloadTimeSeconds, NzbBlobId)
                VALUES ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '2026-01-01 00:00:00',
                        'release.nzb', 'release', 'movies', 1, 1, 1, '{id}');
                INSERT INTO NzbNames(Id, FileName) VALUES ('{id}', 'release.nzb');
                """);
            await AssertFailureAsync(source, "reference-hard");
        }
    }

    [Fact]
    public async Task ValidateAsync_AllowsCrashSafeNameCleanupOnlyWithoutLiveReferences()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await source.ExecuteAsync(
            $"""
            INSERT INTO NzbNames(Id, FileName) VALUES ('{id}', 'orphan.nzb');
            INSERT INTO NzbBlobCleanupItems(Id) VALUES ('{id}');
            """);

        await source.ValidateAsync();
    }

    [Fact]
    public async Task ValidateAsync_RecordsInformationalReferenceAggregateWithoutRawIdentifiers()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            INSERT INTO HealthCheckResults(Id, CreatedAt, DavItemId, Path, Result, RepairStatus)
            VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 0,
                    'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '/historical', 0, 0);
            """);

        var result = await source.ValidateAsync();
        var summary = result.InformationalReferences.Single(
            value => value.Name == "HealthCheckResults_DavItemId");

        Assert.Equal(1, summary.UnresolvedCount);
        Assert.Matches("^[0-9a-f]{64}$", summary.UnresolvedSha256);
        Assert.DoesNotContain("bbbbbbbb", summary.UnresolvedSha256, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_InformationalDigestCoversCanonicalOwnerAndReferenceTuple()
    {
        string firstDigest;
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                """
                INSERT INTO HealthCheckResults(Id, CreatedAt, DavItemId, Path, Result, RepairStatus)
                VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 0,
                        'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '/historical-a', 0, 0);
                """);
            firstDigest = (await source.ValidateAsync()).InformationalReferences.Single(
                value => value.Name == "HealthCheckResults_DavItemId").UnresolvedSha256;
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                """
                INSERT INTO HealthCheckResults(Id, CreatedAt, DavItemId, Path, Result, RepairStatus)
                VALUES ('cccccccc-cccc-cccc-cccc-cccccccccccc', 0,
                        'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', '/historical-c', 0, 0);
                """);
            var secondDigest = (await source.ValidateAsync()).InformationalReferences.Single(
                value => value.Name == "HealthCheckResults_DavItemId").UnresolvedSha256;
            Assert.NotEqual(firstDigest, secondDigest);
        }
    }

    [Fact]
    public async Task ValidateAsync_WorkerJobPolymorphicSummaryUsesKindSpecificPrincipal()
    {
        const string target = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                $"""
                INSERT INTO DavItems(Id, CreatedAt, ParentId, Name, Type, SubType, Path, IdPrefix)
                VALUES ('{target}', '2026-01-01 00:00:00',
                        '00000000-0000-0000-0000-000000000002', 'directory', 1, 101,
                        '/content/directory', 'aaaaa');
                {WorkerJobSql("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", kind: 1, target)}
                """);

            var summary = (await source.ValidateAsync()).InformationalReferences.Single(
                value => value.Name == "WorkerJobs_TargetId");
            Assert.Equal(1, summary.UnresolvedCount);
        }

        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.InsertValidQueueItemAsync(target);
            await source.ExecuteAsync(WorkerJobSql(
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                kind: 2,
                target));

            var summary = (await source.ValidateAsync()).InformationalReferences.Single(
                value => value.Name == "WorkerJobs_TargetId");
            Assert.Equal(1, summary.UnresolvedCount);
        }
    }

    [Fact]
    public async Task ValidateAsync_WorkerJobPolymorphicDigestIncludesKindAcrossUuidCollisions()
    {
        const string target = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string worker = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        string downloadDigest;
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(WorkerJobSql(worker, kind: 1, target));
            downloadDigest = (await source.ValidateAsync()).InformationalReferences.Single(
                value => value.Name == "WorkerJobs_TargetId").UnresolvedSha256;
        }

        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(WorkerJobSql(worker, kind: 2, target));
            var verifyDigest = (await source.ValidateAsync()).InformationalReferences.Single(
                value => value.Name == "WorkerJobs_TargetId").UnresolvedSha256;
            Assert.NotEqual(downloadDigest, verifyDigest);
        }

        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.InsertValidQueueItemAsync(target);
            await source.ExecuteAsync(
                $"""
                INSERT INTO DavItems(Id, CreatedAt, ParentId, Name, Type, SubType, Path, IdPrefix)
                VALUES ('{target}', '2026-01-01 00:00:00',
                        '00000000-0000-0000-0000-000000000002', 'directory', 1, 101,
                        '/content/directory', 'aaaaa');
                {WorkerJobSql(worker, kind: 1, target)}
                {WorkerJobSql("cccccccc-cccc-cccc-cccc-cccccccccccc", kind: 2, target)}
                {WorkerJobSql("dddddddd-dddd-dddd-dddd-dddddddddddd", kind: 3, target)}
                """);

            var summary = (await source.ValidateAsync()).InformationalReferences.Single(
                value => value.Name == "WorkerJobs_TargetId");
            Assert.Equal(0, summary.UnresolvedCount);
        }
    }

    [Fact]
    public async Task ValidateAsync_InventoriesCanonicalOrphanBlobsAndRejectsUnknownLayout()
    {
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.WriteBlobAsync("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "one"u8.ToArray());
            await source.WriteBlobAsync("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "two"u8.ToArray());

            var result = await source.ValidateAsync();

            Assert.Equal(2, result.Blobs.Count);
            Assert.Equal(6, result.Blobs.TotalBytes);
            Assert.Matches("^[0-9a-f]{64}$", result.Blobs.Sha256);
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await File.WriteAllTextAsync(Path.Combine(source.BlobRootPath, "unexpected"), "secret");
            await AssertFailureAsync(source, "blob-layout");
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsDuplicateBlobIdentityAcrossShards()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string firstId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string aliasId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        await source.WriteBlobAsync(firstId, "shared-inode"u8.ToArray());
        var aliasDirectory = Path.Combine(source.BlobRootPath, "bb", "bb");
        Directory.CreateDirectory(aliasDirectory);
        await RunProcessAsync(
            "ln",
            source.BlobPath(firstId),
            Path.Combine(aliasDirectory, aliasId));

        await AssertFailureAsync(source, "blob-layout");
    }

    [Fact]
    public async Task ValidateAsync_AllowsSoleBlobIdentityWithExternalHardLink()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await source.WriteBlobAsync(id, "external-link"u8.ToArray());
        var external = Path.Combine(Path.GetDirectoryName(source.BlobRootPath)!, "external-link");
        await RunProcessAsync("ln", source.BlobPath(id), external);

        var result = await source.ValidateAsync();

        Assert.Equal(1, result.Blobs.Count);
        Assert.Equal("external-link"u8.Length, result.Blobs.TotalBytes);
        Assert.True(File.Exists(external));
    }

    [Fact]
    public async Task ValidateAsync_RejectsBlobSymlinkWithoutReadingItsTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var target = Path.Combine(Path.GetDirectoryName(source.BlobRootPath)!, "outside-secret");
        await File.WriteAllTextAsync(target, "outside");
        var directory = Path.Combine(source.BlobRootPath, "aa", "aa");
        Directory.CreateDirectory(directory);
        File.CreateSymbolicLink(Path.Combine(directory, id), target);

        await AssertFailureAsync(source, "blob-layout");
    }

    [Fact]
    public async Task ValidateAsync_RejectsFifoBlobWithoutBlockingOrReadingIt()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var directory = Path.Combine(source.BlobRootPath, "aa", "aa");
        Directory.CreateDirectory(directory);
        var fifo = Path.Combine(directory, id);
        await RunProcessAsync("mkfifo", fifo);

        using var writer = StartFifoWriter(fifo);
        try
        {
            await AssertFailureAsync(source, "blob-layout");
        }
        finally
        {
            if (!writer.HasExited)
                writer.Kill(entireProcessTree: true);
            await writer.WaitForExitAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateAsync_RejectsCoordinatedInPlaceBlobMutationAfterFirstChunk(bool truncate)
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var original = Enumerable.Repeat((byte)'a', 64 * 1024).ToArray();
        await source.WriteBlobAsync(id, original);
        var path = source.BlobPath(id);
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddDays(1));
        var mutated = false;
        var progress = new ImmediateProgress<TransferV3ValidationProgress>(value =>
        {
            if (mutated || value.Phase != "blob-first-chunk-read") return;
            mutated = true;
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            if (truncate)
            {
                writer.SetLength(1);
            }
            else
            {
                writer.Write(Enumerable.Repeat((byte)'b', original.Length).ToArray());
            }
            writer.Flush(flushToDisk: true);
        });

        await AssertFailureAsync(source, "blob-layout", source.Options(Progress: progress));
        Assert.True(mutated);
    }

    [Fact]
    public async Task ValidateAsync_RejectsBlobEntryReplacementBetweenEnumerationAndOpen()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await source.WriteBlobAsync(id, "original"u8.ToArray());
        var path = source.BlobPath(id);
        var outside = Path.Combine(Path.GetDirectoryName(source.BlobRootPath)!, "replaced-original");
        var mutated = false;
        var progress = new ImmediateProgress<TransferV3ValidationProgress>(value =>
        {
            if (mutated || value.Phase != "blob-entry-enumerated") return;
            mutated = true;
            File.Move(path, outside);
            File.WriteAllBytes(path, "replacement"u8.ToArray());
        });

        await AssertFailureAsync(source, "blob-layout", source.Options(Progress: progress));
        Assert.True(mutated);
    }

    [Fact]
    public void PosixFingerprintDetectsSameSizeInPlaceFileAndDirectoryMutation()
    {
        if (!TransferV3Posix.IsSupported) return;
        var root = Path.Combine(
            Path.GetDirectoryName(RepositoryPath("backend/NzbWebDAV.csproj"))!,
            $".nzbdav-transfer-v3-fingerprint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "file");
            File.WriteAllText(path, "one");
            File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddDays(1));
            using var directory = TransferV3Posix.OpenDirectory(root);
            var directoryBefore = TransferV3Posix.GetFingerprint(directory);
            using var file = TransferV3Posix.OpenReadOnlyRegularFileAt(directory, "file");
            var fileBefore = TransferV3Posix.GetFingerprint(file);

            File.WriteAllText(path, "two");
            File.WriteAllText(Path.Combine(root, "added"), "entry");
            var fileAfter = TransferV3Posix.GetFingerprint(file);
            var directoryAfter = TransferV3Posix.GetFingerprint(directory);

            Assert.Equal(fileBefore.Identity, fileAfter.Identity);
            Assert.Equal(fileBefore.Size, fileAfter.Size);
            Assert.NotEqual(fileBefore, fileAfter);
            Assert.Equal(directoryBefore.Identity, directoryAfter.Identity);
            Assert.NotEqual(directoryBefore, directoryAfter);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PosixRegularOpen_RejectsEntrySwappedToSymlinkAfterNameEnumeration()
    {
        if (!TransferV3Posix.IsSupported) return;
        var root = Path.Combine(
            Path.GetDirectoryName(RepositoryPath("backend/NzbWebDAV.csproj"))!,
            $".nzbdav-transfer-v3-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string name = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
            var entry = Path.Combine(root, name);
            var outside = Path.Combine(root, "outside");
            File.WriteAllText(entry, "original");
            File.WriteAllText(outside, "outside-secret");
            Assert.Contains(
                name,
                Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName));
            File.Delete(entry);
            File.CreateSymbolicLink(entry, outside);
            using var directory = TransferV3Posix.OpenDirectory(root);
            var method = typeof(TransferV3Posix).GetMethod(
                "OpenReadOnlyRegularFileAt",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

            Assert.NotNull(method);
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => method.Invoke(null, [directory, name]));
            Assert.IsAssignableFrom<IOException>(exception.InnerException);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PosixRegularOpen_RejectsCharacterDevice()
    {
        if (!TransferV3Posix.IsSupported || !File.Exists("/dev/null")) return;
        using var directory = TransferV3Posix.OpenDirectory("/dev");
        var method = typeof(TransferV3Posix).GetMethod(
            "OpenReadOnlyRegularFileAt",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        var exception = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => method.Invoke(null, [directory, "null"]));
        Assert.IsAssignableFrom<IOException>(exception.InnerException);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void PosixDirectoryOpen_RejectsNonChildComponents(string name)
    {
        if (!TransferV3Posix.IsSupported) return;
        var root = Path.GetDirectoryName(RepositoryPath("backend/NzbWebDAV.csproj"))!;
        using var directory = TransferV3Posix.OpenDirectory(root);

        Assert.Throws<IOException>(() =>
        {
            using var escaped = TransferV3Posix.OpenDirectoryAt(directory, name);
        });
    }

    [Fact]
    public void BlobInventorySource_UsesBoundedPinnedDescriptorTraversal()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3BlobInventoryScanner.cs"));

        Assert.DoesNotContain(".OrderBy(", source, StringComparison.Ordinal);
        Assert.Contains("TransferV3Posix.EnumerateDirectoryNames", source, StringComparison.Ordinal);
        Assert.Contains("TransferV3Posix.OpenReadOnlyRegularFileAt", source, StringComparison.Ordinal);
        Assert.Contains("TransferV3Posix.GetFingerprint", source, StringComparison.Ordinal);
        Assert.Contains("blob-first-chunk-read", source, StringComparison.Ordinal);
        Assert.Contains("blob-entry-enumerated", source, StringComparison.Ordinal);
        Assert.Contains("TransferV3Posix.IsSupported", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileInfo", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_StreamsOversizedTextWithDualBudgetsWithoutWholeTableMaterialization()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        const string id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await source.InsertQueueItemAsync(id);
        await source.ExecuteAsync($"INSERT INTO NzbNames(Id, FileName) VALUES ('{id}', 'large.nzb');");
        await source.WriteBlobAsync(id, "nzb"u8.ToArray());
        await source.InsertQueueContentsAsync(id, new string('x', 1024 * 1024));

        var result = await source.ValidateAsync(source.Options(MaxRowsPerBatch: 3, MaxBytesPerBatch: 1024));

        Assert.Equal(1, result.Tables.Single(table => table.Name == "QueueNzbContents").RowCount);
        Assert.True(result.MaxObservedBytesPerBatch >= 1024 * 1024);
        Assert.Equal(16 * 1024, result.MaxObservedIoBufferBytes);
        Assert.InRange(result.MaxObservedRowsPerBatch, 1, 3);
    }

    [Fact]
    public async Task ValidateAsync_PreservesSourceBytesAndCleansSensitiveIndexAfterFailure()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var sidecars = new[]
        {
            source.DatabasePath + "-wal",
            source.DatabasePath + "-shm",
            source.DatabasePath + "-journal",
        };
        Assert.All(sidecars, path => Assert.False(File.Exists(path)));
        var before = SHA256.HashData(await File.ReadAllBytesAsync(source.DatabasePath));
        await source.ValidateAsync();
        var after = SHA256.HashData(await File.ReadAllBytesAsync(source.DatabasePath));
        Assert.Equal(before, after);
        Assert.All(sidecars, path => Assert.False(File.Exists(path)));

        await source.ExecuteAsync(
            "INSERT INTO ConfigItems(ConfigName, ConfigValue) VALUES ('bad', CAST(X'80' AS TEXT));");
        await AssertFailureAsync(source, "utf8");
        Assert.All(sidecars, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task ValidateAsync_RefusesRetainedWalWithoutChangingMainOrSidecars()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = source.DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using var writer = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await writer.OpenAsync();
        await using (var command = writer.CreateCommand())
        {
            command.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; "
                + "UPDATE ConfigItems SET ConfigValue = lower(ConfigValue) WHERE ConfigName = 'api.key';";
            await command.ExecuteNonQueryAsync();
        }

        var paths = new[]
        {
            source.DatabasePath,
            source.DatabasePath + "-wal",
            source.DatabasePath + "-shm",
        };
        Assert.All(paths, path => Assert.True(File.Exists(path), path));
        var before = await CaptureFilesAsync(paths);

        await AssertFailureAsync(source, "source-stability");

        var after = await CaptureFilesAsync(paths);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var path in paths) Assert.Equal(before[path], after[path]);
    }

    [Fact]
    public async Task ValidateAsync_RefusesBrokenSymlinkAtSidecarName()
    {
        if (!TransferV3Posix.IsSupported) return;
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var sidecar = source.DatabasePath + "-wal";
        File.CreateSymbolicLink(sidecar, Path.Combine(Path.GetDirectoryName(sidecar)!, "missing-target"));

        await AssertFailureAsync(source, "source-stability");

        Assert.True(File.ResolveLinkTarget(sidecar, returnFinalTarget: false) is not null);
    }

    [Fact]
    public async Task ValidateAsync_RejectsSourceMetadataMutationAfterGuardIsPinned()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var mutated = false;
        var progress = new ImmediateProgress<TransferV3ValidationProgress>(value =>
        {
            if (mutated || value.Phase != "private-index-ready") return;
            mutated = true;
            File.SetLastWriteTimeUtc(source.DatabasePath, DateTime.UnixEpoch.AddDays(2));
        });

        await AssertFailureAsync(source, "source-stability", source.Options(Progress: progress));
        Assert.True(mutated);
    }

    [Fact]
    public async Task ValidateAsync_AttachesLiteralReservedAndUnicodeFilenameSegments()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var path = Path.Combine(
            Path.GetDirectoryName(source.DatabasePath)!,
            "db%2Fcopy %25 ? # ü.sqlite");
        File.Copy(source.DatabasePath, path);

        var result = await new TransferV3SqlitePreflightValidator().ValidateAsync(path, source.Options());

        Assert.Equal(27, result.Tables.Count);
    }

    [Fact]
    public async Task ValidateAsync_RejectsTargetLocalImportStateInSource()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            "INSERT INTO ConfigItems(ConfigName, ConfigValue) "
            + "VALUES ('database.import-state', '{\"formatVersion\":3,\"state\":\"fresh\"}');");

        await AssertFailureAsync(source, "reserved-config");
    }

    [Fact]
    public async Task ValidateAsync_StringKeysetUsesOrdinalUtf8OrderWithoutSkippingDistinctUnicode()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            INSERT INTO Accounts(Type, Username, PasswordHash, RandomSalt) VALUES (1, 'A', 'h', 's');
            INSERT INTO Accounts(Type, Username, PasswordHash, RandomSalt) VALUES (1, 'a', 'h', 's');
            INSERT INTO Accounts(Type, Username, PasswordHash, RandomSalt) VALUES (1, 'é', 'h', 's');
            INSERT INTO Accounts(Type, Username, PasswordHash, RandomSalt) VALUES (1, 'é', 'h', 's');
            """);

        var result = await source.ValidateAsync(source.Options(MaxRowsPerBatch: 1));
        var accounts = result.Tables.Single(table => table.Name == "Accounts");

        Assert.Equal(4, accounts.RowCount);
        Assert.Contains("COLLATE \"BINARY\"", accounts.SqliteOrderExpression, StringComparison.Ordinal);
        Assert.Contains("COLLATE \"C\"", accounts.PostgreSqlOrderExpression, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_RejectsKeysetTextBeyondBoundWithRedactedError()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.InsertConfigAsync(new string('k', 64 * 1024 + 1), "value");

        var exception = await AssertFailureAsync(source, "keyset-text-bytes");

        Assert.InRange(exception.Message.Length, 1, 512);
        Assert.DoesNotContain(new string('k', 64), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RawValidatorSource_HasNoEfMaterializationOrOffsetPagination()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SqliteRawScanner.cs"));
        var validator = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SqlitePreflightValidator.cs"));
        var posix = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3Posix.cs"));

        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(
            @"\bOFFSET\s+(?:\$|\d)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant), source);
        Assert.DoesNotContain("DbContext", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EntityFrameworkCore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DbContext", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildSqliteFileUri", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("Uri.EscapeDataString", validator, StringComparison.Ordinal);
        Assert.Contains("ProbeSqliteDescriptorUri", validator, StringComparison.Ordinal);
        Assert.Contains("ATTACH DATABASE $path AS source", validator, StringComparison.Ordinal);
        Assert.Contains("mode=ro&immutable=1&cache=private", posix, StringComparison.Ordinal);
        Assert.Contains("/proc/self/fd/", posix, StringComparison.Ordinal);
        Assert.Contains("/dev/fd/", posix, StringComparison.Ordinal);
        var sourceGuard = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3SqliteSourceGuard.cs"));
        Assert.Contains("EntryExistsNoFollow", sourceGuard, StringComparison.Ordinal);
        Assert.Contains("VerifyUnchanged", sourceGuard, StringComparison.Ordinal);
        Assert.Contains("SELECT count(*) FROM source.", source, StringComparison.Ordinal);
        Assert.Contains("\"row-coverage\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceValidatorSource_DoesNotBufferAllUsenetMetadataRows()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/TransferV3ReferenceValidator.cs"));

        Assert.DoesNotContain("new List<(byte[] Id", source, StringComparison.Ordinal);
        Assert.DoesNotContain("rows.Add(", source, StringComparison.Ordinal);
        Assert.Contains("ORDER BY ordinal.ordinal LIMIT 1", source, StringComparison.Ordinal);
        Assert.Contains("scratch.scan_ordinals", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_NormalizedUuidKeysetNeverSkipsMixedCaseBatchBoundaries()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            INSERT INTO BlobCleanupItems(Id) VALUES ('00000000-0000-0000-0000-000000000010');
            INSERT INTO BlobCleanupItems(Id) VALUES ('00000000-0000-0000-0000-00000000000F');
            INSERT INTO BlobCleanupItems(Id) VALUES ('00000000-0000-0000-0000-000000000020');
            INSERT INTO BlobCleanupItems(Id) VALUES ('00000000-0000-0000-0000-000000000001');
            INSERT INTO BlobCleanupItems(Id) VALUES ('00000000-0000-0000-0000-0000000000AA');
            """);

        var result = await source.ValidateAsync(source.Options(MaxRowsPerBatch: 1));

        Assert.Equal(5, result.Tables.Single(table => table.Name == "BlobCleanupItems").RowCount);
        Assert.Equal(1, result.MaxObservedRowsPerBatch);
    }

    [Fact]
    public async Task ValidateAsync_UuidKeysetCoversMinimumNegativeAndZeroExplicitRowids()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            INSERT INTO BlobCleanupItems(rowid, Id)
            VALUES (CAST('-9223372036854775808' AS INTEGER), '00000000-0000-0000-0000-000000000001');
            INSERT INTO BlobCleanupItems(rowid, Id)
            VALUES (-1, '00000000-0000-0000-0000-000000000002');
            INSERT INTO BlobCleanupItems(rowid, Id)
            VALUES (0, '00000000-0000-0000-0000-000000000003');
            INSERT INTO BlobCleanupItems(rowid, Id)
            VALUES (1, '00000000-0000-0000-0000-000000000004');
            """);

        var result = await source.ValidateAsync(source.Options(MaxRowsPerBatch: 1));

        Assert.Equal(4, result.Tables.Single(table => table.Name == "BlobCleanupItems").RowCount);
    }

    [Fact]
    public async Task ValidateAsync_UuidCollisionReportsItsOwnDiscoveryOrdinalNotLaterPrefetchedRow()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            INSERT INTO BlobCleanupItems(rowid, Id)
            VALUES (10, 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
            INSERT INTO BlobCleanupItems(rowid, Id)
            VALUES (20, 'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA');
            INSERT INTO BlobCleanupItems(rowid, Id)
            VALUES (30, 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb');
            """);

        var exception = await AssertFailureAsync(
            source,
            "uuid-normalized-collision",
            source.Options(MaxRowsPerBatch: 3));
        Assert.Contains("row=2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_ForeignKeyReferenceAndMetadataFailuresReportCanonicalOrdinals()
    {
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                """
                PRAGMA foreign_keys=OFF;
                INSERT INTO QueueNzbContents(Id, NzbContents)
                VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '<nzb/>');
                """);
            var exception = await AssertFailureAsync(source, "foreign-key");
            Assert.Contains("row=1", exception.Message, StringComparison.Ordinal);
        }

        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                """
                INSERT INTO DavItems(Id, CreatedAt, ParentId, Name, Type, SubType, Path, IdPrefix)
                VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', '2026-01-01 00:00:00',
                        'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'orphan', 1, 101,
                        '/orphan', 'aaaaa');
                """);
            var exception = await AssertFailureAsync(source, "reference-state");
            Assert.Contains("row=6", exception.Message, StringComparison.Ordinal);
        }

        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.InsertUsenetDavItemAsync(
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                subType: 201,
                fileBlobId: null);
            var exception = await AssertFailureAsync(source, "metadata-source");
            Assert.Contains("row=6", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ValidateAsync_ForeignKeyFailureSelectionIsCanonicalAcrossPhysicalInsertionOrder()
    {
        const string first = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string second = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        string firstMessage;
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                $"PRAGMA foreign_keys=OFF; "
                + $"INSERT INTO QueueNzbContents(Id, NzbContents) VALUES ('{second}', '<nzb/>'); "
                + $"INSERT INTO QueueNzbContents(Id, NzbContents) VALUES ('{first}', '<nzb/>');");
            firstMessage = (await AssertFailureAsync(source, "foreign-key")).Message;
        }

        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                $"PRAGMA foreign_keys=OFF; "
                + $"INSERT INTO QueueNzbContents(Id, NzbContents) VALUES ('{first}', '<nzb/>'); "
                + $"INSERT INTO QueueNzbContents(Id, NzbContents) VALUES ('{second}', '<nzb/>');");
            var secondMessage = (await AssertFailureAsync(source, "foreign-key")).Message;
            Assert.Equal(firstMessage, secondMessage);
            Assert.Contains("row=1", secondMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ValidateAsync_DerivedRowidScanCoversMinimumNegativeAndZeroExplicitRowids()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(
            """
            INSERT INTO HealthCheckResults(Id, CreatedAt, DavItemId, Path, Result, RepairStatus)
            VALUES
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1', 0,
               '00000000-0000-0000-0000-000000000000', '/a', 0, 0),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2', 86400,
               '00000000-0000-0000-0000-000000000000', '/b', 0, 0),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3', 172800,
               '00000000-0000-0000-0000-000000000000', '/c', 0, 0),
              ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4', 259200,
               '00000000-0000-0000-0000-000000000000', '/d', 0, 0);
            UPDATE HealthCheckStats SET rowid = rowid + 1000;
            UPDATE HealthCheckStats
            SET rowid = CASE DateStartInclusive
                WHEN 0 THEN CAST('-9223372036854775808' AS INTEGER)
                WHEN 86400 THEN -1
                WHEN 172800 THEN 0
                WHEN 259200 THEN 1
                END;
            """);
        var progress = new List<TransferV3ValidationProgress>();

        await source.ValidateAsync(source.Options(
            MaxRowsPerBatch: 1,
            Progress: new ImmediateProgress<TransferV3ValidationProgress>(progress.Add)));

        Assert.Equal(
            4,
            progress.Where(value => value.Phase == "table-batch-validated"
                                    && value.Table == "HealthCheckStats")
                .Max(value => value.RowsProcessed));
    }

    [Fact]
    public async Task ValidateAsync_RejectsInconsistentDerivedHealthStatistics()
    {
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                """
                INSERT INTO HealthCheckResults(Id, CreatedAt, DavItemId, Path, Result, RepairStatus)
                VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 0,
                        '00000000-0000-0000-0000-000000000000', '/', 0, 0);
                UPDATE HealthCheckStats SET Count = Count + 1;
                """);
            await AssertFailureAsync(source, "derived-state");
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                """
                INSERT INTO HealthCheckResults(Id, CreatedAt, DavItemId, Path, Result, RepairStatus)
                VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 0,
                        '00000000-0000-0000-0000-000000000000', '/', 0, 0);
                DELETE FROM HealthCheckStats;
                """);
            await AssertFailureAsync(source, "derived-state");
        }
    }

    [Fact]
    public async Task ValidateAsync_ReportsBoundedDerivedFailureForYear9999BucketOverflow()
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = source.DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            var triggers = new List<(string Name, string Sql)>();
            await using (var read = connection.CreateCommand())
            {
                read.CommandText =
                    "SELECT name, sql FROM sqlite_schema "
                    + "WHERE type = 'trigger' AND tbl_name = 'HealthCheckResults' ORDER BY name;";
                await using var reader = await read.ExecuteReaderAsync();
                while (await reader.ReadAsync()) triggers.Add((reader.GetString(0), reader.GetString(1)));
            }
            Assert.NotEmpty(triggers);
            foreach (var trigger in triggers)
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP TRIGGER \"{trigger.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
                await drop.ExecuteNonQueryAsync();
            }
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    "INSERT INTO HealthCheckResults(Id, CreatedAt, DavItemId, Path, Result, RepairStatus) "
                    + "VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 253402300799, "
                    + "'00000000-0000-0000-0000-000000000000', '/max', 0, 0);";
                await insert.ExecuteNonQueryAsync();
            }
            foreach (var trigger in triggers)
            {
                await using var create = connection.CreateCommand();
                create.CommandText = trigger.Sql;
                await create.ExecuteNonQueryAsync();
            }
        }

        var exception = await AssertFailureAsync(source, "derived-state");
        Assert.InRange(exception.Message.Length, 1, 512);
        Assert.Matches("row=1 digest=[0-9a-f]{12}", exception.Message);
    }

    [Fact]
    public async Task ValidateAsync_DerivedMismatchSelectionIsCanonicalAcrossInsertionOrder()
    {
        string firstMessage;
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                "INSERT INTO HealthCheckStats VALUES (86400, 172800, 0, 0, 1); "
                + "INSERT INTO HealthCheckStats VALUES (0, 86400, 0, 0, 1);");
            firstMessage = (await AssertFailureAsync(source, "derived-state")).Message;
        }
        await using (var source = await TransferV3ValidationSource.CreateAsync())
        {
            await source.ExecuteAsync(
                "INSERT INTO HealthCheckStats VALUES (0, 86400, 0, 0, 1); "
                + "INSERT INTO HealthCheckStats VALUES (86400, 172800, 0, 0, 1);");
            var secondMessage = (await AssertFailureAsync(source, "derived-state")).Message;
            Assert.Equal(firstMessage, secondMessage);
        }
    }

    private static async Task AssertHistoryFailureAsync(string sql, string code)
    {
        await using var source = await TransferV3ValidationSource.CreateAsync();
        await source.ExecuteAsync(sql);
        await AssertFailureAsync(source, code);
    }

    private static async Task<Dictionary<string, byte[]>> CaptureFilesAsync(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in paths) result.Add(path, await File.ReadAllBytesAsync(path));
        return result;
    }

    private static async Task<TransferV3SourceValidationException> AssertFailureAsync(
        TransferV3ValidationSource source,
        string code,
        TransferV3SqliteValidationOptions? options = null)
    {
        var exception = await Assert.ThrowsAsync<TransferV3SourceValidationException>(
            () => source.ValidateAsync(options));
        Assert.Contains($"code={code}", exception.Message, StringComparison.Ordinal);
        Assert.InRange(exception.Message.Length, 1, 512);
        return exception;
    }

    private static string WorkerJobSql(string id, int kind, string target) =>
        $"""
        INSERT INTO WorkerJobs(
            Id, Kind, Status, TargetId, Priority, Attempts,
            CreatedAt, UpdatedAt, AvailableAt, LeaseGeneration)
        VALUES ('{id}', {kind}, 0, '{target}', 0, 0, 0, 0, 0, 0);
        """;

    private static string RepositoryPath(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relativePath);
    }

    private static async Task RunProcessAsync(string fileName, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)
                            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(await process.StandardError.ReadToEndAsync());
    }

    private static System.Diagnostics.Process StartFifoWriter(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("/bin/sh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("printf x > \"$1\"");
        start.ArgumentList.Add("fifo-writer");
        start.ArgumentList.Add(path);
        return System.Diagnostics.Process.Start(start)
               ?? throw new InvalidOperationException("Could not start FIFO writer.");
    }
}

internal sealed class ImmediateProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

internal sealed class ProgressProbeException : Exception;

internal sealed class TransferV3ValidationSource : IAsyncDisposable
{
    private readonly SqliteContractDatabase _database;
    private readonly string _root;

    private TransferV3ValidationSource(
        SqliteContractDatabase database,
        string root,
        string databasePath,
        string blobRootPath,
        string validationWorkspaceRoot)
    {
        _database = database;
        _root = root;
        DatabasePath = databasePath;
        BlobRootPath = blobRootPath;
        ValidationWorkspaceRoot = validationWorkspaceRoot;
    }

    internal string DatabasePath { get; }
    internal string BlobRootPath { get; }
    internal string ValidationWorkspaceRoot { get; }

    internal static async Task<TransferV3ValidationSource> CreateAsync()
    {
        var root = Path.Combine(
            Environment.CurrentDirectory,
            $".nzbdav-transfer-v3-validator-test-{Guid.NewGuid():N}");
        var blobRoot = Path.Combine(root, "blobs");
        var workspace = Path.Combine(root, "validation");
        Directory.CreateDirectory(blobRoot);
        Directory.CreateDirectory(workspace);
        var database = new SqliteContractDatabase();
        try
        {
            string migratedPath;
            await using (var context = database.CreateContext())
            {
                await context.Database.MigrateAsync();
                migratedPath = context.Database.GetDbConnection().DataSource;
            }
            var databasePath = Path.Combine(root, "source.sqlite");
            File.Copy(migratedPath, databasePath);
            return new TransferV3ValidationSource(database, root, databasePath, blobRoot, workspace);
        }
        catch
        {
            await database.DisposeAsync();
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    internal TransferV3SqliteValidationOptions Options(
        int MaxRowsPerBatch = 256,
        long MaxBytesPerBatch = 4 * 1024 * 1024,
        IProgress<TransferV3ValidationProgress>? Progress = null) =>
        new(BlobRootPath, MaxRowsPerBatch, MaxBytesPerBatch, Progress);

    internal Task<TransferV3ValidatedSource> ValidateAsync(
        TransferV3SqliteValidationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        new TransferV3SqlitePreflightValidator().ValidateAsync(
            DatabasePath,
            options ?? Options(),
            cancellationToken);

    internal async Task ExecuteAsync(string sql)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    internal Task InsertQueueItemAsync(string id) => ExecuteAsync(
        $"""
        INSERT INTO QueueItems(Id, Category, CreatedAt, FileName, JobName, NzbFileSize,
                               PostProcessing, Priority, TotalSegmentBytes)
        VALUES ('{id}', 'movies', '2026-01-01 00:00:00', 'release.nzb', 'release', 1, 0, 0, 1);
        """);

    internal async Task InsertValidQueueItemAsync(string id)
    {
        await InsertQueueItemAsync(id);
        await ExecuteAsync($"INSERT INTO NzbNames(Id, FileName) VALUES ('{id}', 'release.nzb');");
        await WriteBlobAsync(id, "nzb"u8.ToArray());
    }

    internal Task InsertUsenetDavItemAsync(string id, int subType, string? fileBlobId) => ExecuteAsync(
        $"""
        INSERT INTO DavItems(Id, CreatedAt, ParentId, Name, Type, SubType, Path, IdPrefix, FileBlobId)
        VALUES ('{id}', '2026-01-01 00:00:00', '00000000-0000-0000-0000-000000000002',
                'media.mkv', 2, {subType}, '/content/media.mkv', 'aaaaa',
                {(fileBlobId is null ? "NULL" : $"'{fileBlobId}'")});
        """);

    internal async Task WriteBlobAsync(string id, byte[] contents)
    {
        var normalized = Guid.ParseExact(id, "D").ToString("N");
        var directory = Path.Combine(BlobRootPath, normalized[..2], normalized.Substring(2, 2));
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(
            Path.Combine(directory, Guid.ParseExact(id, "D").ToString("D")),
            contents);
    }

    internal string BlobPath(string id)
    {
        var value = Guid.ParseExact(id, "D");
        var normalized = value.ToString("N");
        return Path.Combine(BlobRootPath, normalized[..2], normalized.Substring(2, 2), value.ToString("D"));
    }

    internal async Task InsertQueueContentsAsync(string id, string contents)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO QueueNzbContents(Id, NzbContents) VALUES ($id, $contents);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$contents", contents);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task InsertConfigAsync(string name, string value)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO ConfigItems(ConfigName, ConfigValue) VALUES ($name, $value);";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}
