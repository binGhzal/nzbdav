using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using backend.Tests.Database;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;
using backend.Tests.Services;

namespace backend.Tests.Tasks;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class RemoveUnlinkedFilesTaskTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public RemoveUnlinkedFilesTaskTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Execute_SkipsMalformedDavItemIdsAndPersistsInvalidationsInTheDeleteBatch()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();

        var validItem = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "Movie.mkv",
            1024,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            DateTimeOffset.UtcNow.AddDays(-2),
            null,
            null,
            null);
        dbContext.Items.Add(validItem);
        await SaveWithoutInvalidationsAsync(dbContext);
        var updatedRows = await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE DavItems SET CreatedAt = {0} WHERE Id = {1}",
            DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            validItem.Id.ToString().ToUpperInvariant());
        Assert.Equal(1, updatedRows);

        var linkedLibrary = await CreateLinkedLibraryAsync(dbContext);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO DavItems
                (Id, IdPrefix, CreatedAt, ParentId, Name, FileSize, Type, SubType, Path,
                 ReleaseDate, LastHealthCheck, NextHealthCheck, HistoryItemId, FileBlobId, NzbBlobId)
            VALUES
                ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8},
                 NULL, NULL, NULL, NULL, NULL, NULL);
            """,
            "not-a-guid",
            "not-a",
            DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DavItem.ContentFolder.Id.ToString(),
            "Broken.mkv",
            2048,
            (int)DavItem.ItemType.UsenetFile,
            (int)DavItem.ItemSubType.NzbFile,
            "/content/Broken.mkv");

        var configManager = _fixture.CreateConfigManager(linkedLibrary.Path);
        RcloneClient.Initialize(configManager);
        Assert.False(RcloneClient.RequiresVfsVisibilityFence);
        var task = new RemoveUnlinkedFilesTask(
            configManager,
            new WebsocketManager(),
            isDryRun: false);

        await task.Execute();

        dbContext.ChangeTracker.Clear();
        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.False(await assertionContext.Items.AnyAsync(x => x.Id == validItem.Id));
        var malformedRows = await assertionContext.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM DavItems WHERE Id = 'not-a-guid'")
            .SingleAsync();
        Assert.Equal(0, malformedRows);
        Assert.Contains("/content/Movie.mkv", RemoveUnlinkedFilesTask.GetAuditReport());
        Assert.Contains("/content/Broken.mkv", RemoveUnlinkedFilesTask.GetAuditReport());
        await AssertWholeCacheVisibilityFenceOnlyAsync(assertionContext);
        await AssertNoCleanupTablesAsync(assertionContext);
    }

    [Fact]
    public async Task Execute_PersistsContentSnapshotAfterDeletingItems()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();

        var removedItem = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            "Removed.mkv",
            1024,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            DateTimeOffset.UtcNow.AddDays(-2),
            null,
            null,
            null);
        dbContext.Items.Add(removedItem);
        dbContext.NzbFiles.Add(new DavNzbFile
        {
            Id = removedItem.Id,
            SegmentIds = ["segment-1"]
        });
        await SaveWithoutInvalidationsAsync(dbContext);
        await ContentIndexSnapshotWriterService.FlushNowAsync(CancellationToken.None);
        var updatedRows = await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE DavItems SET CreatedAt = {0} WHERE Id = {1}",
            DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            removedItem.Id.ToString().ToUpperInvariant());
        Assert.Equal(1, updatedRows);

        var linkedLibrary = await CreateLinkedLibraryAsync(dbContext);
        var task = new RemoveUnlinkedFilesTask(
            _fixture.CreateConfigManager(linkedLibrary.Path),
            new WebsocketManager(),
            isDryRun: false);

        await task.Execute();

        await _fixture.RecreateDatabaseAsync();

        var recoveryService = new ContentIndexRecoveryService();
        await recoveryService.RecoverAsync(CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        Assert.DoesNotContain(
            "/content/Removed.mkv",
            await assertionContext.Items.AsNoTracking().Select(x => x.Path).ToListAsync());
    }

    [Fact]
    public async Task Execute_ClearsStaleAuditReportWhenCleanupAbortsBeforeScanningUnlinkedItems()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await CreateLegacyCleanupTablesAsync(dbContext);
        SetAuditReport(["/content/stale-file.mkv"]);
        var task = new RemoveUnlinkedFilesTask(new ConfigManager(), new WebsocketManager(), isDryRun: true);

        await task.Execute();

        Assert.Equal(
            "This list is Empty.\nYou must first run the task.",
            RemoveUnlinkedFilesTask.GetAuditReport());
        await AssertNoCleanupTablesAsync(dbContext);
    }

    [Fact]
    public async Task Execute_UsesOneConnectionScopedTemporaryTableAndRemovesLegacyTables()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        await CreateLegacyCleanupTablesAsync(dbContext);
        var linkedLibrary = await CreateLinkedLibraryAsync(dbContext);
        var inspectedSecondConnection = false;
        var task = new RemoveUnlinkedFilesTask(
            _fixture.CreateConfigManager(linkedLibrary.Path),
            new WebsocketManager(),
            isDryRun: true,
            progressReporter: async progress =>
            {
                if (!progress.Message.Contains("Searching for unlinked", StringComparison.Ordinal)) return;

                await using var secondConnection = await _fixture.CreateMigratedContextAsync();
                await AssertNoCleanupTablesAsync(secondConnection);
                await Assert.ThrowsAsync<SqliteException>(async () =>
                    await secondConnection.Database
                        .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"TMP_LINKED_FILES\"")
                        .SingleAsync());
                inspectedSecondConnection = true;
            });

        await task.Execute();

        Assert.True(inspectedSecondConnection);
        await AssertNoCleanupTablesAsync(dbContext);
    }

    [Fact]
    public async Task Execute_CancellationAfterScanDropsTemporaryTableAndAllowsNextRun()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var linkedLibrary = await CreateLinkedLibraryAsync(dbContext);
        using var cancellation = new CancellationTokenSource();
        var task = new RemoveUnlinkedFilesTask(
            _fixture.CreateConfigManager(linkedLibrary.Path),
            new WebsocketManager(),
            isDryRun: true,
            progressReporter: progress =>
            {
                if (progress.Message.Contains("Searching for unlinked", StringComparison.Ordinal))
                    cancellation.Cancel();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.Execute(cancellation.Token));
        await AssertNoCleanupTablesAsync(dbContext);

        var nextRun = new RemoveUnlinkedFilesTask(
            _fixture.CreateConfigManager(linkedLibrary.Path),
            new WebsocketManager(),
            isDryRun: true);
        await nextRun.Execute();
        await AssertNoCleanupTablesAsync(dbContext);
    }

    [Fact]
    public async Task Execute_InjectedFailureAfterScanDropsTemporaryTable()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var linkedLibrary = await CreateLinkedLibraryAsync(dbContext);
        var task = new RemoveUnlinkedFilesTask(
            _fixture.CreateConfigManager(linkedLibrary.Path),
            new WebsocketManager(),
            isDryRun: true,
            progressReporter: progress => progress.Message.Contains(
                "Searching for unlinked",
                StringComparison.Ordinal)
                    ? Task.FromException(new InjectedCleanupException())
                    : Task.CompletedTask);

        await Assert.ThrowsAsync<InjectedCleanupException>(() => task.Execute());

        await AssertNoCleanupTablesAsync(dbContext);
    }

    [Fact]
    public async Task Execute_AbortsWhenSixLibraryEntriesReferenceOnlyOneDistinctDavItem()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var unlinkedItem = CreateUsenetItem(Guid.NewGuid(), "MustRemain.mkv");
        dbContext.Items.Add(unlinkedItem);
        var linkedLibrary = await CreateLinkedLibraryAsync(
            dbContext,
            distinctLinkedItems: 1,
            referencesPerItem: 6);
        var progress = new List<MaintenanceTaskProgress>();
        var task = new RemoveUnlinkedFilesTask(
            _fixture.CreateConfigManager(linkedLibrary.Path),
            new WebsocketManager(),
            isDryRun: false,
            progressReporter: item =>
            {
                progress.Add(item);
                return Task.CompletedTask;
            });

        await task.Execute();

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Items.AsNoTracking().AnyAsync(x => x.Id == unlinkedItem.Id));
        Assert.Empty(await dbContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
        Assert.Contains(progress, item =>
            item.Message.Contains("less than five unique linked files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_FailureBeforeFileBatchCommitRollsBackDeleteAndInvalidations()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var unlinkedItem = CreateUsenetItem(Guid.NewGuid(), "RollbackFile.mkv");
        dbContext.Items.Add(unlinkedItem);
        var linkedLibrary = await CreateLinkedLibraryAsync(dbContext);
        var configManager = _fixture.CreateConfigManager(linkedLibrary.Path);
        RcloneClient.Initialize(configManager);
        var task = new RemoveUnlinkedFilesTask(
            configManager,
            new WebsocketManager(),
            isDryRun: false,
            progressReporter: item => item.Message.Contains(
                "Committing unlinked item batch",
                StringComparison.Ordinal)
                    ? Task.FromException(new InjectedPreCommitException())
                    : Task.CompletedTask);

        await Assert.ThrowsAsync<InjectedPreCommitException>(() => task.Execute());

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Items.AsNoTracking().AnyAsync(x => x.Id == unlinkedItem.Id));
        Assert.Empty(await dbContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(unlinkedItem.Path, RemoveUnlinkedFilesTask.GetAuditReport());
    }

    [Fact]
    public async Task Execute_InvalidationPersistenceFailureRollsBackDeleteInsideTransaction()
    {
        const string failureMarker = "injected-invalidation-persistence-failure";
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var unlinkedItem = CreateUsenetItem(Guid.NewGuid(), "TransactionalRollbackFile.mkv");
        dbContext.Items.Add(unlinkedItem);
        var linkedLibrary = await CreateLinkedLibraryAsync(dbContext);
        var configManager = _fixture.CreateConfigManager(linkedLibrary.Path);
        RcloneClient.Initialize(configManager);
        Assert.False(RcloneClient.RequiresVfsVisibilityFence);
        await dbContext.Database.ExecuteSqlRawAsync(
            $$"""
            DROP TRIGGER IF EXISTS fail_cleanup_invalidation_insert;
            CREATE TRIGGER fail_cleanup_invalidation_insert
            BEFORE INSERT ON RcloneInvalidationItems
            BEGIN
                SELECT RAISE(ABORT, '{{failureMarker}}');
            END;
            """);
        var task = new RemoveUnlinkedFilesTask(
            configManager,
            new WebsocketManager(),
            isDryRun: false);

        try
        {
            var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => task.Execute());

            Assert.Contains(failureMarker, thrown.ToString(), StringComparison.Ordinal);
            dbContext.ChangeTracker.Clear();
            Assert.True(await dbContext.Items.AsNoTracking().AnyAsync(x => x.Id == unlinkedItem.Id));
            Assert.Empty(await dbContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
            Assert.DoesNotContain(unlinkedItem.Path, RemoveUnlinkedFilesTask.GetAuditReport());
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS fail_cleanup_invalidation_insert;");
        }
    }

    [Fact]
    public async Task Execute_DurableProgressReporterWritesOutsideFileBatchTransaction()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var unlinkedItem = CreateUsenetItem(Guid.NewGuid(), "ProgressWriter.mkv");
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var maintenanceRun = new MaintenanceRun
        {
            Id = Guid.NewGuid(),
            Kind = MaintenanceRunKind.RemoveUnlinkedFiles,
            Status = MaintenanceRunStatus.Running,
            ActiveSlot = 1,
            RequestedBy = "manual",
            CreatedAt = now,
            StartedAt = now,
            UpdatedAt = now,
            ProgressCurrent = 0,
            Message = "Running."
        };
        dbContext.Items.Add(unlinkedItem);
        dbContext.MaintenanceRuns.Add(maintenanceRun);
        var linkedLibrary = await CreateLinkedLibraryAsync(dbContext);
        var progressWriteCompleted = false;
        var configManager = _fixture.CreateConfigManager(linkedLibrary.Path);
        RcloneClient.Initialize(configManager);
        Assert.False(RcloneClient.RequiresVfsVisibilityFence);
        var task = new RemoveUnlinkedFilesTask(
            configManager,
            new WebsocketManager(),
            isDryRun: false,
            progressReporter: async progress =>
            {
                if (!progress.Message.Contains("Committing unlinked item batch", StringComparison.Ordinal)) return;

                await using var progressContext = new DavDatabaseContext();
                progressContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(1));
                var updated = await MaintenanceRunTransitions.PersistProgressAsync(
                    progressContext,
                    maintenanceRun.Id,
                    progress,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                Assert.Equal(1, updated);
                progressWriteCompleted = true;
            });

        await task.Execute();

        Assert.True(progressWriteCompleted);
        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Items.AsNoTracking().AnyAsync(x => x.Id == unlinkedItem.Id));
        await AssertWholeCacheVisibilityFenceOnlyAsync(dbContext);
    }

    [Fact]
    public async Task Execute_RcloneEnabledEnqueuesPathInvalidationForDeletedFile()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var unlinkedItem = CreateUsenetItem(Guid.NewGuid(), "PathInvalidationFile.mkv");
        dbContext.Items.Add(unlinkedItem);
        var linkedLibrary = await CreateLinkedLibraryAsync(dbContext);
        var configManager = _fixture.CreateConfigManager(linkedLibrary.Path);
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "rclone.host", ConfigValue = "http://127.0.0.1:5572" }
        ]);
        RcloneClient.Initialize(configManager);
        Assert.True(RcloneClient.RequiresVfsVisibilityFence);
        var task = new RemoveUnlinkedFilesTask(
            configManager,
            new WebsocketManager(),
            isDryRun: false);

        try
        {
            await task.Execute();
        }
        finally
        {
            configManager.UpdateValues([
                new ConfigItem { ConfigName = "rclone.rc-enabled", ConfigValue = "false" }
            ]);
            RcloneClient.Initialize(configManager);
        }

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Items.AsNoTracking().AnyAsync(x => x.Id == unlinkedItem.Id));
        var invalidationPaths = await dbContext.RcloneInvalidationItems
            .AsNoTracking()
            .Select(x => x.Path)
            .ToListAsync();
        Assert.Contains("/content", invalidationPaths);
        Assert.DoesNotContain(RcloneInvalidationItem.WholeCacheVisibilityFencePath, invalidationPaths);
    }

    [Fact]
    public async Task Execute_EmptyDirectoryDeleteAndInvalidationsCommitTogether()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var emptyDirectory = CreateDirectoryItem(
            Guid.NewGuid(),
            DavItem.ContentFolder.Id,
            "empty-success",
            "/content/empty-success",
            DavItem.ItemSubType.Directory);
        dbContext.Items.Add(emptyDirectory);
        var linkedLibrary = await CreateLinkedLibraryAsync(dbContext);
        var configManager = _fixture.CreateConfigManager(linkedLibrary.Path);
        RcloneClient.Initialize(configManager);
        Assert.False(RcloneClient.RequiresVfsVisibilityFence);
        var task = new RemoveUnlinkedFilesTask(configManager, new WebsocketManager(), isDryRun: false);

        await task.Execute();

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Items.AsNoTracking().AnyAsync(x => x.Id == emptyDirectory.Id));
        await AssertWholeCacheVisibilityFenceOnlyAsync(dbContext);
    }

    [Fact]
    public async Task Execute_FailureBeforeEmptyDirectoryBatchCommitRollsBackDeleteAndInvalidations()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var emptyDirectory = CreateDirectoryItem(
            Guid.NewGuid(),
            DavItem.ContentFolder.Id,
            "empty-rollback",
            "/content/empty-rollback",
            DavItem.ItemSubType.Directory);
        dbContext.Items.Add(emptyDirectory);
        var linkedLibrary = await CreateLinkedLibraryAsync(dbContext);
        var configManager = _fixture.CreateConfigManager(linkedLibrary.Path);
        RcloneClient.Initialize(configManager);
        var task = new RemoveUnlinkedFilesTask(
            configManager,
            new WebsocketManager(),
            isDryRun: false,
            progressReporter: item => item.Message.Contains(
                "Committing empty-directory batch",
                StringComparison.Ordinal)
                    ? Task.FromException(new InjectedPreCommitException())
                    : Task.CompletedTask);

        await Assert.ThrowsAsync<InjectedPreCommitException>(() => task.Execute());

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Items.AsNoTracking().AnyAsync(x => x.Id == emptyDirectory.Id));
        Assert.Empty(await dbContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ConnectionCleanup_PreservesPrimaryFailureAndStillAttemptsDropAndClose()
    {
        var primaryFailure = new InjectedCleanupException();
        var dropFailure = new InjectedDropException();
        var closeFailure = new InjectedCloseException();
        var closeAttempted = false;

        var thrown = await Assert.ThrowsAsync<InjectedCleanupException>(() =>
            RemoveUnlinkedFilesTask.RunWithConnectionCleanupAsync(
                () => Task.FromException(primaryFailure),
                () => Task.FromException(dropFailure),
                () =>
                {
                    closeAttempted = true;
                    return Task.FromException(closeFailure);
                }));

        Assert.Same(primaryFailure, thrown);
        Assert.True(closeAttempted);
    }

    [Fact]
    public async Task ConnectionCleanup_SurfacesCleanupFailureWhenBodySucceeds()
    {
        var dropFailure = new InjectedDropException();
        var closeAttempted = false;

        var thrown = await Assert.ThrowsAsync<InjectedDropException>(() =>
            RemoveUnlinkedFilesTask.RunWithConnectionCleanupAsync(
                () => Task.CompletedTask,
                () => Task.FromException(dropFailure),
                () =>
                {
                    closeAttempted = true;
                    return Task.CompletedTask;
                }));

        Assert.Same(dropFailure, thrown);
        Assert.True(closeAttempted);
    }

    [Fact]
    public void CountAndRemovalProgress_Use64BitValuesWithoutWrappingTheUiBoundary()
    {
        var countMethod = typeof(RemoveUnlinkedFilesTask).GetMethod(
            "CountUnlinkedItems",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var removeMethod = typeof(RemoveUnlinkedFilesTask).GetMethod(
            "RemoveUnlinkedItems",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(countMethod);
        Assert.NotNull(removeMethod);
        Assert.Equal(typeof(Task<long>), countMethod.ReturnType);
        Assert.Equal(
            typeof(long),
            removeMethod.GetParameters().Single(parameter => parameter.Name == "totalCount").ParameterType);

        Assert.Equal((int.MaxValue, int.MaxValue), RemoveUnlinkedFilesTask.ToProgressValues(int.MaxValue, int.MaxValue));
        Assert.Equal((null, null), RemoveUnlinkedFilesTask.ToProgressValues(int.MaxValue + 1L, int.MaxValue + 1L));
    }

    [Fact]
    public void PostgreSqlCreatedBeforeStrictUpperBoundCeilsWithoutChangingAlignedValues()
    {
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql("Host=127.0.0.1;Database=not-opened;Username=test;Password=test")
            .Options;
        using var dbContext = new PostgreSqlDavDatabaseContext(options);
        var method = typeof(RemoveUnlinkedFilesTask).GetMethod(
            "CreateCreatedBeforeParameter",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var aligned = new DateTime(2026, 7, 12, 10, 0, 0, DateTimeKind.Local).AddTicks(10);
        var subMicrosecond = aligned.AddTicks(5);

        var alignedParameter = Assert.IsType<NpgsqlParameter>(
            method.Invoke(null, [dbContext, aligned]));
        var subMicrosecondParameter = Assert.IsType<NpgsqlParameter>(
            method.Invoke(null, [dbContext, subMicrosecond]));

        var alignedValue = Assert.IsType<DateTime>(alignedParameter.Value);
        var ceilingValue = Assert.IsType<DateTime>(subMicrosecondParameter.Value);
        Assert.Equal(aligned.Ticks, alignedValue.Ticks);
        Assert.Equal(aligned.AddTicks(10).Ticks, ceilingValue.Ticks);
        Assert.Equal(DateTimeKind.Unspecified, alignedValue.Kind);
        Assert.Equal(DateTimeKind.Unspecified, ceilingValue.Kind);
    }

    [PostgreSqlFact]
    public async Task PostgreSqlNativeSchema_CountDryRunAndDeleteUseProviderTypedSql()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.TestConnectionStringVariable);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var schemaName = $"remove_unlinked_{Guid.NewGuid():N}";
        await using var adminConnection = new NpgsqlConnection(connectionString);
        await adminConnection.OpenAsync();
        await ExecuteNonQueryAsync(adminConnection, $"CREATE SCHEMA \"{schemaName}\"");

        try
        {
            var schemaConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schemaName,
                Pooling = false,
                Timezone = TimeZoneInfo.Local.Id,
                GssEncryptionMode = GssEncryptionMode.Disable
            }.ConnectionString;
            using var environment = new DatabaseEnvironmentScope(schemaConnectionString);
            await using (var schemaContext = new PostgreSqlDavDatabaseContext())
            {
                await schemaContext.Database.ExecuteSqlRawAsync(
                    schemaContext.Database.GenerateCreateScript());
                await schemaContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE tmp_linked_files (id uuid NOT NULL PRIMARY KEY);
                    CREATE TABLE tmp_linked_files_unique (id uuid NOT NULL PRIMARY KEY);
                    CREATE TABLE "TMP_LINKED_FILES" ("Id" uuid NOT NULL PRIMARY KEY);
                    CREATE TABLE "TMP_LINKED_FILES_UNIQUE" ("Id" uuid NOT NULL PRIMARY KEY);
                    """);
            }

            Assert.Equal(
                "uuid",
                await ReadColumnTypeAsync(adminConnection, schemaName, "DavItems", "Id"));

            var unlinkedId = Guid.NewGuid();
            LinkedLibrary linkedLibrary;
            await using (var seedContext = new PostgreSqlDavDatabaseContext())
            {
                seedContext.Items.AddRange(
                    CreateDirectoryItem(DavItem.Root.Id, null, "/", "/", DavItem.ItemSubType.WebdavRoot),
                    CreateDirectoryItem(
                        DavItem.ContentFolder.Id,
                        DavItem.Root.Id,
                        "content",
                        "/content",
                        DavItem.ItemSubType.ContentRoot),
                    CreateUsenetItem(unlinkedId, "Unlinked.mkv"));
                linkedLibrary = await CreateLinkedLibraryAsync(seedContext);
            }

            var progress = new List<MaintenanceTaskProgress>();
            var inspectedSecondConnection = false;
            var dryRunTask = new RemoveUnlinkedFilesTask(
                _fixture.CreateConfigManager(linkedLibrary.Path),
                new WebsocketManager(),
                isDryRun: true,
                progressReporter: async item =>
                {
                    progress.Add(item);
                    if (!item.Message.Contains("Searching for unlinked", StringComparison.Ordinal)) return;

                    await using var secondConnection = new PostgreSqlDavDatabaseContext();
                    var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
                        await secondConnection.Database
                            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"TMP_LINKED_FILES\"")
                            .SingleAsync());
                    Assert.Equal(PostgresErrorCodes.UndefinedTable, exception.SqlState);
                    inspectedSecondConnection = true;
                });

            await dryRunTask.Execute();

            Assert.True(inspectedSecondConnection);
            Assert.Equal(0, await ReadPersistentCleanupTableCountAsync(adminConnection, schemaName));
            Assert.Contains(progress, item =>
                item.Message.Contains("Found 1 webdav items to remove.", StringComparison.Ordinal));
            Assert.Equal("/content/Unlinked.mkv", RemoveUnlinkedFilesTask.GetAuditReport());
            await using (var dryRunAssertionContext = new PostgreSqlDavDatabaseContext())
            {
                Assert.Equal(
                    linkedLibrary.ItemIds.Count,
                    await dryRunAssertionContext.Items.CountAsync(x => linkedLibrary.ItemIds.Contains(x.Id)));
                Assert.True(await dryRunAssertionContext.Items.AnyAsync(x => x.Id == unlinkedId));
            }

            var deleteTask = new RemoveUnlinkedFilesTask(
                _fixture.CreateConfigManager(linkedLibrary.Path),
                new WebsocketManager(),
                isDryRun: false);

            await deleteTask.Execute();

            await using var deleteAssertionContext = new PostgreSqlDavDatabaseContext();
            Assert.Equal(
                linkedLibrary.ItemIds.Count,
                await deleteAssertionContext.Items.CountAsync(x => linkedLibrary.ItemIds.Contains(x.Id)));
            Assert.False(await deleteAssertionContext.Items.AnyAsync(x => x.Id == unlinkedId));
            Assert.Equal("/content/Unlinked.mkv", RemoveUnlinkedFilesTask.GetAuditReport());
            Assert.Equal(0, await ReadPersistentCleanupTableCountAsync(adminConnection, schemaName));
        }
        finally
        {
            await ExecuteNonQueryAsync(
                adminConnection,
                $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE");
        }
    }

    private static DavItem CreateDirectoryItem(
        Guid id,
        Guid? parentId,
        string name,
        string path,
        DavItem.ItemSubType subType)
    {
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = WholePostgreSqlMicroseconds(DateTime.Now.AddDays(-3)),
            ParentId = parentId,
            Name = name,
            Type = DavItem.ItemType.Directory,
            SubType = subType,
            Path = path
        };
    }

    private static DavItem CreateUsenetItem(Guid id, string name)
    {
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = WholePostgreSqlMicroseconds(DateTime.Now.AddDays(-2)),
            ParentId = DavItem.ContentFolder.Id,
            Name = name,
            FileSize = 1024,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = $"/content/{name}"
        };
    }

    private static DateTime WholePostgreSqlMicroseconds(DateTime value)
    {
        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return new DateTime(
            unspecified.Ticks - unspecified.Ticks % 10,
            DateTimeKind.Unspecified);
    }

    private async Task<LinkedLibrary> CreateLinkedLibraryAsync(
        DavDatabaseContext dbContext,
        int distinctLinkedItems = 5,
        int referencesPerItem = 1)
    {
        var linkedIds = Enumerable.Range(0, distinctLinkedItems)
            .Select(_ => Guid.NewGuid())
            .ToList();
        dbContext.Items.AddRange(linkedIds.Select((id, index) =>
            CreateUsenetItem(id, $"Linked-{index}-{id:N}.mkv")));
        await SaveWithoutInvalidationsAsync(dbContext);

        var libraryPath = _fixture.CreateLibraryDirectory();
        for (var itemIndex = 0; itemIndex < linkedIds.Count; itemIndex++)
        {
            for (var referenceIndex = 0; referenceIndex < referencesPerItem; referenceIndex++)
            {
                File.CreateSymbolicLink(
                    Path.Join(libraryPath, $"linked-{itemIndex}-{referenceIndex}.mkv"),
                    DatabaseStoreSymlinkFile.GetTargetPath(linkedIds[itemIndex], "/mnt/nzbdav"));
            }
        }

        return new LinkedLibrary(libraryPath, linkedIds);
    }

    private static async Task SaveWithoutInvalidationsAsync(DavDatabaseContext dbContext)
    {
        var previousSuppression = dbContext.SuppressRcloneInvalidations;
        dbContext.SuppressRcloneInvalidations = true;
        try
        {
            await dbContext.SaveChangesAsync();
        }
        finally
        {
            dbContext.SuppressRcloneInvalidations = previousSuppression;
        }
    }

    private static async Task AssertWholeCacheVisibilityFenceOnlyAsync(DavDatabaseContext dbContext)
    {
        var item = Assert.Single(await dbContext.RcloneInvalidationItems.AsNoTracking().ToListAsync());
        Assert.Equal(RcloneInvalidationItem.WholeCacheVisibilityFenceId, item.Id);
        Assert.Equal(RcloneInvalidationItem.WholeCacheVisibilityFencePath, item.Path);
        Assert.True(item.Revision >= 1);
    }

    private static Task CreateLegacyCleanupTablesAsync(DavDatabaseContext dbContext)
    {
        return dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP TABLE IF EXISTS "TMP_LINKED_FILES";
            DROP TABLE IF EXISTS "TMP_LINKED_FILES_UNIQUE";
            CREATE TABLE "TMP_LINKED_FILES" ("Id" TEXT NOT NULL PRIMARY KEY);
            CREATE TABLE "TMP_LINKED_FILES_UNIQUE" ("Id" TEXT NOT NULL PRIMARY KEY);
            """);
    }

    private static async Task AssertNoCleanupTablesAsync(DavDatabaseContext dbContext)
    {
        Assert.Equal(
            0,
            await dbContext.Database.SqlQueryRaw<long>(
                    """
                    SELECT COUNT(*) AS "Value"
                    FROM sqlite_master
                    WHERE type = 'table'
                      AND name IN ('TMP_LINKED_FILES', 'TMP_LINKED_FILES_UNIQUE')
                    """)
                .SingleAsync());
        Assert.Equal(
            0,
            await dbContext.Database.SqlQueryRaw<long>(
                    """
                    SELECT COUNT(*) AS "Value"
                    FROM sqlite_temp_master
                    WHERE type = 'table'
                      AND name IN ('TMP_LINKED_FILES', 'TMP_LINKED_FILES_UNIQUE')
                    """)
                .SingleAsync());
    }

    private static async Task ExecuteNonQueryAsync(NpgsqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadColumnTypeAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = @schemaName
              AND table_name = @tableName
              AND column_name = @columnName
            """;
        command.Parameters.AddWithValue("schemaName", schemaName);
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("columnName", columnName);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<long> ReadPersistentCleanupTableCountAsync(
        NpgsqlConnection connection,
        string schemaName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = @schemaName
              AND lower(table_name) IN ('tmp_linked_files', 'tmp_linked_files_unique')
            """;
        command.Parameters.AddWithValue("schemaName", schemaName);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class DatabaseEnvironmentScope : IDisposable
    {
        private readonly string? _provider = Environment.GetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER");
        private readonly string? _connectionString = Environment.GetEnvironmentVariable(
            "NZBDAV_DATABASE_CONNECTION_STRING");
        private readonly string? _legacyTimestampTimezone = Environment.GetEnvironmentVariable(
            PostgreSqlConnectionPolicy.LegacyTimezoneVariable);

        public DatabaseEnvironmentScope(string connectionString)
        {
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", "postgres");
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING", connectionString);
            Environment.SetEnvironmentVariable(
                PostgreSqlConnectionPolicy.LegacyTimezoneVariable,
                TimeZoneInfo.Local.Id);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_PROVIDER", _provider);
            Environment.SetEnvironmentVariable("NZBDAV_DATABASE_CONNECTION_STRING", _connectionString);
            Environment.SetEnvironmentVariable(
                PostgreSqlConnectionPolicy.LegacyTimezoneVariable,
                _legacyTimestampTimezone);
        }
    }

    private static void SetAuditReport(List<string> paths)
    {
        var field = typeof(RemoveUnlinkedFilesTask).GetField(
            "_allRemovedPaths",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(null, paths);
    }

    private sealed record LinkedLibrary(string Path, IReadOnlyList<Guid> ItemIds);
    private sealed class InjectedCleanupException : Exception;
    private sealed class InjectedPreCommitException : Exception;
    private sealed class InjectedDropException : Exception;
    private sealed class InjectedCloseException : Exception;
}
