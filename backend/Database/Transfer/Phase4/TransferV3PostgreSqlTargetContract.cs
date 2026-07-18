using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using NpgsqlTypes;

namespace NzbWebDAV.Database.Transfer;

internal sealed record TransferV3PostgreSqlColumnContract(
    int Ordinal,
    string Name,
    TransferV3ColumnKind SourceKind,
    string PostgreSqlType,
    string? Collation,
    bool Nullable,
    NpgsqlDbType BinaryCopyType);

internal sealed record TransferV3PostgreSqlTableContract(
    int Ordinal,
    string Name,
    IReadOnlyList<TransferV3PostgreSqlColumnContract> Columns,
    IReadOnlyList<string> KeyColumns,
    bool PreserveBootstrapRoots,
    bool FiltersReservedImportState);

internal sealed class TransferV3PostgreSqlTargetContract
{
    private const string ResourceSuffix =
        ".Database.Transfer.Contracts.transfer-v3-postgresql-target-contract.json";

    private static readonly string[] RootProperties =
    [
        "formatVersion",
        "tables",
        "derivedHealthCheckStats",
    ];

    private static readonly string[] TableProperties =
    [
        "ordinal",
        "name",
        "columns",
        "keyColumns",
        "preserveBootstrapRoots",
        "filtersReservedImportState",
    ];

    private static readonly string[] ColumnProperties =
    [
        "ordinal",
        "name",
        "sourceKind",
        "postgreSqlType",
        "collation",
        "nullable",
        "binaryCopyType",
    ];

    private static readonly string[] PhysicalOrderDivergences =
    [
        "QueueItems",
        "DavItems",
        "ArrDownloadCorrelations",
        "WorkerJobs",
        "RcloneInvalidationItems",
    ];

    private readonly IReadOnlyList<TransferV3PostgreSqlTableContract> _tables;
    private readonly Dictionary<TransferV3PostgreSqlTableContract, SqlFragments> _fragments;

    private TransferV3PostgreSqlTargetContract(
        IReadOnlyList<TransferV3PostgreSqlTableContract> tables,
        TransferV3PostgreSqlTableContract derivedHealthCheckStats)
    {
        _tables = tables;
        DerivedHealthCheckStats = derivedHealthCheckStats;
        _fragments = new Dictionary<TransferV3PostgreSqlTableContract, SqlFragments>(
            ReferenceEqualityComparer.Instance);

        foreach (var table in tables)
            _fragments.Add(table, BuildFragments(table, isDerived: false));
        _fragments.Add(derivedHealthCheckStats, BuildFragments(derivedHealthCheckStats, isDerived: true));
    }

    internal IReadOnlyList<TransferV3PostgreSqlTableContract> Tables => _tables;

    internal TransferV3PostgreSqlTableContract DerivedHealthCheckStats { get; }

