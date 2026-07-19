using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3SourceContractTests
{
    private static readonly string[] ExpectedDependencyOrder =
    [
        "Accounts",
        "ConfigItems",
        "HistoryItems",
        "QueueItems",
        "RepairRuns",
        "DavItems",
        "ArrImportCommands",
        "QueueNzbContents",
        "QueuePriorityHints",
        "RepairEntryHealth",
        "RepairBrokenFiles",
        "DavNzbFiles",
        "DavRarFiles",
        "DavMultipartFiles",
        "HealthCheckResults",
        "ArrDownloadCorrelations",
        "ArrDownloadLifecycleEvents",
        "ArrSearchNudgeCommands",
        "ImportReceipts",
        "WorkerJobs",
        "MaintenanceRuns",
        "BlobCleanupItems",
        "HistoryCleanupItems",
        "DavCleanupItems",
        "NzbNames",
        "NzbBlobCleanupItems",
        "RcloneInvalidationItems",
    ];

    [Fact]
    public void EmbeddedContract_FreezesExactCurrentSourceAndTransferShape()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();

        Assert.Equal(3, contract.FormatVersion);
        Assert.Equal(49, contract.Migrations.Count);
        Assert.Equal(28, contract.SourceModeledTableCount);
        Assert.Equal(240, contract.SourceModeledColumnCount);
        Assert.Equal(27, contract.Tables.Count);
        Assert.Equal(235, contract.Tables.Sum(table => table.Columns.Count));
        Assert.Single(contract.DerivedTables);
        Assert.Equal("HealthCheckStats", contract.DerivedTables[0].Name);
        Assert.Equal(5, contract.DerivedTables[0].Columns.Count);
        Assert.Equal(ExpectedDependencyOrder, contract.Tables.Select(table => table.Name));
        Assert.Equal(["HealthCheckStats"], contract.DerivedExcludedTables);
        Assert.Equal(["database.import-state"], contract.ExcludedConfigKeys);
        Assert.Equal(46, contract.Tables.Sum(table => table.Columns.Count(column => column.Kind == TransferV3ColumnKind.Uuid)));
        Assert.Equal(6, contract.Tables.Sum(table => table.Columns.Count(column => column.Kind == TransferV3ColumnKind.Boolean)));
        Assert.Equal(47, contract.Tables.Sum(table => table.Columns.Count(column => column.MaxRunes is not null)));
        Assert.Equal(4, contract.Tables.Sum(table => table.Columns.Count(column => column.Kind == TransferV3ColumnKind.LocalWallTimestamp)));
        Assert.All(contract.Tables, table => Assert.NotEmpty(table.Keyset));
        Assert.All(contract.Tables.Concat(contract.DerivedTables), table =>
            Assert.All(table.UniqueKeys, unique =>
                Assert.Equal(unique.Columns, unique.Components.Select(component => component.Column))));
        Assert.All(
            contract.Migrations,
            migration => Assert.NotEmpty(migration.AllowedProductVersions));

        var expectedVersions = new[] { "9.0.4", "10.0.1", "10.0.4", "10.0.9" };
        Assert.Equal(
            new[] { 21, 11, 1, 16 },
            expectedVersions.Select(version => contract.Migrations.Count(
                migration => migration.IntroducedProductVersion == version)));
        Assert.All(contract.Migrations, migration =>
        {
            var first = Array.IndexOf(expectedVersions, migration.IntroducedProductVersion);
            Assert.True(first >= 0);
            Assert.Equal(expectedVersions[first..], migration.AllowedProductVersions);
        });
    }

    [Fact]
    public void EmbeddedContract_MatchesReviewedMigrationAndRawSchemaEvidence()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var migrationEvidence = TransferV3SourceContract.ReadEmbeddedMigrationSourceContract();
        var schemaEvidence = TransferV3SourceContract.ReadEmbeddedSourceSchema();
        using var migrationJson = JsonDocument.Parse(migrationEvidence);
        using var schemaJson = JsonDocument.Parse(schemaEvidence);

        var migrationIds = migrationJson.RootElement.GetProperty("migrations")
            .EnumerateArray()
            .Select(value => value.GetProperty("id").GetString())
            .ToArray();
        var schemaTables = schemaJson.RootElement.GetProperty("logicalTables")
            .EnumerateArray()
            .ToDictionary(
                value => value.GetProperty("name").GetString()!,
                value => value,
                StringComparer.Ordinal);

        Assert.Equal(migrationIds, contract.Migrations.Select(value => value.Id));
        Assert.Equal(28, schemaTables.Count);
        Assert.Equal(240, schemaTables.Values.Sum(value => value.GetProperty("columns").GetArrayLength()));
        Assert.Equal(
            schemaTables.Keys.Where(name => name != "HealthCheckStats").Order(StringComparer.Ordinal),
            contract.Tables.Select(table => table.Name).Order(StringComparer.Ordinal));
        Assert.All(contract.Tables, table =>
        {
            var expected = schemaTables[table.Name].GetProperty("columns")
                .EnumerateArray()
                .Select(value => value.GetProperty("name").GetString())
                .ToArray();
            Assert.Equal(expected, table.Columns.Select(column => column.Name));
        });
        Assert.Equal(
            schemaTables["HealthCheckStats"].GetProperty("columns").EnumerateArray()
                .Select(value => value.GetProperty("name").GetString()),
            contract.DerivedTables.Single().Columns.Select(column => column.Name));
        Assert.Equal(49, migrationIds.Length);
        Assert.Contains("20251106165542_Ensure-Strm-Key-Exists", migrationIds);
    }

    [Fact]
    public void EmbeddedContract_MatchesCurrentEfModelKindsLengthsAndDeclaredForeignKeys()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        using var context = new DavDatabaseContext();
        var entities = context.Model.GetEntityTypes()
            .ToDictionary(entity => entity.GetTableName()!, StringComparer.Ordinal);

        Assert.Equal(46, entities.Values.SelectMany(entity => entity.GetProperties())
            .Count(property => Nullable.GetUnderlyingType(property.ClrType) == typeof(Guid)
                               || property.ClrType == typeof(Guid)));

        foreach (var table in contract.Tables.Concat(contract.DerivedTables))
        {
            var entity = entities[table.Name];
            foreach (var column in table.Columns)
            {
                var property = entity.GetProperties().Single(value => value.GetColumnName() == column.Name);
                Assert.Equal(property.IsNullable, column.Nullable);
                Assert.Equal(property.GetMaxLength(), column.MaxRunes);
                Assert.Equal(ExpectedKind(property), column.Kind);
                if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType).IsEnum)
                {
                    if (table.Name == "MaintenanceRuns" && column.Name == "Kind")
                    {
                        Assert.Equal([0L, 1L, 2L, 3L], column.AllowedIntegers);
                        Assert.Equal(
                            [0L, 1L],
                            Enum.GetValues(Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType)
                                .Cast<object>().Select(Convert.ToInt64).Order());
                        continue;
                    }

                    Assert.Equal(
                        Enum.GetValues(Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType)
                            .Cast<object>().Select(Convert.ToInt64).Order(),
                        column.AllowedIntegers);
                }
            }

            Assert.All(table.UniqueKeys.SelectMany(key => key.Components), component =>
            {
                var column = table.Columns.Single(value => value.Name == component.Column);
                Assert.Equal(column.Kind == TransferV3ColumnKind.Text ? "BINARY" : "none", component.SqliteCollation);
                Assert.Equal(column.Kind == TransferV3ColumnKind.Text ? "C" : "none", component.PostgreSqlCollation);
            });

            var expectedForeignKeys = entity.GetForeignKeys()
                .Select(foreignKey => string.Join(
                    "|",
                    string.Join(",", foreignKey.Properties.Select(property => property.GetColumnName())),
                    foreignKey.PrincipalEntityType.GetTableName()!,
                    string.Join(",", foreignKey.PrincipalKey.Properties
                        .Select(property => property.GetColumnName()))))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var actualForeignKeys = table.References
                .Where(reference => reference.Policy == TransferV3ReferencePolicy.DeclaredForeignKeyHard)
                .Select(reference => string.Join(
                    "|",
                    string.Join(",", reference.Columns),
                    reference.PrincipalTables.Single(),
                    string.Join(",", reference.PrincipalColumns)))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedForeignKeys, actualForeignKeys);
        }
    }

    [Fact]
    public void EmbeddedContract_FreezesExactApplicationReferencePolicyMatrix()
    {
        var contract = TransferV3SourceContract.LoadEmbedded();
        var actual = contract.Tables.SelectMany(table => table.References.Select(reference => string.Join(
            "|",
            table.Name,
            reference.Name,
            string.Join(",", reference.Columns),
            string.Join(",", reference.PrincipalTables),
            string.Join(",", reference.PrincipalColumns),
            reference.Policy))).ToArray();

        Assert.Equal(
        [
            "HistoryItems|HistoryItems_DownloadDirId|DownloadDirId|DavItems|Id|InformationalDigest",
            "HistoryItems|HistoryItems_NzbBlobId_Blob|NzbBlobId|@blob|Id|BlobAndNameHard",
            "HistoryItems|HistoryItems_NzbBlobId_NzbNames|NzbBlobId|NzbNames|Id|ApplicationHard",
            "QueueItems|QueueItems_Id_Blob|Id|@blob|Id|BlobAndNameHard",
            "QueueItems|QueueItems_Id_NzbNames|Id|NzbNames|Id|ApplicationHard",
            "DavItems|DavItems_ParentId|ParentId|DavItems,DavCleanupItems|Id|StateAwareHard",
            "DavItems|DavItems_HistoryItemId|HistoryItemId|HistoryItems,HistoryCleanupItems|Id|StateAwareHard",
            "DavItems|DavItems_FileBlobId|FileBlobId|@blob|Id|BlobHard",
            "DavItems|DavItems_NzbBlobId_Blob|NzbBlobId|@blob|Id|BlobAndNameHard",
            "DavItems|DavItems_NzbBlobId_NzbNames|NzbBlobId|NzbNames|Id|ApplicationHard",
            "ArrImportCommands|FK_ArrImportCommands_HistoryItems_HistoryItemId|HistoryItemId|HistoryItems|Id|DeclaredForeignKeyHard",
            "QueueNzbContents|FK_QueueNzbContents_QueueItems_Id|Id|QueueItems|Id|DeclaredForeignKeyHard",
            "QueuePriorityHints|FK_QueuePriorityHints_QueueItems_QueueItemId|QueueItemId|QueueItems|Id|DeclaredForeignKeyHard",
            "RepairEntryHealth|FK_RepairEntryHealth_RepairRuns_RepairRunId|RepairRunId|RepairRuns|Id|DeclaredForeignKeyHard",
            "RepairEntryHealth|RepairEntryHealth_DavItemId|DavItemId|DavItems|Id|InformationalDigest",
            "RepairBrokenFiles|FK_RepairBrokenFiles_RepairRuns_RepairRunId|RepairRunId|RepairRuns|Id|DeclaredForeignKeyHard",
            "RepairBrokenFiles|RepairBrokenFiles_DavItemId|DavItemId|DavItems|Id|InformationalDigest",
            "DavNzbFiles|FK_DavNzbFiles_DavItems_Id|Id|DavItems|Id|DeclaredForeignKeyHard",
            "DavRarFiles|FK_DavRarFiles_DavItems_Id|Id|DavItems|Id|DeclaredForeignKeyHard",
            "DavMultipartFiles|FK_DavMultipartFiles_DavItems_Id|Id|DavItems|Id|DeclaredForeignKeyHard",
            "HealthCheckResults|HealthCheckResults_DavItemId|DavItemId|DavItems|Id|InformationalDigest",
            "ArrDownloadCorrelations|ArrDownloadCorrelations_QueueItemId|QueueItemId|QueueItems|Id|InformationalDigest",
            "ArrDownloadCorrelations|ArrDownloadCorrelations_HistoryItemId|HistoryItemId|HistoryItems|Id|InformationalDigest",
            "ArrDownloadLifecycleEvents|ArrDownloadLifecycleEvents_QueueItemId|QueueItemId|QueueItems|Id|InformationalDigest",
            "ArrDownloadLifecycleEvents|ArrDownloadLifecycleEvents_HistoryItemId|HistoryItemId|HistoryItems|Id|InformationalDigest",
            "ImportReceipts|ImportReceipts_DavItemId|DavItemId|DavItems|Id|InformationalDigest",
            "ImportReceipts|ImportReceipts_HistoryItemId|HistoryItemId|HistoryItems|Id|InformationalDigest",
            "WorkerJobs|WorkerJobs_TargetId|TargetId|QueueItems,DavItems|Id|PolymorphicInformationalDigest",
            "BlobCleanupItems|BlobCleanupItems_Id|Id|@blob|Id|CleanupTombstone",
            "HistoryCleanupItems|HistoryCleanupItems_Id|Id|HistoryItems|Id|CleanupTombstone",
            "DavCleanupItems|DavCleanupItems_Id|Id|DavItems|Id|CleanupTombstone",
            "NzbNames|NzbNames_Id_Blob|Id|@blob,NzbBlobCleanupItems|Id|ConditionalCleanupTombstone",
            "NzbBlobCleanupItems|NzbBlobCleanupItems_Id|Id|@blob|Id|CleanupTombstone",
        ],
        actual);
        Assert.All(contract.Tables.SelectMany(table => table.References), reference =>
            Assert.False(string.IsNullOrWhiteSpace(reference.Rationale)));

        var workerTarget = contract.Tables.Single(table => table.Name == "WorkerJobs")
            .References.Single(reference => reference.Name == "WorkerJobs_TargetId");
        Assert.Equal("Kind", workerTarget.DiscriminatorColumn);
        Assert.Equal(
            [(1L, "QueueItems"), (2L, "DavItems"), (3L, "DavItems")],
            workerTarget.PolymorphicCases!.Select(value =>
                (value.DiscriminatorValue, value.PrincipalTable)));

        var metadata = contract.Tables.Single(table => table.Name == "DavItems").MetadataRule!;
        Assert.Equal(
            [(1L, new long[] { 101, 102, 103, 104, 105, 106 }), (2L, new long[] { 201, 202, 203 })],
            metadata.TypeDomains.Select(value => (value.Type, value.SubTypes.ToArray())));
    }

    private static TransferV3ColumnKind ExpectedKind(IProperty property)
    {
        var clrType = property.ClrType;
        var unwrapped = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (unwrapped == typeof(Guid)) return TransferV3ColumnKind.Uuid;
        if (unwrapped == typeof(bool)) return TransferV3ColumnKind.Boolean;
        if (unwrapped.IsEnum) return TransferV3ColumnKind.EnumInt32;
        if (unwrapped == typeof(int)) return TransferV3ColumnKind.Int32;
        if (unwrapped == typeof(long)) return TransferV3ColumnKind.Int64;
        if (unwrapped == typeof(DateTime)) return TransferV3ColumnKind.LocalWallTimestamp;
        if (unwrapped == typeof(DateTimeOffset)) return TransferV3ColumnKind.Instant;
        if (unwrapped == typeof(string)
            || property.GetTypeMapping().Converter?.ProviderClrType == typeof(string))
            return TransferV3ColumnKind.Text;
        throw new InvalidOperationException($"Unhandled CLR type '{clrType}'.");
    }

}
