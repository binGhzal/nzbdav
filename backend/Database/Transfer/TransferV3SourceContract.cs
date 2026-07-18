using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NzbWebDAV.Database.Transfer;

internal enum TransferV3ColumnKind
{
    Uuid,
    Boolean,
    EnumInt32,
    Int32,
    Int64,
    Text,
    LocalWallTimestamp,
    Instant,
}

internal enum TransferV3InstantEncoding
{
    None,
    UtcTicks,
    UnixSeconds,
}

internal enum TransferV3UuidRole
{
    None,
    Identity,
    Reference,
    OpaqueToken,
    Tombstone,
    PolymorphicReference,
}

internal enum TransferV3ReferencePolicy
{
    DeclaredForeignKeyHard,
    ApplicationHard,
    StateAwareHard,
    BlobHard,
    BlobAndNameHard,
    InformationalDigest,
    CleanupTombstone,
    ConditionalCleanupTombstone,
    PolymorphicInformationalDigest,
}

internal sealed record TransferV3SourceContract(
    int FormatVersion,
    string Provider,
    string Context,
    string HistoryTable,
    int SourceModeledTableCount,
    int SourceModeledColumnCount,
    IReadOnlyList<string> DerivedExcludedTables,
    IReadOnlyList<string> ExcludedConfigKeys,
    string SourceSchemaSha256,
    string MigrationSourceContractSha256,
    IReadOnlyList<TransferV3MigrationContract> Migrations,
    IReadOnlyList<TransferV3TableContract> Tables,
    IReadOnlyList<TransferV3TableContract> DerivedTables,
    TransferV3BootstrapContract Bootstrap,
    TransferV3BlobContract Blobs)
{
    internal const string ContractResourceSuffix =
        ".Database.Transfer.Contracts.transfer-v3-source-contract.json";
    internal const string SchemaResourceSuffix =
        ".Database.Transfer.Contracts.sqlite-source-schema-manifest.json";
    internal const string MigrationResourceSuffix =
        ".Database.Transfer.Contracts.sqlite-migration-contract.json";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal static TransferV3SourceContract LoadEmbedded()
    {
        var bytes = ReadEmbedded(ContractResourceSuffix);
        var contract = JsonSerializer.Deserialize<TransferV3SourceContract>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("The embedded Transfer-v3 source contract was empty.");
        contract.ValidateShape();
        return contract;
    }

    internal static byte[] ReadEmbeddedSourceSchema() => ReadEmbedded(SchemaResourceSuffix);

    internal static byte[] ReadEmbeddedMigrationSourceContract() => ReadEmbedded(MigrationResourceSuffix);

    internal string ComputeSha256()
    {
        var bytes = ReadEmbedded(ContractResourceSuffix);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private void ValidateShape()
    {
        if (FormatVersion != 3
            || Provider != "Microsoft.EntityFrameworkCore.Sqlite"
            || Context != "NzbWebDAV.Database.DavDatabaseContext"
            || HistoryTable != "__EFMigrationsHistory"
            || SourceModeledTableCount != 28
            || SourceModeledColumnCount != 240
            || Migrations.Count != 49
            || Tables.Count != 27
            || Tables.Sum(table => table.Columns.Count) != 235
            || DerivedTables.Count != 1
            || DerivedTables.Sum(table => table.Columns.Count) != 5
            || Tables.Count + DerivedTables.Count != SourceModeledTableCount
            || Tables.Sum(table => table.Columns.Count)
               + DerivedTables.Sum(table => table.Columns.Count) != SourceModeledColumnCount
            || Tables.Sum(table => table.Columns.Count(column => column.Kind == TransferV3ColumnKind.Uuid)) != 46)
        {
            throw new InvalidDataException("The embedded Transfer-v3 source contract shape is not the reviewed schema.");
        }

        RequireUnique(Migrations.Select(migration => migration.Id), "migration IDs");
        RequireUnique(Tables.Select(table => table.Name), "table names");
        RequireUnique(DerivedTables.Select(table => table.Name), "derived table names");
        foreach (var migration in Migrations)
        {
            if (migration.AllowedProductVersions.Count == 0)
                throw new InvalidDataException("A migration has no reviewed ProductVersion allowlist.");
        }

        foreach (var table in Tables.Concat(DerivedTables))
        {
            if (table.Keyset.Count == 0)
                throw new InvalidDataException("A transferred table has no stable keyset.");
            RequireUnique(table.Columns.Select(column => column.Name), $"columns for {table.Name}");
            foreach (var component in table.Keyset)
            {
                if (!table.Columns.Any(column => column.Name == component.Column))
                    throw new InvalidDataException("A table keyset references an unknown column.");
            }
            foreach (var unique in table.UniqueKeys)
            {
                if (!unique.Columns.SequenceEqual(unique.Components.Select(component => component.Column), StringComparer.Ordinal))
                    throw new InvalidDataException("A unique key is missing its provider-neutral component contract.");
            }
            foreach (var reference in table.References)
            {
                var polymorphic = reference.Policy == TransferV3ReferencePolicy.PolymorphicInformationalDigest;
                if (polymorphic != (reference.DiscriminatorColumn is not null
                                    && reference.PolymorphicCases is { Count: > 0 }))
                    throw new InvalidDataException("A polymorphic reference is missing its discriminator mapping.");
                if (!polymorphic) continue;
                var cases = reference.PolymorphicCases!;
                if (!table.Columns.Any(column => column.Name == reference.DiscriminatorColumn)
                    || cases.Select(value => value.DiscriminatorValue).Distinct().Count() != cases.Count
                    || cases.Any(value =>
                        !reference.PrincipalTables.Contains(value.PrincipalTable, StringComparer.Ordinal)))
                    throw new InvalidDataException("A polymorphic reference has an invalid discriminator mapping.");
            }
            if (table.MetadataRule is { } metadataRule)
            {
                if (metadataRule.TypeDomains.Count == 0
                    || metadataRule.TypeDomains.Select(value => value.Type).Distinct().Count()
                    != metadataRule.TypeDomains.Count
                    || metadataRule.TypeDomains.Any(value => value.SubTypes.Count == 0)
                    || metadataRule.TypeDomains.SelectMany(value => value.SubTypes).Distinct().Count()
                    != metadataRule.TypeDomains.Sum(value => value.SubTypes.Count))
                    throw new InvalidDataException("A metadata rule has an invalid Type/SubType domain mapping.");
            }
        }

        if (!DerivedExcludedTables.SequenceEqual(["HealthCheckStats"], StringComparer.Ordinal)
            || !ExcludedConfigKeys.SequenceEqual(["database.import-state"], StringComparer.Ordinal))
        {
            throw new InvalidDataException("The Transfer-v3 exclusion contract is not the reviewed contract.");
        }

        VerifyDigest(ReadEmbeddedSourceSchema(), SourceSchemaSha256, "source schema");
        VerifyDigest(ReadEmbeddedMigrationSourceContract(), MigrationSourceContractSha256, "migration source");
    }

    private static void RequireUnique(IEnumerable<string> values, string description)
    {
        var array = values.ToArray();
        if (array.Distinct(StringComparer.Ordinal).Count() != array.Length)
            throw new InvalidDataException($"The embedded Transfer-v3 contract has duplicate {description}.");
    }

    private static void VerifyDigest(byte[] bytes, string expected, string description)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(actual)))
        {
            throw new InvalidDataException($"The embedded Transfer-v3 {description} evidence digest changed.");
        }
    }

    private static byte[] ReadEmbedded(string suffix)
    {
        var assembly = typeof(TransferV3SourceContract).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(suffix, StringComparison.Ordinal))
            .ToArray();
        if (names.Length != 1)
            throw new InvalidDataException($"Expected exactly one embedded Transfer-v3 resource ending in '{suffix}'.");
        using var stream = assembly.GetManifestResourceStream(names[0])
                           ?? throw new InvalidDataException("The embedded Transfer-v3 resource could not be opened.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal sealed record TransferV3MigrationContract(
    string Id,
    string IntroducedProductVersion,
    IReadOnlyList<string> AllowedProductVersions);