    internal static TransferV3PostgreSqlTargetContract LoadEmbedded()
    {
        var assembly = typeof(TransferV3PostgreSqlTargetContract).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            .ToArray();
        if (resourceNames.Length != 1)
            throw Invalid("The embedded PostgreSQL target contract resource count is invalid.");

        using var stream = assembly.GetManifestResourceStream(resourceNames[0])
                           ?? throw Invalid("The embedded PostgreSQL target contract could not be opened.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return Parse(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
    }

    internal static TransferV3PostgreSqlTargetContract Parse(ReadOnlySpan<byte> utf8Json)
    {
        var snapshot = utf8Json.ToArray();
        try
        {
            using var document = JsonDocument.Parse(snapshot.AsMemory(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            var parsed = ParseRoot(document.RootElement);
            ValidateAgainstReviewedSources(parsed);
            return Freeze(parsed);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Invalid("The PostgreSQL target contract JSON is invalid.", exception);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw Invalid("The PostgreSQL target contract contains an invalid scalar.", exception);
        }
    }

    internal string GetQuotedTableName(TransferV3PostgreSqlTableContract table) =>
        GetOwnedFragments(table).QuotedTableName;

    internal string GetCopyColumnList(TransferV3PostgreSqlTableContract table)
    {
        var fragments = GetOwnedFragments(table);
        if (fragments.IsDerived)
            throw new InvalidOperationException(
                "Derived HealthCheckStats does not have a PostgreSQL COPY column list.");
        return fragments.CopyColumnList;
    }

    internal string GetOrderByList(TransferV3PostgreSqlTableContract table) =>
        GetOwnedFragments(table).OrderByList;

    internal string GetSelectProjection(TransferV3PostgreSqlTableContract table) =>
        GetOwnedFragments(table).SelectProjection;

    private SqlFragments GetOwnedFragments(TransferV3PostgreSqlTableContract table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!_fragments.TryGetValue(table, out var fragments))
            throw new ArgumentException(
                "The table contract is not owned by this PostgreSQL target contract instance.",
                nameof(table));
        return fragments;
    }

    private static ParsedContract ParseRoot(JsonElement root)
    {
        RequireExactProperties(root, RootProperties, "root");
        var formatVersion = ReadInt32(root, "formatVersion");
        var tablesElement = RequireProperty(root, "tables");
        if (tablesElement.ValueKind != JsonValueKind.Array)
            throw Invalid("The PostgreSQL target contract tables value is not an array.");

        var tables = new List<ParsedTable>();
        foreach (var table in tablesElement.EnumerateArray())
            tables.Add(ParseTable(table));

        var derived = ParseTable(RequireProperty(root, "derivedHealthCheckStats"));
        return new ParsedContract(formatVersion, tables, derived);
    }

    private static ParsedTable ParseTable(JsonElement element)
    {
        RequireExactProperties(element, TableProperties, "table");
        var columnsElement = RequireProperty(element, "columns");
        if (columnsElement.ValueKind != JsonValueKind.Array)
            throw Invalid("A PostgreSQL target table columns value is not an array.");
        var columns = new List<ParsedColumn>();
        foreach (var column in columnsElement.EnumerateArray())
            columns.Add(ParseColumn(column));

        var keyColumnsElement = RequireProperty(element, "keyColumns");
        if (keyColumnsElement.ValueKind != JsonValueKind.Array)
            throw Invalid("A PostgreSQL target keyColumns value is not an array.");
        var keyColumns = new List<string>();
        foreach (var keyColumn in keyColumnsElement.EnumerateArray())
            keyColumns.Add(ReadRequiredStringValue(keyColumn, "key column"));

        return new ParsedTable(
            ReadInt32(element, "ordinal"),
            ReadRequiredString(element, "name"),
            columns,
            keyColumns,
            ReadBoolean(element, "preserveBootstrapRoots"),
            ReadBoolean(element, "filtersReservedImportState"));
    }

    private static ParsedColumn ParseColumn(JsonElement element)
    {
        RequireExactProperties(element, ColumnProperties, "column");
        var collationElement = RequireProperty(element, "collation");
        var collation = collationElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => ReadRequiredStringValue(collationElement, "collation"),
            _ => throw Invalid("A PostgreSQL target column collation is neither a string nor null."),
        };

        return new ParsedColumn(
            ReadInt32(element, "ordinal"),
            ReadRequiredString(element, "name"),
            ParseSourceKind(RequireProperty(element, "sourceKind")),
            ReadRequiredString(element, "postgreSqlType"),
            collation,
            ReadBoolean(element, "nullable"),
            ParseBinaryCopyType(RequireProperty(element, "binaryCopyType")));
    }

    private static void RequireExactProperties(
        JsonElement element,
        IReadOnlyList<string> expectedProperties,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid($"The PostgreSQL target contract {description} is not an object.");

        var expected = new HashSet<string>(expectedProperties, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
                throw Invalid($"The PostgreSQL target contract {description} has an unknown property.");
            if (!seen.Add(property.Name))
                throw Invalid($"The PostgreSQL target contract {description} has a duplicate property.");
        }

        if (seen.Count != expected.Count || expected.Any(property => !seen.Contains(property)))
            throw Invalid($"The PostgreSQL target contract {description} is missing a property.");
    }

    private static JsonElement RequireProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            throw Invalid("The PostgreSQL target contract is missing a required property.");
        return property;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        var property = RequireProperty(element, propertyName);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
            throw Invalid("A PostgreSQL target contract ordinal or version is not an integral Int32.");
        return value;
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        var property = RequireProperty(element, propertyName);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid("A PostgreSQL target contract boolean has the wrong JSON type."),
        };
    }

    private static string ReadRequiredString(JsonElement element, string propertyName) =>
        ReadRequiredStringValue(RequireProperty(element, propertyName), propertyName);

    private static string ReadRequiredStringValue(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw Invalid($"A PostgreSQL target contract {description} is not a string.");
        return element.GetString()
               ?? throw Invalid($"A PostgreSQL target contract {description} is null.");
    }

    private static TransferV3ColumnKind ParseSourceKind(JsonElement element)
    {
        var value = ReadRequiredStringValue(element, "source kind");
        return value switch
        {
            "uuid" => TransferV3ColumnKind.Uuid,
            "boolean" => TransferV3ColumnKind.Boolean,
            "enumInt32" => TransferV3ColumnKind.EnumInt32,
            "int32" => TransferV3ColumnKind.Int32,
            "int64" => TransferV3ColumnKind.Int64,
            "text" => TransferV3ColumnKind.Text,
            "localWallTimestamp" => TransferV3ColumnKind.LocalWallTimestamp,
            "instant" => TransferV3ColumnKind.Instant,
            _ => throw Invalid("A PostgreSQL target contract source kind is not reviewed."),
        };
    }

    private static NpgsqlDbType ParseBinaryCopyType(JsonElement element)
    {
        var value = ReadRequiredStringValue(element, "binary COPY type");
        return value switch
        {
            "uuid" => NpgsqlDbType.Uuid,
            "boolean" => NpgsqlDbType.Boolean,
            "integer" => NpgsqlDbType.Integer,
            "bigint" => NpgsqlDbType.Bigint,
            "text" => NpgsqlDbType.Text,
            "timestamp" => NpgsqlDbType.Timestamp,
            _ => throw Invalid("A PostgreSQL target contract binary COPY type is not reviewed."),
        };
    }

    private static void ValidateAgainstReviewedSources(ParsedContract target)
    {
        if (target.FormatVersion != 3)
            throw Invalid("The PostgreSQL target contract format version is not reviewed.");

        var source = TransferV3SourceContract.LoadEmbedded();
        if (target.Tables.Count != 27
            || target.Tables.Sum(table => table.Columns.Count) != 235
            || source.Tables.Count != target.Tables.Count
            || source.DerivedTables.Count != 1)
        {
            throw Invalid("The PostgreSQL target contract table or column count is not reviewed.");
        }

        for (var index = 0; index < target.Tables.Count; index++)
            ValidateSourceTable(target.Tables[index], source.Tables[index], index, isDerived: false);
        ValidateSourceTable(target.DerivedHealthCheckStats, source.DerivedTables[0], 27, isDerived: true);

        ValidateModelAndCatalog(target);
    }

    private static void ValidateSourceTable(
        ParsedTable target,
        TransferV3TableContract source,
        int expectedOrdinal,
        bool isDerived)
    {
        if (target.Ordinal != expectedOrdinal
            || !string.Equals(target.Name, source.Name, StringComparison.Ordinal)
            || target.Columns.Count != source.Columns.Count
            || !target.KeyColumns.SequenceEqual(
                source.Keyset.Select(component => component.Column),
                StringComparer.Ordinal)
            || target.PreserveBootstrapRoots != (!isDerived && source.Name == "DavItems")
            || target.FiltersReservedImportState != (!isDerived && source.Name == "ConfigItems"))
        {
            throw Invalid("A PostgreSQL target table differs from the reviewed source contract.");
        }

        RequireSafeIdentifier(target.Name);
        if (target.KeyColumns.Count == 0
            || target.KeyColumns.Distinct(StringComparer.Ordinal).Count() != target.KeyColumns.Count)
        {
            throw Invalid("A PostgreSQL target table key is empty or duplicated.");
        }

        for (var index = 0; index < target.Columns.Count; index++)
        {
            var targetColumn = target.Columns[index];
            var sourceColumn = source.Columns[index];
            if (targetColumn.Ordinal != index
                || !string.Equals(targetColumn.Name, sourceColumn.Name, StringComparison.Ordinal)
                || targetColumn.SourceKind != sourceColumn.Kind
                || targetColumn.Nullable != sourceColumn.Nullable)
            {
                throw Invalid("A PostgreSQL target column differs from the reviewed source contract.");
            }
            RequireSafeIdentifier(targetColumn.Name);
        }

        if (target.Columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).Count()
            != target.Columns.Count
            || target.KeyColumns.Any(key =>
                target.Columns.All(column => !string.Equals(column.Name, key, StringComparison.Ordinal))))
        {
            throw Invalid("A PostgreSQL target table has duplicate columns or an unknown key column.");
        }
    }

    private static void ValidateModelAndCatalog(ParsedContract target)
    {
        using var context = CreateModelContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var entities = model.GetEntityTypes().ToArray();
        var catalog = ReadCatalogColumns();
        var allTables = target.Tables.Append(target.DerivedHealthCheckStats).ToArray();

        if (entities.Select(entity => entity.GetTableName()).Distinct(StringComparer.Ordinal).Count() != 28
            || entities.Sum(entity => entity.GetProperties().Count()) != 240
            || catalog.Count != 242
            || catalog.Values.Count(column =>
                column.Table == DatabaseMigrationPolicy.PostgreSqlHistoryTableName) != 2)
        {
            throw Invalid("The PostgreSQL EF model or physical catalog count is not reviewed.");
        }

        var mappedKeys = new HashSet<(string Table, string Column)>();
        var efDefaultKeys = new List<(string Table, string Column)>();
        var catalogDefaultKeys = new List<(string Table, string Column)>();

        foreach (var table in allTables)
        {
            var entity = entities.SingleOrDefault(entity =>
                string.Equals(entity.GetTableName(), table.Name, StringComparison.Ordinal))
                         ?? throw Invalid("A PostgreSQL target table is absent from the EF model.");
            var storeObject = StoreObjectIdentifier.Table(table.Name, entity.GetSchema());

            foreach (var column in table.Columns)
            {
                var property = entity.GetProperties().SingleOrDefault(property =>
                    string.Equals(property.GetColumnName(storeObject), column.Name, StringComparison.Ordinal))
                               ?? throw Invalid("A PostgreSQL target column is absent from the EF model.");
                var key = (table.Name, column.Name);
                if (!mappedKeys.Add(key) || !catalog.TryGetValue(key, out var physical))
                    throw Invalid("A PostgreSQL target column is duplicated or absent from the catalog.");

                var expectedCollation = column.Collation == "C"
                    ? "pg_catalog.C"
                    : column.SourceKind == TransferV3ColumnKind.Text
                        ? "pg_catalog.default"
                        : string.Empty;
                if (!string.Equals(property.GetColumnType(), column.PostgreSqlType, StringComparison.Ordinal)
                    || !string.Equals(physical.PostgreSqlType, column.PostgreSqlType, StringComparison.Ordinal)
                    || property.IsNullable != column.Nullable
                    || physical.NotNull == column.Nullable
                    || !string.Equals(property.GetCollation(), column.Collation, StringComparison.Ordinal)
                    || !string.Equals(physical.Collation, expectedCollation, StringComparison.Ordinal)
                    || physical.Dropped
                    || physical.Identity.Length != 0
                    || physical.Generated.Length != 0
                    || property.GetComputedColumnSql() is not null
                    || !IsReviewedKindMapping(column))
                {
                    throw Invalid("A PostgreSQL target mapping differs from the EF model or catalog.");
                }

                var hasEfDefault = property.FindAnnotation(RelationalAnnotationNames.DefaultValue) is not null
                                   || property.GetDefaultValueSql() is not null;
                if (hasEfDefault)
                    efDefaultKeys.Add(key);
                if (physical.HasDefault)
                    catalogDefaultKeys.Add(key);

                var isLeaseGeneration = table.Name == "WorkerJobs"
                                        && column.Name == "LeaseGeneration";
                if (isLeaseGeneration)
                {
                    var defaultValue = property.FindAnnotation(RelationalAnnotationNames.DefaultValue)?.Value;
                    if (!hasEfDefault
                        || !physical.HasDefault
                        || !string.Equals(physical.DefaultExpression, "0", StringComparison.Ordinal)
                        || defaultValue is null
                        || Convert.ToInt64(defaultValue, CultureInfo.InvariantCulture) != 0L
                        || property.GetDefaultValueSql() is not null
                        || property.ValueGenerated != ValueGenerated.OnAdd)
                    {
                        throw Invalid("WorkerJobs.LeaseGeneration does not have the reviewed default mapping.");
                    }
                }
                else if (hasEfDefault
                         || physical.HasDefault
                         || property.ValueGenerated != ValueGenerated.Never)
                {
                    throw Invalid("An unreviewed PostgreSQL generated or defaulted column exists.");
                }
            }
        }

        var expectedLeaseKey = (Table: "WorkerJobs", Column: "LeaseGeneration");
        if (mappedKeys.Count != 240
            || !efDefaultKeys.SequenceEqual([expectedLeaseKey])
            || !catalogDefaultKeys.SequenceEqual([expectedLeaseKey])
            || catalog.Values.Any(column =>
                column.Table != DatabaseMigrationPolicy.PostgreSqlHistoryTableName
                && !mappedKeys.Contains((column.Table, column.Name))))
        {
            throw Invalid("The PostgreSQL target mapping does not cover the exact reviewed model and catalog.");
        }

        ValidateCollationAndOrderEvidence(allTables, catalog);
    }

    private static void ValidateCollationAndOrderEvidence(
        IReadOnlyList<ParsedTable> tables,
        IReadOnlyDictionary<(string Table, string Column), PhysicalColumn> catalog)
    {
        var columns = tables.SelectMany(table => table.Columns).ToArray();
        if (columns.Count(column => column.Collation == "C") != 20
            || columns.Count(column =>
                column.SourceKind == TransferV3ColumnKind.Text && column.Collation is null) != 59
            || columns.Count(column =>
                column.SourceKind != TransferV3ColumnKind.Text && column.Collation is null) != 161)
        {
            throw Invalid("The PostgreSQL target collation inventory is not reviewed.");
        }

        var collatedKeys = tables
            .SelectMany(table => table.KeyColumns.Select(key =>
                (table.Name, Column: table.Columns.Single(column => column.Name == key))))
            .Where(value => value.Column.Collation == "C")
            .Select(value => $"{value.Name}.{value.Column.Name}")
            .ToArray();
        if (!collatedKeys.SequenceEqual(["Accounts.Username", "ConfigItems.ConfigName"],
                StringComparer.Ordinal))
        {
            throw Invalid("The PostgreSQL target key-collation inventory is not reviewed.");
        }

        var divergences = tables
            .Where(table => !table.Columns.Select(column => column.Name).SequenceEqual(
                catalog.Values.Where(column => column.Table == table.Name)
                    .OrderBy(column => column.Attnum)
                    .Select(column => column.Name),
                StringComparer.Ordinal))
            .Select(table => table.Name)
            .ToArray();
        if (!divergences.SequenceEqual(PhysicalOrderDivergences, StringComparer.Ordinal))
            throw Invalid("The PostgreSQL physical-attnum divergence inventory is not reviewed.");
    }

    private static bool IsReviewedKindMapping(ParsedColumn column)
    {
        var expectedCopyType = column.SourceKind switch
        {
            TransferV3ColumnKind.Uuid => NpgsqlDbType.Uuid,
            TransferV3ColumnKind.Boolean => NpgsqlDbType.Boolean,
            TransferV3ColumnKind.EnumInt32 or TransferV3ColumnKind.Int32 => NpgsqlDbType.Integer,
            TransferV3ColumnKind.Int64 or TransferV3ColumnKind.Instant => NpgsqlDbType.Bigint,
            TransferV3ColumnKind.Text => NpgsqlDbType.Text,
            TransferV3ColumnKind.LocalWallTimestamp => NpgsqlDbType.Timestamp,
            _ => throw Invalid("The PostgreSQL target source kind is not reviewed."),
        };
        if (column.BinaryCopyType != expectedCopyType)
            return false;

        return column.SourceKind switch
        {
            TransferV3ColumnKind.Uuid => column.PostgreSqlType == "uuid",
            TransferV3ColumnKind.Boolean => column.PostgreSqlType == "boolean",
            TransferV3ColumnKind.EnumInt32 or TransferV3ColumnKind.Int32 =>
                column.PostgreSqlType == "integer",
            TransferV3ColumnKind.Int64 or TransferV3ColumnKind.Instant =>
                column.PostgreSqlType == "bigint",
            TransferV3ColumnKind.Text => IsReviewedTextType(column.PostgreSqlType),
            TransferV3ColumnKind.LocalWallTimestamp =>
                column.PostgreSqlType == "timestamp without time zone",
            _ => false,
        };
    }

    private static bool IsReviewedTextType(string value)
    {
        if (value == "text")
            return true;
        const string prefix = "character varying(";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value[^1] != ')')
            return false;
        var digits = value.AsSpan(prefix.Length, value.Length - prefix.Length - 1);
        return digits.Length > 0
               && digits[0] != '0'
               && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
               && length > 0;
    }

    private static Dictionary<(string Table, string Column), PhysicalColumn> ReadCatalogColumns()
    {
        var result = new Dictionary<(string Table, string Column), PhysicalColumn>();
        var inventory = PostgreSqlPhysicalCatalogContract.ReadExpectedInventory(PostgreSqlCatalogState.Head);
        foreach (var line in inventory.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("column|", StringComparison.Ordinal))
                continue;
            var fields = line.Split('|');
            if (fields.Length != 23
                || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var attnum)
                || !bool.TryParse(fields[4], out var dropped)
                || !bool.TryParse(fields[8], out var notNull)
                || !bool.TryParse(fields[9], out var hasDefault))
            {
                throw Invalid("The reviewed PostgreSQL physical catalog column shape is invalid.");
            }

            var column = new PhysicalColumn(
                fields[1],
                attnum,
                fields[3],
                dropped,
                fields[5],
                notNull,
                hasDefault,
                fields[12],
                fields[13],
                fields[14],
                fields[21]);
            if (!result.TryAdd((column.Table, column.Name), column))
                throw Invalid("The reviewed PostgreSQL physical catalog has a duplicate column.");
        }
        return result;
    }

    private static PostgreSqlDavDatabaseContext CreateModelContext()
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Database = "nzbdav-contract-validation",
            Username = "nzbdav-contract-validation",
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

    private static TransferV3PostgreSqlTargetContract Freeze(ParsedContract source)
    {
        var tables = source.Tables.Select(FreezeTable).ToArray();
        var ownedTables = new ReadOnlyCollection<TransferV3PostgreSqlTableContract>(tables);
        return new TransferV3PostgreSqlTargetContract(
            ownedTables,
            FreezeTable(source.DerivedHealthCheckStats));
    }

    private static TransferV3PostgreSqlTableContract FreezeTable(ParsedTable source)
    {
        var columns = source.Columns.Select(column => new TransferV3PostgreSqlColumnContract(
            column.Ordinal,
            column.Name,
            column.SourceKind,
            column.PostgreSqlType,
            column.Collation,
            column.Nullable,
            column.BinaryCopyType)).ToArray();
        var keyColumns = source.KeyColumns.ToArray();
        return new TransferV3PostgreSqlTableContract(
            source.Ordinal,
            source.Name,
            new ReadOnlyCollection<TransferV3PostgreSqlColumnContract>(columns),
            new ReadOnlyCollection<string>(keyColumns),
            source.PreserveBootstrapRoots,
            source.FiltersReservedImportState);
    }

    private static SqlFragments BuildFragments(
        TransferV3PostgreSqlTableContract table,
        bool isDerived)
    {
        var columnsByName = table.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var copy = string.Join(", ", table.Columns.Select(column => Quote(column.Name)));
        var order = string.Join(", ", table.KeyColumns.Select(key =>
        {
            var column = columnsByName[key];
            return Quote(key) + (column.Collation == "C" ? " COLLATE pg_catalog.\"C\"" : string.Empty);
        }));
        var projection = string.Join(", ", table.Columns.SelectMany(column =>
            column.SourceKind == TransferV3ColumnKind.Text
                ? new[] { $"pg_catalog.octet_length({Quote(column.Name)})", Quote(column.Name) }
                : new[] { Quote(column.Name) }));
        return new SqlFragments(Quote(table.Name), copy, order, projection, isDerived);
    }

    private static string Quote(string identifier) => $"\"{identifier}\"";

    private static void RequireSafeIdentifier(string identifier)
    {
        if (identifier.Length is < 1 or > 63
            || !IsAsciiIdentifierStart(identifier[0])
            || identifier.Skip(1).Any(character => !IsAsciiIdentifierContinuation(character)))
        {
            throw Invalid("A PostgreSQL target identifier is not reviewed-safe ASCII.");
        }
    }

    private static bool IsAsciiIdentifierStart(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsAsciiIdentifierContinuation(char value) =>
        IsAsciiIdentifierStart(value) || value is >= '0' and <= '9';

    private static InvalidDataException Invalid(string message, Exception? innerException = null) =>
        new(message, innerException);

    private sealed record ParsedContract(
        int FormatVersion,
        IReadOnlyList<ParsedTable> Tables,
        ParsedTable DerivedHealthCheckStats);

    private sealed record ParsedTable(
        int Ordinal,
        string Name,
        IReadOnlyList<ParsedColumn> Columns,
        IReadOnlyList<string> KeyColumns,
        bool PreserveBootstrapRoots,
        bool FiltersReservedImportState);

    private sealed record ParsedColumn(
        int Ordinal,
        string Name,
        TransferV3ColumnKind SourceKind,
        string PostgreSqlType,
        string? Collation,
        bool Nullable,
        NpgsqlDbType BinaryCopyType);

    private sealed record PhysicalColumn(
        string Table,
        int Attnum,
        string Name,
        bool Dropped,
        string PostgreSqlType,
        bool NotNull,
        bool HasDefault,
        string Identity,
        string Generated,
        string Collation,
        string DefaultExpression);

    private sealed record SqlFragments(
        string QuotedTableName,
        string CopyColumnList,
        string OrderByList,
        string SelectProjection,
        bool IsDerived);
}
