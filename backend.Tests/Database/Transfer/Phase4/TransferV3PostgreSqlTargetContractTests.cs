using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using NpgsqlTypes;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3PostgreSqlTargetContractTests
{
    private static readonly Regex SafeIdentifier = new(
        "^[A-Za-z_][A-Za-z0-9_]{0,62}$",
        RegexOptions.CultureInvariant);

    [Fact]
    public void DirectNpgsqlReferenceIsPinnedAndResolvedAt1003()
    {
        var projectPath = SqliteContractTestSupport.AbsolutePath("backend/NzbWebDAV.csproj");
        var project = XDocument.Load(projectPath);
        var package = Assert.Single(project.Descendants("PackageReference"), element =>
            string.Equals((string?)element.Attribute("Include"), "Npgsql", StringComparison.Ordinal));
        Assert.Equal("[10.0.3]", (string?)package.Attribute("Version"));

        var assetsPath = SqliteContractTestSupport.AbsolutePath("backend/obj/project.assets.json");
        using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        Assert.Equal(
            "[10.0.3, 10.0.3]",
            assets.RootElement
                .GetProperty("project")
                .GetProperty("frameworks")
                .GetProperty("net10.0")
                .GetProperty("dependencies")
                .GetProperty("Npgsql")
                .GetProperty("version")
                .GetString());
        var restoredNpgsql = assets.RootElement
            .GetProperty("libraries")
            .GetProperty("Npgsql/10.0.3");
        Assert.Equal("package", restoredNpgsql.GetProperty("type").GetString());
        Assert.Equal(
            "7nb5YzXuvWWJxB0J8DiyL3we+X4FOctZrt0fIBnucOIaIevFEEwGQVZKtiu9olXdlNAK1eNgqSral6r/jlhI4w==",
            restoredNpgsql.GetProperty("sha512").GetString());
    }

    [Fact]
    public void EmbeddedContractHasExactSourceOrderCountsKeysAndBehaviorFlags()
    {
        var source = TransferV3SourceContract.LoadEmbedded();
        var target = TransferV3PostgreSqlTargetContract.LoadEmbedded();

        Assert.Equal(27, target.Tables.Count);
        Assert.Equal(235, target.Tables.Sum(table => table.Columns.Count));
        Assert.Equal(Enumerable.Range(0, 27), target.Tables.Select(table => table.Ordinal));
        Assert.Equal(source.Tables.Select(table => table.Name), target.Tables.Select(table => table.Name));
        Assert.Equal(27, target.Tables.Select(table => table.Name).Distinct(StringComparer.Ordinal).Count());

        for (var tableIndex = 0; tableIndex < source.Tables.Count; tableIndex++)
        {
            var sourceTable = source.Tables[tableIndex];
            var targetTable = target.Tables[tableIndex];
            Assert.Equal(tableIndex, targetTable.Ordinal);
            Assert.Equal(sourceTable.Name, targetTable.Name);
            Assert.Equal(
                sourceTable.Keyset.Select(component => component.Column),
                targetTable.KeyColumns);
            Assert.Equal(
                Enumerable.Range(0, sourceTable.Columns.Count),
                targetTable.Columns.Select(column => column.Ordinal));
            Assert.Equal(
                sourceTable.Columns.Select(column => column.Name),
                targetTable.Columns.Select(column => column.Name));
            Assert.Equal(
                sourceTable.Columns.Select(column => column.Kind),
                targetTable.Columns.Select(column => column.SourceKind));
            Assert.Equal(
                sourceTable.Columns.Select(column => column.Nullable),
                targetTable.Columns.Select(column => column.Nullable));
            Assert.Equal(
                sourceTable.Columns.Count,
                targetTable.Columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(sourceTable.Name == "DavItems", targetTable.PreserveBootstrapRoots);
            Assert.Equal(sourceTable.Name == "ConfigItems", targetTable.FiltersReservedImportState);
        }

        var sourceDerived = Assert.Single(source.DerivedTables);
        var targetDerived = target.DerivedHealthCheckStats;
        Assert.DoesNotContain(targetDerived, target.Tables);
        Assert.Equal(27, targetDerived.Ordinal);
        Assert.Equal("HealthCheckStats", targetDerived.Name);
        Assert.Equal(sourceDerived.Name, targetDerived.Name);
        Assert.Equal(5, targetDerived.Columns.Count);
        Assert.Equal(Enumerable.Range(0, 5), targetDerived.Columns.Select(column => column.Ordinal));
        Assert.Equal(sourceDerived.Columns.Select(column => column.Name),
            targetDerived.Columns.Select(column => column.Name));
        Assert.Equal(sourceDerived.Columns.Select(column => column.Kind),
            targetDerived.Columns.Select(column => column.SourceKind));
        Assert.Equal(sourceDerived.Columns.Select(column => column.Nullable),
            targetDerived.Columns.Select(column => column.Nullable));
        Assert.Equal(sourceDerived.Keyset.Select(component => component.Column), targetDerived.KeyColumns);
        Assert.False(targetDerived.PreserveBootstrapRoots);
        Assert.False(targetDerived.FiltersReservedImportState);

        var allTables = target.Tables.Append(targetDerived).ToArray();
        Assert.Equal(240, allTables.Sum(table => table.Columns.Count));
        Assert.Equal(20, allTables.Sum(table => table.Columns.Count(column => column.Collation == "C")));
        Assert.Equal(59, allTables.Sum(table => table.Columns.Count(column =>
            column.SourceKind == TransferV3ColumnKind.Text && column.Collation is null)));
        Assert.Equal(161, allTables.Sum(table => table.Columns.Count(column =>
            column.SourceKind != TransferV3ColumnKind.Text && column.Collation is null)));
        Assert.Single(target.Tables, table => table.PreserveBootstrapRoots);
        Assert.Single(target.Tables, table => table.FiltersReservedImportState);
    }

    [Fact]
    public void EveryMappingMatchesSourceEfModelAndHeadPhysicalCatalogWithoutFallbacks()
    {
        var target = TransferV3PostgreSqlTargetContract.LoadEmbedded();
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var catalogColumns = ReadCatalogColumns();
        var mapped = target.Tables.Append(target.DerivedHealthCheckStats)
            .SelectMany(table => table.Columns.Select(column => (table, column)))
            .ToArray();

        Assert.Equal(242, catalogColumns.Count);
        Assert.Equal(2, catalogColumns.Count(column =>
            column.Table == DatabaseMigrationPolicy.PostgreSqlHistoryTableName));
        Assert.Equal(240, mapped.Length);

        foreach (var (table, column) in mapped)
        {
            var entity = Assert.Single(model.GetEntityTypes(), entity =>
                string.Equals(entity.GetTableName(), table.Name, StringComparison.Ordinal));
            var storeObject = StoreObjectIdentifier.Table(table.Name, entity.GetSchema());
            var property = Assert.Single(entity.GetProperties(), property =>
                string.Equals(property.GetColumnName(storeObject), column.Name, StringComparison.Ordinal));
            var catalog = Assert.Single(catalogColumns, value =>
                value.Table == table.Name && value.Name == column.Name);

            Assert.Equal(property.GetColumnType(), column.PostgreSqlType);
            Assert.Equal(!property.IsNullable, catalog.NotNull);
            Assert.Equal(property.IsNullable, column.Nullable);
            Assert.Equal(property.GetCollation(), column.Collation);
            Assert.Equal(column.PostgreSqlType, catalog.PostgreSqlType);
            Assert.Equal(ExpectedCatalogCollation(column), catalog.Collation);
            Assert.Equal(ExpectedCopyType(column.SourceKind), column.BinaryCopyType);
            AssertKindType(column);
            Assert.Equal(string.Empty, catalog.Identity);
            Assert.Equal(string.Empty, catalog.Generated);
            Assert.Null(property.GetComputedColumnSql());
        }

        var defaults = mapped
            .Select(pair => (pair.table, pair.column, catalog: Assert.Single(catalogColumns, value =>
                value.Table == pair.table.Name && value.Name == pair.column.Name)))
            .Where(pair => pair.catalog.HasDefault)
            .ToArray();
        var leaseDefault = Assert.Single(defaults);
        Assert.Equal("WorkerJobs", leaseDefault.table.Name);
        Assert.Equal("LeaseGeneration", leaseDefault.column.Name);
        Assert.Equal("0", leaseDefault.catalog.DefaultExpression);
        Assert.Contains("\"LeaseGeneration\"", target.GetCopyColumnList(leaseDefault.table),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FragmentsAreExactAndAcceptOnlyOwnedTableReferences()
    {
        var contract = TransferV3PostgreSqlTargetContract.LoadEmbedded();
        foreach (var table in contract.Tables.Append(contract.DerivedHealthCheckStats))
        {
            Assert.Matches(SafeIdentifier, table.Name);
            Assert.All(table.Columns, column => Assert.Matches(SafeIdentifier, column.Name));
            Assert.All(table.KeyColumns, column => Assert.Matches(SafeIdentifier, column));
            Assert.Equal(Quote(table.Name), contract.GetQuotedTableName(table));
            Assert.Equal(ExpectedOrderBy(table), contract.GetOrderByList(table));
            Assert.Equal(ExpectedSelectProjection(table), contract.GetSelectProjection(table));

            if (ReferenceEquals(table, contract.DerivedHealthCheckStats))
                Assert.Throws<InvalidOperationException>(() => contract.GetCopyColumnList(table));
            else
                Assert.Equal(string.Join(", ", table.Columns.Select(column => Quote(column.Name))),
                    contract.GetCopyColumnList(table));
        }

        Assert.Equal(
            "\"Type\", \"Username\" COLLATE pg_catalog.\"C\"",
            contract.GetOrderByList(Assert.Single(contract.Tables, table => table.Name == "Accounts")));
        Assert.Equal(
            "\"ConfigName\" COLLATE pg_catalog.\"C\"",
            contract.GetOrderByList(Assert.Single(contract.Tables, table => table.Name == "ConfigItems")));
        Assert.Equal(
            2,
            contract.Tables.Count(table => contract.GetOrderByList(table)
                .Contains(" COLLATE pg_catalog.\"C\"", StringComparison.Ordinal)));

        var owned = contract.Tables[0];
        var clone = owned with { };
        Assert.Equal(owned, clone);
        Assert.NotSame(owned, clone);
        var foreignContract = TransferV3PostgreSqlTargetContract.LoadEmbedded();
        var foreign = foreignContract.Tables[0];
        Assert.NotSame(owned, foreign);

        foreach (var rejected in new[] { clone, foreign })
        {
            Assert.Throws<ArgumentException>(() => contract.GetQuotedTableName(rejected));
            Assert.Throws<ArgumentException>(() => contract.GetCopyColumnList(rejected));
            Assert.Throws<ArgumentException>(() => contract.GetOrderByList(rejected));
            Assert.Throws<ArgumentException>(() => contract.GetSelectProjection(rejected));
        }
        Assert.Throws<ArgumentNullException>(() => contract.GetQuotedTableName(null!));
        Assert.Throws<ArgumentNullException>(() => contract.GetCopyColumnList(null!));
        Assert.Throws<ArgumentNullException>(() => contract.GetOrderByList(null!));
        Assert.Throws<ArgumentNullException>(() => contract.GetSelectProjection(null!));
    }

    [Fact]
    public void ExposedGraphIsADeepImmutableDefensiveSnapshot()
    {
        var bytes = ReadTargetJson();
        var contract = TransferV3PostgreSqlTargetContract.Parse(bytes);
        var first = contract.Tables[0];
        var quoted = contract.GetQuotedTableName(first);
        var copy = contract.GetCopyColumnList(first);
        var order = contract.GetOrderByList(first);
        var projection = contract.GetSelectProjection(first);

        AssertMutationFails(contract.Tables);
        foreach (var table in contract.Tables.Append(contract.DerivedHealthCheckStats))
        {
            AssertMutationFails(table.Columns);
            AssertMutationFails(table.KeyColumns);
        }

        Array.Fill(bytes, (byte)'x');
        Assert.Equal("Accounts", first.Name);
        Assert.Equal(quoted, contract.GetQuotedTableName(first));
        Assert.Equal(copy, contract.GetCopyColumnList(first));
        Assert.Equal(order, contract.GetOrderByList(first));
        Assert.Equal(projection, contract.GetSelectProjection(first));

        var callerOwnedColumns = first.Columns.ToList();
        var callerOwnedKeys = first.KeyColumns.ToList();
        var callerClone = first with { Columns = callerOwnedColumns, KeyColumns = callerOwnedKeys };
        callerOwnedColumns.Reverse();
        callerOwnedKeys.Clear();
        Assert.Equal(copy, contract.GetCopyColumnList(first));
        Assert.Equal(order, contract.GetOrderByList(first));
        Assert.Throws<ArgumentException>(() => contract.GetQuotedTableName(callerClone));
    }

    [Fact]
    public void ParserAcceptsObjectPropertyReorderingAndInsignificantWhitespace()
    {
        var root = ReadTargetRoot();
        var reordered = new JsonObject();
        foreach (var property in root.Reverse())
            reordered[property.Key] = property.Value?.DeepClone();

        var parsed = TransferV3PostgreSqlTargetContract.Parse(
            Encoding.UTF8.GetBytes(reordered.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            })));
        Assert.Equal(27, parsed.Tables.Count);
        Assert.Equal("HealthCheckStats", parsed.DerivedHealthCheckStats.Name);
    }

    [Fact]
    public void ParserRejectsEveryForbiddenJsonShapeCategory()
    {
        var valid = Encoding.UTF8.GetString(ReadTargetJson());
        var trailing = valid.TrimEnd();
        trailing = trailing[..^1] + ",}";

        var cases = new (string Name, byte[] Json)[]
        {
            ("top-level duplicate", Utf8(valid.Replace(
                "\"formatVersion\": 3,",
                "\"formatVersion\": 3, \"formatVersion\": 3,",
                StringComparison.Ordinal))),
            ("table duplicate", Utf8(valid.Replace(
                "\"preserveBootstrapRoots\": false,",
                "\"preserveBootstrapRoots\": false, \"preserveBootstrapRoots\": false,",
                StringComparison.Ordinal))),
            ("column duplicate", Utf8(valid.Replace(
                "\"postgreSqlType\": \"integer\",",
                "\"postgreSqlType\": \"integer\", \"postgreSqlType\": \"integer\",",
                StringComparison.Ordinal))),
            ("missing top property", Mutate(root => root.Remove("formatVersion"))),
            ("missing table property", Mutate(root => FirstTable(root).Remove("keyColumns"))),
            ("missing column property", Mutate(root => FirstColumn(root).Remove("nullable"))),
            ("unknown top property", Mutate(root => root["unknown"] = true)),
            ("unknown table property", Mutate(root => FirstTable(root)["unknown"] = true)),
            ("unknown column property", Mutate(root => FirstColumn(root)["unknown"] = true)),
            ("case-mismatched property", Utf8(valid.Replace(
                "\"formatVersion\"", "\"FormatVersion\"", StringComparison.Ordinal))),
            ("null non-nullable top", Mutate(root => root["tables"] = null)),
            ("null non-nullable table", Mutate(root => FirstTable(root)["name"] = null)),
            ("null non-nullable column", Mutate(root => FirstColumn(root)["name"] = null)),
            ("null key column", Mutate(root => FirstKeyColumns(root)[0] = null)),
            ("comment", Utf8("/* forbidden */" + valid)),
            ("trailing comma", Utf8(trailing)),
            ("unknown source enum", Mutate(root => FirstColumn(root)["sourceKind"] = "unknown")),
            ("numeric source enum", Mutate(root => FirstColumn(root)["sourceKind"] = 2)),
            ("unknown copy enum", Mutate(root => FirstColumn(root)["binaryCopyType"] = "unknown")),
            ("numeric copy enum", Mutate(root => FirstColumn(root)["binaryCopyType"] = 2)),
            ("non-integral table ordinal", Mutate(root => FirstTable(root)["ordinal"] = 0.5)),
            ("non-integral column ordinal", Mutate(root => FirstColumn(root)["ordinal"] = 0.5)),
            ("wrong format version type", Mutate(root => root["formatVersion"] = "3")),
            ("wrong array type", Mutate(root => root["tables"] = new JsonObject())),
            ("wrong boolean type", Mutate(root => FirstTable(root)["preserveBootstrapRoots"] = "false")),
            ("wrong nullable type", Mutate(root => FirstColumn(root)["nullable"] = 0)),
        };

        Assert.All(cases, testCase => AssertInvalid(testCase.Name, testCase.Json));
    }

    [Fact]
    public void ParserRejectsEveryReviewedSourceModelAndCatalogFactMismatch()
    {
        var cases = new (string Name, byte[] Json)[]
        {
            ("format version", Mutate(root => root["formatVersion"] = 4)),
            ("table ordinal", Mutate(root => FirstTable(root)["ordinal"] = 1)),
            ("table name", Mutate(root => FirstTable(root)["name"] = "Accounts2")),
            ("table order", Mutate(root => Swap((JsonArray)root["tables"]!, 0, 1))),
            ("column ordinal", Mutate(root => FirstColumn(root)["ordinal"] = 1)),
            ("column name", Mutate(root => FirstColumn(root)["name"] = "Type2")),
            ("column order", Mutate(root => Swap((JsonArray)FirstTable(root)["columns"]!, 0, 1))),
            ("source kind", Mutate(root => FirstColumn(root)["sourceKind"] = "int32")),
            ("target type", Mutate(root => FirstColumn(root)["postgreSqlType"] = "bigint")),
            ("collation", Mutate(root => FirstColumn(root)["collation"] = "C")),
            ("nullability", Mutate(root => FirstColumn(root)["nullable"] = true)),
            ("binary copy type", Mutate(root => FirstColumn(root)["binaryCopyType"] = "bigint")),
            ("key order", Mutate(root => Swap(FirstKeyColumns(root), 0, 1))),
            ("unknown key", Mutate(root => FirstKeyColumns(root)[0] = "Unknown")),
            ("bootstrap flag", Mutate(root => FirstTable(root)["preserveBootstrapRoots"] = true)),
            ("reserved flag", Mutate(root => FirstTable(root)["filtersReservedImportState"] = true)),
            ("derived ordinal", Mutate(root => Derived(root)["ordinal"] = 26)),
            ("derived name", Mutate(root => Derived(root)["name"] = "HealthCheckStats2")),
            ("derived column order", Mutate(root => Swap((JsonArray)Derived(root)["columns"]!, 0, 1))),
            ("derived key order", Mutate(root => Swap((JsonArray)Derived(root)["keyColumns"]!, 0, 1))),
            ("derived flag", Mutate(root => Derived(root)["preserveBootstrapRoots"] = true)),
        };

        Assert.All(cases, testCase => AssertInvalid(testCase.Name, testCase.Json));
    }

    [Fact]
    public void SourceOrderIsNotReplacedByPhysicalAttnumForTheFiveDivergentTables()
    {
        var source = TransferV3SourceContract.LoadEmbedded();
        var catalog = ReadCatalogColumns();
        var divergent = source.Tables
            .Where(table => !table.Columns.Select(column => column.Name).SequenceEqual(
                catalog.Where(column => column.Table == table.Name)
                    .OrderBy(column => column.Attnum)
                    .Select(column => column.Name),
                StringComparer.Ordinal))
            .Select(table => table.Name)
            .ToArray();

        Assert.Equal(
        [
            "QueueItems",
            "DavItems",
            "ArrDownloadCorrelations",
            "WorkerJobs",
            "RcloneInvalidationItems",
        ], divergent);

        foreach (var tableName in divergent)
        {
            var physicalOrder = catalog.Where(column => column.Table == tableName)
                .OrderBy(column => column.Attnum)
                .Select(column => column.Name)
                .ToArray();
            var json = Mutate(root =>
            {
                var table = Assert.Single(((JsonArray)root["tables"]!).Select(node => node!.AsObject()),
                    value => value["name"]!.GetValue<string>() == tableName);
                var current = ((JsonArray)table["columns"]!).Select(node => node!.DeepClone())
                    .ToDictionary(node => node!["name"]!.GetValue<string>(), StringComparer.Ordinal);
                var reordered = new JsonArray();
                for (var index = 0; index < physicalOrder.Length; index++)
                {
                    var column = current[physicalOrder[index]]!.AsObject();
                    column["ordinal"] = index;
                    reordered.Add(column);
                }
                table["columns"] = reordered;
            });
            AssertInvalid($"physical attnum order for {tableName}", json);
        }
    }

    private static void AssertKindType(TransferV3PostgreSqlColumnContract column)
    {
        switch (column.SourceKind)
        {
            case TransferV3ColumnKind.Uuid:
                Assert.Equal("uuid", column.PostgreSqlType);
                break;
            case TransferV3ColumnKind.Boolean:
                Assert.Equal("boolean", column.PostgreSqlType);
                break;
            case TransferV3ColumnKind.EnumInt32:
            case TransferV3ColumnKind.Int32:
                Assert.Equal("integer", column.PostgreSqlType);
                break;
            case TransferV3ColumnKind.Int64:
            case TransferV3ColumnKind.Instant:
                Assert.Equal("bigint", column.PostgreSqlType);
                break;
            case TransferV3ColumnKind.Text:
                Assert.True(column.PostgreSqlType == "text"
                            || Regex.IsMatch(column.PostgreSqlType,
                                "^character varying\\([1-9][0-9]*\\)$",
                                RegexOptions.CultureInvariant));
                break;
            case TransferV3ColumnKind.LocalWallTimestamp:
                Assert.Equal("timestamp without time zone", column.PostgreSqlType);
                break;
            default:
                throw new Xunit.Sdk.XunitException($"Unexpected source kind {column.SourceKind}.");
        }
    }

    private static NpgsqlDbType ExpectedCopyType(TransferV3ColumnKind kind) => kind switch
    {
        TransferV3ColumnKind.Uuid => NpgsqlDbType.Uuid,
        TransferV3ColumnKind.Boolean => NpgsqlDbType.Boolean,
        TransferV3ColumnKind.EnumInt32 or TransferV3ColumnKind.Int32 => NpgsqlDbType.Integer,
        TransferV3ColumnKind.Int64 or TransferV3ColumnKind.Instant => NpgsqlDbType.Bigint,
        TransferV3ColumnKind.Text => NpgsqlDbType.Text,
        TransferV3ColumnKind.LocalWallTimestamp => NpgsqlDbType.Timestamp,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string ExpectedCatalogCollation(TransferV3PostgreSqlColumnContract column) =>
        column.Collation == "C"
            ? "pg_catalog.C"
            : column.SourceKind == TransferV3ColumnKind.Text
                ? "pg_catalog.default"
                : string.Empty;

    private static string ExpectedOrderBy(TransferV3PostgreSqlTableContract table) =>
        string.Join(", ", table.KeyColumns.Select(key =>
        {
            var column = Assert.Single(table.Columns, column => column.Name == key);
            return Quote(key) + (column.Collation == "C" ? " COLLATE pg_catalog.\"C\"" : string.Empty);
        }));

    private static string ExpectedSelectProjection(TransferV3PostgreSqlTableContract table) =>
        string.Join(", ", table.Columns.SelectMany(column =>
            column.SourceKind == TransferV3ColumnKind.Text
                ? new[] { $"pg_catalog.octet_length({Quote(column.Name)})", Quote(column.Name) }
                : new[] { Quote(column.Name) }));

    private static string Quote(string identifier) => $"\"{identifier}\"";

    private static void AssertMutationFails<T>(IReadOnlyList<T> values)
    {
        Assert.NotEmpty(values);
        Assert.False(values is T[]);
        Assert.False(values is List<T>);
        var mutableView = Assert.IsAssignableFrom<IList<T>>(values);
        Assert.True(mutableView.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = mutableView[0]);
        Assert.Throws<NotSupportedException>(() => mutableView.Add(mutableView[0]));
        Assert.Throws<NotSupportedException>(() => mutableView.RemoveAt(0));
    }

    private static void AssertInvalid(string caseName, byte[] json)
    {
        var exception = Record.Exception(() => TransferV3PostgreSqlTargetContract.Parse(json));
        Assert.True(exception is InvalidDataException,
            $"{caseName} should fail closed with InvalidDataException, got {exception?.GetType().Name ?? "success"}.");
    }

    private static byte[] Mutate(Action<JsonObject> mutation)
    {
        var root = ReadTargetRoot();
        mutation(root);
        return Utf8(root.ToJsonString());
    }

    private static JsonObject ReadTargetRoot() =>
        JsonNode.Parse(ReadTargetJson())!.AsObject();

    private static byte[] ReadTargetJson() => File.ReadAllBytes(
        SqliteContractTestSupport.AbsolutePath(
            "backend/Database/Transfer/Contracts/transfer-v3-postgresql-target-contract.json"));

    private static JsonObject FirstTable(JsonObject root) =>
        ((JsonArray)root["tables"]!)[0]!.AsObject();

    private static JsonObject FirstColumn(JsonObject root) =>
        ((JsonArray)FirstTable(root)["columns"]!)[0]!.AsObject();

    private static JsonArray FirstKeyColumns(JsonObject root) =>
        (JsonArray)FirstTable(root)["keyColumns"]!;

    private static JsonObject Derived(JsonObject root) =>
        root["derivedHealthCheckStats"]!.AsObject();

    private static void Swap(JsonArray values, int first, int second)
    {
        var firstClone = values[first]!.DeepClone();
        var secondClone = values[second]!.DeepClone();
        values[first] = secondClone;
        values[second] = firstClone;
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static IReadOnlyList<CatalogColumn> ReadCatalogColumns() =>
        PostgreSqlPhysicalCatalogContract.ReadExpectedInventory(PostgreSqlCatalogState.Head)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("column|", StringComparison.Ordinal))
            .Select(line =>
            {
                var parts = line.Split('|');
                Assert.Equal(23, parts.Length);
                return new CatalogColumn(
                    parts[1],
                    int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                    parts[3],
                    parts[5],
                    bool.Parse(parts[8]),
                    bool.Parse(parts[9]),
                    parts[12],
                    parts[13],
                    parts[14],
                    parts[21]);
            })
            .ToArray();

    private static PostgreSqlDavDatabaseContext CreateContext()
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Database = "nzbdav",
            Username = "nzbdav",
            Password = "not-used",
            Timezone = TimeZoneInfo.Local.Id,
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<PostgreSqlDavDatabaseContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsHistoryTable(
                    DatabaseMigrationPolicy.PostgreSqlHistoryTableName))
            .Options;
        return new PostgreSqlDavDatabaseContext(options);
    }

    private sealed record CatalogColumn(
        string Table,
        int Attnum,
        string Name,
        string PostgreSqlType,
        bool NotNull,
        bool HasDefault,
        string Identity,
        string Generated,
        string Collation,
        string DefaultExpression);
}
