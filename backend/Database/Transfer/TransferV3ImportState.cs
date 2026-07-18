namespace NzbWebDAV.Database.Transfer;

internal enum TransferV3ImportStateKind
{
    Fresh,
    Importing,
    DatabaseVerified,
    Failed,
}

internal sealed record TransferV3ImportState
{
    private TransferV3ImportState(
        TransferV3ImportStateKind kind,
        string? manifestSha256)
    {
        Kind = kind;
        ManifestSha256 = manifestSha256;
    }

    internal TransferV3ImportStateKind Kind { get; }

    internal string? ManifestSha256 { get; }

    internal static TransferV3ImportState Fresh() =>
        new(TransferV3ImportStateKind.Fresh, manifestSha256: null);

    internal static TransferV3ImportState Importing(string manifestSha256) =>
        WithDigest(TransferV3ImportStateKind.Importing, manifestSha256);

    internal static TransferV3ImportState DatabaseVerified(string manifestSha256) =>
        WithDigest(TransferV3ImportStateKind.DatabaseVerified, manifestSha256);

    internal static TransferV3ImportState Failed(string manifestSha256) =>
        WithDigest(TransferV3ImportStateKind.Failed, manifestSha256);

    private static TransferV3ImportState WithDigest(
        TransferV3ImportStateKind kind,
        string manifestSha256)
    {
        if (!TransferV3ImportStateCodec.IsCanonicalDigest(manifestSha256))
        {
            throw new ArgumentException(
                "The manifest SHA-256 must be exactly 64 lowercase hexadecimal characters.",
                nameof(manifestSha256));
        }

        return new TransferV3ImportState(kind, manifestSha256);
    }
}
