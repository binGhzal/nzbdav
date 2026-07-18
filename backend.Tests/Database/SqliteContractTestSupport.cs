using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;

namespace backend.Tests.Database;

internal static partial class SqliteContractTestSupport
{
    internal const string ContextName = "NzbWebDAV.Database.DavDatabaseContext";
    internal const string HistoryTableName = "__EFMigrationsHistory";
    internal const string MigrationLockTableName = "__EFMigrationsLock";
    internal const string LatestMigrationId = "20260712123000_Add-Arr-Import-Commands";
    internal const string SnapshotRelativePath =
        "backend/Database/Migrations/DavDatabaseContextModelSnapshot.cs";
    internal const string SnapshotTypeName =
        "NzbWebDAV.Database.Migrations.DavDatabaseContextModelSnapshot";
    internal const string MigrationContractRelativePath =
        "backend.Tests/TestData/sqlite-migration-contract.json";
    internal const string SchemaManifestRelativePath =
        "backend.Tests/TestData/sqlite-source-schema-manifest.json";

    private const string UpSignature = "protected override void Up(MigrationBuilder migrationBuilder)";
    private const string BuildTargetModelSignature =
        "protected override void BuildTargetModel(ModelBuilder modelBuilder)";
    private const string BuildModelSignature = "protected override void BuildModel(ModelBuilder modelBuilder)";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "backend", "NzbWebDAV.csproj"))
                    && Directory.Exists(Path.Combine(
                        directory.FullName,
                        "backend",
                        "Database",
                        "Migrations")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException(
                $"Could not find the NZBDav repository root above '{AppContext.BaseDirectory}'.");
        }
    }

    internal static string AbsolutePath(string repositoryRelativePath) =>
        Path.Combine(RepositoryRoot, repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar));

    internal static SqliteMigrationContract CaptureMigrationContract()
    {
        var migrationsDirectory = AbsolutePath("backend/Database/Migrations");
        var migrations = Directory
            .EnumerateFiles(migrationsDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.Ordinal)
                           && PrimaryMigrationFileNameRegex().IsMatch(Path.GetFileName(path)))
            .OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal)
            .Select(CaptureMigrationSource)
            .ToArray();

        var snapshotPath = AbsolutePath(SnapshotRelativePath);
        return new SqliteMigrationContract(
            FormatVersion: 1,
            Context: ContextName,
            HistoryTable: HistoryTableName,
            Snapshot: new SqliteSnapshotContract(
                Path: SnapshotRelativePath,
                Type: SnapshotTypeName,
                Context: ContextName,
                BuildModelSha256: HashSourceSegment(snapshotPath, BuildModelSignature)),
            Migrations: migrations);
    }

    internal static byte[] SerializeCanonical<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions) + "\n");

    internal static T ReadFixture<T>(string repositoryRelativePath)
    {
        var path = AbsolutePath(repositoryRelativePath);
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonOptions)
               ?? throw new InvalidDataException($"Fixture '{repositoryRelativePath}' deserialized to null.");
    }

    internal static void WriteMissingFixtureDiagnostic(string fileName, byte[] canonicalBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllBytes(path, canonicalBytes);
        Assert.Fail($"The reviewed fixture is missing. Candidate output was written to '{path}'.");
    }

    internal static async Task<SqliteSourceSchemaManifest> CaptureSchemaManifestAsync()
    {
        await using var database = new SqliteContractDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        return await CaptureSchemaManifestAsync(context);
    }

    internal static async Task<SqliteSourceSchemaManifest> CaptureSchemaManifestAsync(
        DavDatabaseContext context)
    {
        if (context.Database.GetDbConnection().State != ConnectionState.Open)
            await context.Database.OpenConnectionAsync();

        var appliedMigrations = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        var schemaRows = await ReadSqliteSchemaAsync(context.Database.GetDbConnection());
        var allTableNames = schemaRows
            .Where(row => row.Type == "table")
            .Select(row => row.TableName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var applicationTableNames = allTableNames
            .Where(name => name != HistoryTableName
                           && name != MigrationLockTableName
                           && !name.StartsWith("sqlite_", StringComparison.Ordinal))
            .ToArray();

        var tableXInfo = new List<SqliteTableXInfoCapture>(allTableNames.Length);
        var indexList = new List<SqliteIndexListCapture>(allTableNames.Length);
        var foreignKeyList = new List<SqliteForeignKeyListCapture>(allTableNames.Length);
        var indexXInfo = new List<SqliteIndexXInfoCapture>();
        var logicalTables = new List<SqliteLogicalTable>(applicationTableNames.Length);

        foreach (var tableName in allTableNames)
        {
            var columns = await ReadTableXInfoAsync(context.Database.GetDbConnection(), tableName);
            var indexes = await ReadIndexListAsync(context.Database.GetDbConnection(), tableName);
            var foreignKeys = await ReadForeignKeyListAsync(context.Database.GetDbConnection(), tableName);
            tableXInfo.Add(new SqliteTableXInfoCapture(tableName, columns));
            indexList.Add(new SqliteIndexListCapture(tableName, indexes));
            foreignKeyList.Add(new SqliteForeignKeyListCapture(tableName, foreignKeys));

            var logicalIndexes = new List<SqliteLogicalIndex>(indexes.Count);
            foreach (var index in indexes.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                var indexColumns = await ReadIndexXInfoAsync(context.Database.GetDbConnection(), index.Name);
                indexXInfo.Add(new SqliteIndexXInfoCapture(index.Name, indexColumns));
                logicalIndexes.Add(new SqliteLogicalIndex(
                    index.Name,
                    index.Unique,
                    index.Origin,
                    index.Partial,
                    indexColumns.Where(column => column.IsKey).ToArray()));
            }

            if (!applicationTableNames.Contains(tableName, StringComparer.Ordinal))
                continue;

            var tableSchema = schemaRows.Single(row => row.Type == "table" && row.Name == tableName);
            logicalTables.Add(new SqliteLogicalTable(
                Name: tableName,
                CreateSql: tableSchema.Sql,
                WithoutRowId: tableSchema.Sql.Value?.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase)
                              == true,
                Strict: tableSchema.Sql.Value?.Contains(" STRICT", StringComparison.OrdinalIgnoreCase) == true,
                Columns: columns.Select(column => new SqliteLogicalColumn(
                    Name: column.Name,
                    DeclaredType: column.DeclaredType,
                    Affinity: GetSqliteAffinity(column.DeclaredType),
                    Nullable: !column.NotNull,
                    DefaultValue: column.DefaultValue,
                    PrimaryKeyOrdinal: column.PrimaryKeyOrdinal,
                    Hidden: column.Hidden)).ToArray(),
                PrimaryKey: columns
                    .Where(column => column.PrimaryKeyOrdinal > 0)
                    .OrderBy(column => column.PrimaryKeyOrdinal)
                    .Select(column => column.Name)
                    .ToArray(),
                Indexes: logicalIndexes,
                ForeignKeys: foreignKeys));
        }

        indexXInfo.Sort((left, right) => StringComparer.Ordinal.Compare(left.Index, right.Index));
        var knownObjectTypes = new HashSet<string>(["index", "table", "trigger", "view"], StringComparer.Ordinal);
        var unrecognizedObjects = schemaRows
            .Where(row => !knownObjectTypes.Contains(row.Type))
            .ToArray();

        return new SqliteSourceSchemaManifest(
            FormatVersion: 1,
            Provider: "Microsoft.EntityFrameworkCore.Sqlite",
            Context: ContextName,
            HistoryTable: HistoryTableName,
            AppliedMigrations: appliedMigrations,
            LogicalTables: logicalTables.OrderBy(table => table.Name, StringComparer.Ordinal).ToArray(),
            Physical: new SqlitePhysicalSchema(
                SqliteSchema: schemaRows,
                TableXInfo: tableXInfo,
                IndexList: indexList,
                IndexXInfo: indexXInfo,
                ForeignKeyList: foreignKeyList,
                TriggerNames: schemaRows.Where(row => row.Type == "trigger")
                    .Select(row => row.Name).Order(StringComparer.Ordinal).ToArray(),
                ViewNames: schemaRows.Where(row => row.Type == "view")
                    .Select(row => row.Name).Order(StringComparer.Ordinal).ToArray(),
                UnrecognizedObjects: unrecognizedObjects));
    }

    private static SqliteMigrationSourceContract CaptureMigrationSource(string sourcePath)
    {
        var fileName = Path.GetFileName(sourcePath);
        var id = Path.GetFileNameWithoutExtension(sourcePath);
        var designerPath = Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            Path.GetFileNameWithoutExtension(sourcePath) + ".Designer.cs");
        var hasDesigner = File.Exists(designerPath);
        var source = NormalizeSource(File.ReadAllText(sourcePath, Encoding.UTF8));
        var classMatch = MigrationClassRegex().Match(source);
        if (!classMatch.Success)
            throw new InvalidDataException($"Migration class was not found in '{fileName}'.");

        var sourceRelativePath = $"backend/Database/Migrations/{fileName}";
        var metadataRelativePath = hasDesigner
            ? $"backend/Database/Migrations/{Path.GetFileName(designerPath)}"
            : sourceRelativePath;

        return new SqliteMigrationSourceContract(
            Id: id,
            ClassName: classMatch.Groups[1].Value,
            SourcePath: sourceRelativePath,
            MetadataLayout: hasDesigner ? "designer" : "inline",
            MetadataPath: metadataRelativePath,
            UpThroughEofSha256: HashSourceSegment(sourcePath, UpSignature),
            BuildTargetModelSha256: hasDesigner
                ? HashSourceSegment(designerPath, BuildTargetModelSignature)
                : null);
    }

    private static string HashSourceSegment(string path, string signature)
    {
        var source = NormalizeSource(File.ReadAllText(path, Encoding.UTF8));
        var first = source.IndexOf(signature, StringComparison.Ordinal);
        if (first < 0)
            throw new InvalidDataException($"'{signature}' was not found in '{path}'.");
        if (source.IndexOf(signature, first + signature.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException($"'{signature}' appeared more than once in '{path}'.");

        var segment = NormalizeSource(source[first..]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(segment))).ToLowerInvariant();
    }

    private static string NormalizeSource(string source)
    {
        if (source.Length > 0 && source[0] == '\uFEFF')
            source = source[1..];
        source = source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return source.TrimEnd('\n') + "\n";
    }

    private static async Task<IReadOnlyList<SqliteSchemaRow>> ReadSqliteSchemaAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT type, name, tbl_name, sql
            FROM sqlite_schema
            ORDER BY type, name, tbl_name;
            """;

        var rows = new List<SqliteSchemaRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SqliteSchemaRow(
                Type: reader.GetString(0),
                Name: reader.GetString(1),
                TableName: reader.GetString(2),
                Sql: ReadSqlText(reader, 3)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<SqliteTableXInfoRow>> ReadTableXInfoAsync(
        DbConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_xinfo({QuoteSqliteString(tableName)});";
        var rows = new List<SqliteTableXInfoRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SqliteTableXInfoRow(
                Cid: reader.GetInt32(0),
                Name: reader.GetString(1),
                DeclaredType: reader.GetString(2),
                NotNull: reader.GetInt32(3) != 0,
                DefaultValue: ReadSqlText(reader, 4),
                PrimaryKeyOrdinal: reader.GetInt32(5),
                Hidden: reader.GetInt32(6)));
        }

        return rows.OrderBy(row => row.Cid).ToArray();
    }

    private static async Task<IReadOnlyList<SqliteIndexListRow>> ReadIndexListAsync(
        DbConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list({QuoteSqliteString(tableName)});";
        var rows = new List<SqliteIndexListRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SqliteIndexListRow(
                Sequence: reader.GetInt32(0),
                Name: reader.GetString(1),
                Unique: reader.GetInt32(2) != 0,
                Origin: reader.GetString(3),
                Partial: reader.GetInt32(4) != 0));
        }

        return rows.OrderBy(row => row.Sequence).ThenBy(row => row.Name, StringComparer.Ordinal).ToArray();
    }

    private static async Task<IReadOnlyList<SqliteIndexXInfoRow>> ReadIndexXInfoAsync(
        DbConnection connection,
        string indexName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_xinfo({QuoteSqliteString(indexName)});";
        var rows = new List<SqliteIndexXInfoRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SqliteIndexXInfoRow(
                Sequence: reader.GetInt32(0),
                Cid: reader.GetInt32(1),
                Name: reader.IsDBNull(2) ? null : reader.GetString(2),
                Descending: reader.GetInt32(3) != 0,
                Collation: reader.GetString(4),
                IsKey: reader.GetInt32(5) != 0));
        }

        return rows.OrderBy(row => row.Sequence).ToArray();
    }

    private static async Task<IReadOnlyList<SqliteForeignKeyRow>> ReadForeignKeyListAsync(
        DbConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({QuoteSqliteString(tableName)});";
        var rows = new List<SqliteForeignKeyRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SqliteForeignKeyRow(
                Id: reader.GetInt32(0),
                Sequence: reader.GetInt32(1),
                PrincipalTable: reader.GetString(2),
                FromColumn: reader.GetString(3),
                ToColumn: reader.IsDBNull(4) ? null : reader.GetString(4),
                OnUpdate: reader.GetString(5),
                OnDelete: reader.GetString(6),
                Match: reader.GetString(7)));
        }

        return rows.OrderBy(row => row.Id).ThenBy(row => row.Sequence).ToArray();
    }

    private static SqlText ReadSqlText(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? new SqlText("database-null", null)
            : new SqlText("sql", reader.GetString(ordinal));

    private static string QuoteSqliteString(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string GetSqliteAffinity(string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal)) return "INTEGER";
        if (type.Contains("CHAR", StringComparison.Ordinal)
            || type.Contains("CLOB", StringComparison.Ordinal)
            || type.Contains("TEXT", StringComparison.Ordinal)) return "TEXT";
        if (type.Contains("BLOB", StringComparison.Ordinal) || type.Length == 0) return "BLOB";
        if (type.Contains("REAL", StringComparison.Ordinal)
            || type.Contains("FLOA", StringComparison.Ordinal)
            || type.Contains("DOUB", StringComparison.Ordinal)) return "REAL";
        return "NUMERIC";
    }

    [GeneratedRegex(@"^\d{14}_.+\.cs$", RegexOptions.CultureInvariant)]
    private static partial Regex PrimaryMigrationFileNameRegex();

    [GeneratedRegex(@"\bpartial\s+class\s+(\w+)\s*:\s*Migration\b", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationClassRegex();
}

internal sealed class SqliteContractDatabase : IDisposable, IAsyncDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"nzbdav-sqlite-contract-{Guid.NewGuid():N}");

    internal SqliteContractDatabase()
    {
        Directory.CreateDirectory(_directory);
    }

    internal DavDatabaseContext CreateContext()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "contract.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<Microsoft.EntityFrameworkCore.Migrations.SqliteMigrationsSqlGenerator>>()
            .Options;
        return new DavDatabaseContext(options);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record SqliteMigrationContract(
    int FormatVersion,
    string Context,
    string HistoryTable,
    SqliteSnapshotContract Snapshot,
    IReadOnlyList<SqliteMigrationSourceContract> Migrations);

internal sealed record SqliteSnapshotContract(
    string Path,
    string Type,
    string Context,
    string BuildModelSha256);

internal sealed record SqliteMigrationSourceContract(
    string Id,
    string ClassName,
    string SourcePath,
    string MetadataLayout,
    string MetadataPath,
    string UpThroughEofSha256,
    string? BuildTargetModelSha256);

internal sealed record SqliteSourceSchemaManifest(
    int FormatVersion,
    string Provider,
    string Context,
    string HistoryTable,
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<SqliteLogicalTable> LogicalTables,
    SqlitePhysicalSchema Physical);

internal sealed record SqliteLogicalTable(
    string Name,
    SqlText CreateSql,
    bool WithoutRowId,
    bool Strict,
    IReadOnlyList<SqliteLogicalColumn> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<SqliteLogicalIndex> Indexes,
    IReadOnlyList<SqliteForeignKeyRow> ForeignKeys);

internal sealed record SqliteLogicalColumn(
    string Name,
    string DeclaredType,
    string Affinity,
    bool Nullable,
    SqlText DefaultValue,
    int PrimaryKeyOrdinal,
    int Hidden);

internal sealed record SqliteLogicalIndex(
    string Name,
    bool Unique,
    string Origin,
    bool Partial,
    IReadOnlyList<SqliteIndexXInfoRow> Columns);

internal sealed record SqlitePhysicalSchema(
    IReadOnlyList<SqliteSchemaRow> SqliteSchema,
    IReadOnlyList<SqliteTableXInfoCapture> TableXInfo,
    IReadOnlyList<SqliteIndexListCapture> IndexList,
    IReadOnlyList<SqliteIndexXInfoCapture> IndexXInfo,
    IReadOnlyList<SqliteForeignKeyListCapture> ForeignKeyList,
    IReadOnlyList<string> TriggerNames,
    IReadOnlyList<string> ViewNames,
    IReadOnlyList<SqliteSchemaRow> UnrecognizedObjects);

internal sealed record SqliteSchemaRow(string Type, string Name, string TableName, SqlText Sql);
internal sealed record SqlText(string Kind, string? Value);
internal sealed record SqliteTableXInfoCapture(string Table, IReadOnlyList<SqliteTableXInfoRow> Rows);
internal sealed record SqliteIndexListCapture(string Table, IReadOnlyList<SqliteIndexListRow> Rows);
internal sealed record SqliteIndexXInfoCapture(string Index, IReadOnlyList<SqliteIndexXInfoRow> Rows);
internal sealed record SqliteForeignKeyListCapture(string Table, IReadOnlyList<SqliteForeignKeyRow> Rows);
internal sealed record SqliteTableXInfoRow(
    int Cid,
    string Name,
    string DeclaredType,
    bool NotNull,
    SqlText DefaultValue,
    int PrimaryKeyOrdinal,
    int Hidden);
internal sealed record SqliteIndexListRow(int Sequence, string Name, bool Unique, string Origin, bool Partial);
internal sealed record SqliteIndexXInfoRow(
    int Sequence,
    int Cid,
    string? Name,
    bool Descending,
    string Collation,
    bool IsKey);
internal sealed record SqliteForeignKeyRow(
    int Id,
    int Sequence,
    string PrincipalTable,
    string FromColumn,
    string? ToColumn,
    string OnUpdate,
    string OnDelete,
    string Match);
