using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;

namespace backend.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class UsenetFileToBlobstoreMigrationServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public UsenetFileToBlobstoreMigrationServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void TargetBlobIdentityIsStableAndScopedByMetadataType()
    {
        var sourceId = Guid.Parse("018f4f40-a9d8-7c3c-bf4f-aeb593f85191");

        var nzb = UsenetFileToBlobstoreMigrationService.CreateTargetBlobId(
            sourceId,
            UsenetFileToBlobstoreMigrationService.LegacyFileKind.Nzb);
        var repeatedNzb = UsenetFileToBlobstoreMigrationService.CreateTargetBlobId(
            sourceId,
            UsenetFileToBlobstoreMigrationService.LegacyFileKind.Nzb);
        var rar = UsenetFileToBlobstoreMigrationService.CreateTargetBlobId(
            sourceId,
            UsenetFileToBlobstoreMigrationService.LegacyFileKind.Rar);
        var multipart = UsenetFileToBlobstoreMigrationService.CreateTargetBlobId(
            sourceId,
            UsenetFileToBlobstoreMigrationService.LegacyFileKind.Multipart);

        Assert.Equal(nzb, repeatedNzb);
        Assert.NotEqual(sourceId, nzb);
        Assert.Equal(3, new[] { nzb, rar, multipart }.Distinct().Count());
    }

    [Theory]
    [InlineData(DavItem.ItemSubType.NzbFile)]
    [InlineData(DavItem.ItemSubType.RarFile)]
    [InlineData(DavItem.ItemSubType.MultipartFile)]
    public async Task PostCommitAcknowledgementFailurePreservesBlobAndCountsMigration(
        DavItem.ItemSubType subType)
    {
        var sourceId = await CreateLegacyFileAsync(subType);
        var targetBlobId = TargetBlobId(sourceId, subType);
        var interceptor = new ThrowAfterCommittedSaveInterceptor();
        using var service = CreateService(() => CreateContext(interceptor));

        var result = await MigrateOnceAsync(service, subType);

        Assert.Equal(UsenetFileToBlobstoreMigrationService.MigrationStepResult.Migrated, result);
        await AssertMigratedAsync(sourceId, targetBlobId, subType);
    }

    [Theory]
    [InlineData(DavItem.ItemSubType.NzbFile)]
    [InlineData(DavItem.ItemSubType.RarFile)]
    [InlineData(DavItem.ItemSubType.MultipartFile)]
    public async Task PreCommitFailureDeletesUnreferencedBlobAndRetryUsesSameIdentity(
        DavItem.ItemSubType subType)
    {
        var sourceId = await CreateLegacyFileAsync(subType);
        var targetBlobId = TargetBlobId(sourceId, subType);
        using var failingService = CreateService(() => CreateContext(new ThrowBeforeCommitInterceptor()));

        await Assert.ThrowsAsync<InjectedPreCommitException>(() =>
            MigrateOnceAsync(failingService, subType));

        await AssertLegacyStateAsync(sourceId, subType);
        Assert.Equal(BlobStore.BlobReadStatus.Missing, BlobStore.TryStatBlob(targetBlobId).Status);

        using var retryService = CreateService(() => new DavDatabaseContext());
        var retryResult = await MigrateOnceAsync(retryService, subType);

        Assert.Equal(UsenetFileToBlobstoreMigrationService.MigrationStepResult.Migrated, retryResult);
        await AssertMigratedAsync(sourceId, targetBlobId, subType);
    }

    [Fact]
    public async Task IndeterminateReconciliationPreservesBlobForSameIdentityRetry()
    {
        var sourceId = await CreateLegacyFileAsync(DavItem.ItemSubType.NzbFile);
        var targetBlobId = TargetBlobId(sourceId, DavItem.ItemSubType.NzbFile);
        var createCount = 0;
        using var failingService = CreateService(() =>
        {
            if (Interlocked.Increment(ref createCount) == 1)
                return CreateContext(new ThrowBeforeCommitInterceptor());
            throw new InjectedReconciliationException();
        });

        await Assert.ThrowsAsync<InjectedPreCommitException>(() =>
            failingService.MigrateNzbFileOnceAsync(CancellationToken.None));

        await AssertLegacyStateAsync(sourceId, DavItem.ItemSubType.NzbFile);
        var preserved = await BlobStore.ReadBlob<DavNzbFile>(targetBlobId);
        Assert.NotNull(preserved);
        Assert.Equal(targetBlobId, preserved.Id);
        Assert.Equal(["nzb-segment"], preserved.SegmentIds);

        using var retryService = CreateService(() => new DavDatabaseContext());
        var retryResult = await retryService.MigrateNzbFileOnceAsync(CancellationToken.None);

        Assert.Equal(UsenetFileToBlobstoreMigrationService.MigrationStepResult.Migrated, retryResult);
        await AssertMigratedAsync(sourceId, targetBlobId, DavItem.ItemSubType.NzbFile);
    }

    [Fact]
    public async Task MigrationDoesNotOverwriteBlobOwnedByAnotherTable()
    {
        var sourceId = await CreateLegacyFileAsync(DavItem.ItemSubType.NzbFile);
        var targetBlobId = TargetBlobId(sourceId, DavItem.ItemSubType.NzbFile);
        await BlobStore.WriteBlob(targetBlobId, new DavNzbFile
        {
            Id = targetBlobId,
            SegmentIds = ["other-owner"]
        });
        try
        {
            await using (var setup = await _fixture.CreateMigratedContextAsync())
            {
                setup.HistoryItems.Add(new HistoryItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow,
                    FileName = "Other.nzb",
                    JobName = "Other",
                    Category = "movies",
                    DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                    TotalSegmentBytes = 1,
                    DownloadTimeSeconds = 1,
                    NzbBlobId = targetBlobId
                });
                await setup.SaveChangesAsync();
            }

            using var service = CreateService(() => new DavDatabaseContext());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.MigrateNzbFileOnceAsync(CancellationToken.None));

            Assert.Contains("already owned or reserved", exception.Message);
            await AssertLegacyStateAsync(sourceId, DavItem.ItemSubType.NzbFile);
            var preserved = await BlobStore.ReadBlob<DavNzbFile>(targetBlobId);
            Assert.NotNull(preserved);
            Assert.Equal(["other-owner"], preserved.SegmentIds);
        }
        finally
        {
            BlobStore.Delete(targetBlobId);
        }
    }

    private async Task<Guid> CreateLegacyFileAsync(DavItem.ItemSubType subType)
    {
        var sourceId = Guid.NewGuid();
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        dbContext.Items.Add(new DavItem
        {
            Id = sourceId,
            IdPrefix = sourceId.ToString("N")[..DavItem.IdPrefixLength],
            CreatedAt = DateTime.UtcNow,
            ParentId = DavItem.ContentFolder.Id,
            Name = $"Legacy-{sourceId:N}.mkv",
            FileSize = 1024,
            Type = DavItem.ItemType.UsenetFile,
            SubType = subType,
            Path = $"/content/Legacy-{sourceId:N}.mkv"
        });

        switch (subType)
        {
            case DavItem.ItemSubType.NzbFile:
                dbContext.NzbFiles.Add(new DavNzbFile
                {
                    Id = sourceId,
                    SegmentIds = ["nzb-segment"]
                });
                break;
            case DavItem.ItemSubType.RarFile:
                dbContext.RarFiles.Add(new DavRarFile
                {
                    Id = sourceId,
                    RarParts =
                    [
                        new DavRarFile.RarPart
                        {
                            SegmentIds = ["rar-segment"],
                            PartSize = 1,
                            Offset = 0,
                            ByteCount = 1
                        }
                    ]
                });
                break;
            case DavItem.ItemSubType.MultipartFile:
                dbContext.MultipartFiles.Add(new DavMultipartFile
                {
                    Id = sourceId,
                    Metadata = new DavMultipartFile.Meta()
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(subType), subType, null);
        }

        await dbContext.SaveChangesAsync();
        return sourceId;
    }

    private async Task AssertMigratedAsync(
        Guid sourceId,
        Guid targetBlobId,
        DavItem.ItemSubType subType)
    {
        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        var davItem = await dbContext.Items.AsNoTracking().SingleAsync(x => x.Id == sourceId);
        Assert.Equal(targetBlobId, davItem.FileBlobId);
        Assert.False(await LegacyRowExistsAsync(dbContext, sourceId, subType));

        switch (subType)
        {
            case DavItem.ItemSubType.NzbFile:
                var nzb = await BlobStore.ReadBlob<DavNzbFile>(targetBlobId);
                Assert.NotNull(nzb);
                Assert.Equal(targetBlobId, nzb.Id);
                Assert.Equal(["nzb-segment"], nzb.SegmentIds);
                break;
            case DavItem.ItemSubType.RarFile:
                var rar = await BlobStore.ReadBlob<DavRarFile>(targetBlobId);
                Assert.NotNull(rar);
                Assert.Equal(targetBlobId, rar.Id);
                Assert.Equal(["rar-segment"], rar.RarParts.Single().SegmentIds);
                break;
            case DavItem.ItemSubType.MultipartFile:
                var multipart = await BlobStore.ReadBlob<DavMultipartFile>(targetBlobId);
                Assert.NotNull(multipart);
                Assert.Equal(targetBlobId, multipart.Id);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(subType), subType, null);
        }
    }

    private async Task AssertLegacyStateAsync(Guid sourceId, DavItem.ItemSubType subType)
    {
        await using var dbContext = await _fixture.CreateMigratedContextAsync();
        var davItem = await dbContext.Items.AsNoTracking().SingleAsync(x => x.Id == sourceId);
        Assert.Null(davItem.FileBlobId);
        Assert.True(await LegacyRowExistsAsync(dbContext, sourceId, subType));
    }

    private static Task<bool> LegacyRowExistsAsync(
        DavDatabaseContext dbContext,
        Guid sourceId,
        DavItem.ItemSubType subType)
    {
        return subType switch
        {
            DavItem.ItemSubType.NzbFile => dbContext.NzbFiles.AnyAsync(x => x.Id == sourceId),
            DavItem.ItemSubType.RarFile => dbContext.RarFiles.AnyAsync(x => x.Id == sourceId),
            DavItem.ItemSubType.MultipartFile => dbContext.MultipartFiles.AnyAsync(x => x.Id == sourceId),
            _ => throw new ArgumentOutOfRangeException(nameof(subType), subType, null)
        };
    }

    private static Guid TargetBlobId(Guid sourceId, DavItem.ItemSubType subType)
    {
        var kind = subType switch
        {
            DavItem.ItemSubType.NzbFile => UsenetFileToBlobstoreMigrationService.LegacyFileKind.Nzb,
            DavItem.ItemSubType.RarFile => UsenetFileToBlobstoreMigrationService.LegacyFileKind.Rar,
            DavItem.ItemSubType.MultipartFile => UsenetFileToBlobstoreMigrationService.LegacyFileKind.Multipart,
            _ => throw new ArgumentOutOfRangeException(nameof(subType), subType, null)
        };
        return UsenetFileToBlobstoreMigrationService.CreateTargetBlobId(sourceId, kind);
    }

    private static Task<UsenetFileToBlobstoreMigrationService.MigrationStepResult> MigrateOnceAsync(
        UsenetFileToBlobstoreMigrationService service,
        DavItem.ItemSubType subType)
    {
        return subType switch
        {
            DavItem.ItemSubType.NzbFile => service.MigrateNzbFileOnceAsync(CancellationToken.None),
            DavItem.ItemSubType.RarFile => service.MigrateRarFileOnceAsync(CancellationToken.None),
            DavItem.ItemSubType.MultipartFile => service.MigrateMultipartFileOnceAsync(CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(subType), subType, null)
        };
    }

    private static UsenetFileToBlobstoreMigrationService CreateService(
        Func<DavDatabaseContext> createDbContext)
    {
        return new UsenetFileToBlobstoreMigrationService(new WebsocketManager(), createDbContext);
    }

    private static DavDatabaseContext CreateContext(SaveChangesInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>(
                DavDatabaseContext.CreateSqliteOptions(enforceProviderSelection: true))
            .AddInterceptors(interceptor)
            .Options;
        return new DavDatabaseContext(options);
    }

    private sealed class ThrowBeforeCommitInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            throw new InjectedPreCommitException();
        }
    }

    private sealed class ThrowAfterCommittedSaveInterceptor : SaveChangesInterceptor
    {
        private int _thrown;

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new InjectedAcknowledgementException();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedPreCommitException : Exception;
    private sealed class InjectedAcknowledgementException : Exception;
    private sealed class InjectedReconciliationException : Exception;
}
