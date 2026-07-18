using System.Collections.Immutable;

namespace NzbWebDAV.Database.Transfer;

internal sealed record TransferV3Manifest
{
    internal TransferV3Manifest(
        int FormatVersion,
        string SourceProvider,
        string SourceContractSha256,
        string SourceSchemaSha256,
        string MigrationContractSha256,
        string SourceTimeZoneId,
        TransferV3ManifestLimits Limits,
        IEnumerable<TransferV3ManifestTable> Tables,
        IEnumerable<TransferV3ManifestDerivedTable> DerivedTables,
        IEnumerable<TransferV3ManifestInformationalReference> InformationalReferences,
        TransferV3ManifestBlobs Blobs)
    {
        ArgumentNullException.ThrowIfNull(Tables);
        ArgumentNullException.ThrowIfNull(DerivedTables);
        ArgumentNullException.ThrowIfNull(InformationalReferences);

        this.FormatVersion = FormatVersion;
        this.SourceProvider = SourceProvider;
        this.SourceContractSha256 = SourceContractSha256;
        this.SourceSchemaSha256 = SourceSchemaSha256;
        this.MigrationContractSha256 = MigrationContractSha256;
        this.SourceTimeZoneId = SourceTimeZoneId;
        this.Limits = Limits;
        this.Tables = Tables.ToImmutableArray();
        this.DerivedTables = DerivedTables.ToImmutableArray();
        this.InformationalReferences = InformationalReferences.ToImmutableArray();
        this.Blobs = Blobs;
    }

    internal int FormatVersion { get; init; }
    internal string SourceProvider { get; init; }
    internal string SourceContractSha256 { get; init; }
    internal string SourceSchemaSha256 { get; init; }
    internal string MigrationContractSha256 { get; init; }
    internal string SourceTimeZoneId { get; init; }
    internal TransferV3ManifestLimits Limits { get; init; }
    internal ImmutableArray<TransferV3ManifestTable> Tables { get; init; }
    internal ImmutableArray<TransferV3ManifestDerivedTable> DerivedTables { get; init; }
    internal ImmutableArray<TransferV3ManifestInformationalReference> InformationalReferences
    {
        get;
        init;
    }
    internal TransferV3ManifestBlobs Blobs { get; init; }
}

internal sealed record TransferV3ManifestLimits(
    long MaxFieldBytes,
    int MaxBatchRows,
    long MaxBatchBytes);

internal sealed record TransferV3ManifestTable(
    string Name,
    string File,
    int Batches,
    long Rows,
    long DecodedBytes,
    string Sha256);

internal sealed record TransferV3ManifestDerivedTable(
    string Name,
    long Rows,
    string LogicalSha256);

internal sealed record TransferV3ManifestInformationalReference(
    string Name,
    long UnresolvedCount,
    string UnresolvedSha256);

internal sealed record TransferV3ManifestBlobs(
    string Name,
    string File,
    int Batches,
    long Rows,
    long DecodedBytes,
    string Sha256,
    long Count,
    long TotalBytes,
    string InventorySha256);
