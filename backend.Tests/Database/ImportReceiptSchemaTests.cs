using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace backend.Tests.Database;

public sealed class ImportReceiptSchemaTests
{
    [Fact]
    public void ExistingHistoryPropertiesKeepNativeProviderTypes()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var dbContext = new DavDatabaseContext(options);

        var history = dbContext.Model.FindEntityType(typeof(HistoryItem));
        var cleanup = dbContext.Model.FindEntityType(typeof(HistoryCleanupItem));
        Assert.NotNull(history);
        Assert.NotNull(cleanup);
        AssertNativeProviderType(history, nameof(HistoryItem.Id), typeof(Guid));
        AssertNativeProviderType(history, nameof(HistoryItem.CreatedAt), typeof(DateTime));
        AssertNativeProviderType(history, nameof(HistoryItem.DownloadDirId), typeof(Guid));
        AssertNativeProviderType(history, nameof(HistoryItem.NzbBlobId), typeof(Guid));
        AssertNativeProviderType(cleanup, nameof(HistoryCleanupItem.Id), typeof(Guid));
    }

    [Fact]
    public void ExistingHistoryModelMatchesMigrationSnapshot()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var dbContext = new DavDatabaseContext(options);
        var snapshotModel = dbContext.GetService<IModelRuntimeInitializer>().Initialize(
            dbContext.GetService<IMigrationsAssembly>().ModelSnapshot!.Model,
            designTime: true,
            validationLogger: null);
        var designTimeModel = dbContext.GetService<IDesignTimeModel>().Model;
        var operations = dbContext.GetService<IMigrationsModelDiffer>()
            .GetDifferences(snapshotModel.GetRelationalModel(), designTimeModel.GetRelationalModel())
            .ToArray();

        Assert.Empty(operations);
    }

    private static void AssertNativeProviderType(
        IEntityType entityType,
        string propertyName,
        Type expectedType)
    {
        var property = entityType.FindProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expectedType, Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType);
        Assert.Null(property.GetProviderClrType());
    }
}