internal sealed record TransferV3TableContract(
    string Name,
    IReadOnlyList<TransferV3ColumnContract> Columns,
    IReadOnlyList<TransferV3KeyComponentContract> Keyset,
    IReadOnlyList<TransferV3UniqueKeyContract> UniqueKeys,
    IReadOnlyList<TransferV3ReferenceContract> References,
    TransferV3MetadataRuleContract? MetadataRule);

internal sealed record TransferV3ColumnContract(
    string Name,
    string DeclaredType,
    string RawStorageClass,
    bool Nullable,
    TransferV3ColumnKind Kind,
    TransferV3InstantEncoding InstantEncoding,
    TransferV3UuidRole UuidRole,
    int? MaxRunes,
    IReadOnlyList<long> AllowedIntegers);

internal sealed record TransferV3KeyComponentContract(
    string Column,
    string SqliteCollation,
    string PostgreSqlCollation,
    string Ordering);

internal sealed record TransferV3UniqueKeyContract(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<TransferV3KeyComponentContract> Components);

internal sealed record TransferV3ReferenceContract(
    string Name,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> PrincipalTables,
    IReadOnlyList<string> PrincipalColumns,
    TransferV3ReferencePolicy Policy,
    string Rationale,
    string? DiscriminatorColumn = null,
    IReadOnlyList<TransferV3PolymorphicReferenceCaseContract>? PolymorphicCases = null);

internal sealed record TransferV3PolymorphicReferenceCaseContract(
    long DiscriminatorValue,
    string PrincipalTable);

internal sealed record TransferV3MetadataRuleContract(
    string TypeColumn,
    string SubTypeColumn,
    string FileBlobColumn,
    IReadOnlyList<TransferV3MetadataSubtypeContract> Subtypes,
    IReadOnlyList<TransferV3TypeSubtypeDomainContract> TypeDomains);

internal sealed record TransferV3MetadataSubtypeContract(int SubType, string LegacyTable);

internal sealed record TransferV3TypeSubtypeDomainContract(
    long Type,
    IReadOnlyList<long> SubTypes);

internal sealed record TransferV3BootstrapContract(
    IReadOnlyList<TransferV3BootstrapConfigContract> Config,
    IReadOnlyList<TransferV3BootstrapRootContract> Roots);

internal sealed record TransferV3BootstrapConfigContract(
    string Name,
    string Pattern,
    bool DistinctFromOtherSecrets);

internal sealed record TransferV3BootstrapRootContract(
    string Id,
    string? ParentId,
    string Name,
    string IdPrefix,
    string CreatedAt,
    int Type,
    int SubType,
    string Path);

internal sealed record TransferV3BlobContract(
    string Layout,
    string FileNameFormat,
    bool IncludeOrphans,
    bool RequireRegularFiles,
    bool RejectSymbolicLinks);
