using System.Text;
using System.Text.Json;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3ImportStateCodec
{
    internal const int FormatVersion = 3;
    internal const string FreshCanonicalJson = "{\"formatVersion\":3,\"state\":\"fresh\"}";
    internal const int FreshCanonicalUtf8Length = 35;
    internal const int ImportingCanonicalUtf8Length = 123;
    internal const string MalformedValueMessage = "The transfer-v3 import state is malformed.";

    private const int ManifestSha256Utf8Length = 64;
    private const int ImportingManifestSha256Offset = 57;
    private const string DatabaseVerifiedPrefix =
        "{\"formatVersion\":3,\"state\":\"database-verified\",\"manifestSha256\":\"";

    private static ReadOnlySpan<byte> FreshCanonicalUtf8 =>
        "{\"formatVersion\":3,\"state\":\"fresh\"}"u8;

    private static ReadOnlySpan<byte> ImportingCanonicalPrefixUtf8 =>
        "{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\""u8;

    private static ReadOnlySpan<byte> DigestStateCanonicalSuffixUtf8 => "\"}"u8;

    internal static readonly int MaxCanonicalUtf8Bytes =
        Encoding.UTF8.GetByteCount(DatabaseVerifiedPrefix) + 64 + 2;

    internal static byte[] Serialize(TransferV3ImportState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Kind == TransferV3ImportStateKind.Fresh && state.ManifestSha256 is null)
        {
            var fresh = new byte[FreshCanonicalUtf8Length];
            WriteFreshCanonical(fresh);
            return fresh;
        }

        if (state.Kind == TransferV3ImportStateKind.Importing
            && IsCanonicalDigest(state.ManifestSha256))
        {
            var importing = new byte[ImportingCanonicalUtf8Length];
            var manifestSha256Utf8 = InitializeImportingCanonical(importing);
            CopyCanonicalDigestToUtf8(state.ManifestSha256!, manifestSha256Utf8);
            return importing;
        }

        var json = state.Kind switch
        {
            TransferV3ImportStateKind.DatabaseVerified when IsCanonicalDigest(state.ManifestSha256) =>
                WithDigestJson("database-verified", state.ManifestSha256!),
            TransferV3ImportStateKind.Failed when IsCanonicalDigest(state.ManifestSha256) =>
                WithDigestJson("failed", state.ManifestSha256!),
            _ => throw new ArgumentException(
                "The transfer-v3 import state violates its digest invariant.",
                nameof(state)),
        };

        return Encoding.UTF8.GetBytes(json);
    }

    internal static void WriteFreshCanonical(Span<byte> destination)
    {
        if (destination.Length != FreshCanonicalUtf8Length)
            throw InvalidDestinationLength(nameof(destination));

        FreshCanonicalUtf8.CopyTo(destination);
    }

    internal static Span<byte> InitializeImportingCanonical(Span<byte> destination)
    {
        if (destination.Length != ImportingCanonicalUtf8Length)
            throw InvalidDestinationLength(nameof(destination));

        ImportingCanonicalPrefixUtf8.CopyTo(destination);
        DigestStateCanonicalSuffixUtf8.CopyTo(destination[^DigestStateCanonicalSuffixUtf8.Length..]);

        var manifestSha256Utf8 = destination.Slice(
            ImportingManifestSha256Offset,
            ManifestSha256Utf8Length);
        manifestSha256Utf8.Clear();
        return manifestSha256Utf8;
    }

    internal static bool IsCanonicalFreshToImportingTransition(
        ReadOnlySpan<byte> expectedCanonicalUtf8,
        ReadOnlySpan<byte> nextCanonicalUtf8)
    {
        if (!expectedCanonicalUtf8.SequenceEqual(FreshCanonicalUtf8)
            || nextCanonicalUtf8.Length != ImportingCanonicalUtf8Length
            || !nextCanonicalUtf8[..ImportingManifestSha256Offset]
                .SequenceEqual(ImportingCanonicalPrefixUtf8)
            || !nextCanonicalUtf8[^DigestStateCanonicalSuffixUtf8.Length..]
                .SequenceEqual(DigestStateCanonicalSuffixUtf8))
        {
            return false;
        }

        var manifestSha256Utf8 = nextCanonicalUtf8.Slice(
            ImportingManifestSha256Offset,
            ManifestSha256Utf8Length);
        foreach (var value in manifestSha256Utf8)
        {
            if (value is not (>= (byte)'0' and <= (byte)'9'
                or >= (byte)'a' and <= (byte)'f'))
            {
                return false;
            }
        }

        return true;
    }

    internal static TransferV3ImportState ParseCanonical(ReadOnlyMemory<byte> value)
    {
        if (value.Length == 0 || value.Length > MaxCanonicalUtf8Bytes)
            throw Malformed();

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3,
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Malformed();

            var properties = root.EnumerateObject().ToArray();
            if (properties.Length < 2
                || properties[0].Name != "formatVersion"
                || properties[0].Value.ValueKind != JsonValueKind.Number
                || !properties[0].Value.TryGetInt32(out var version)
                || version != FormatVersion
                || properties[1].Name != "state"
                || properties[1].Value.ValueKind != JsonValueKind.String)
            {
                throw Malformed();
            }

            var parsed = properties[1].Value.GetString() switch
            {
                "fresh" => ParseFresh(properties),
                "importing" => ParseDigestState(properties, TransferV3ImportState.Importing),
                "database-verified" => ParseDigestState(
                    properties,
                    TransferV3ImportState.DatabaseVerified),
                "failed" => ParseDigestState(properties, TransferV3ImportState.Failed),
                _ => throw Malformed(),
            };

            if (!value.Span.SequenceEqual(Serialize(parsed)))
                throw Malformed();

            return parsed;
        }
        catch (JsonException)
        {
            throw Malformed();
        }
        catch (InvalidOperationException)
        {
            throw Malformed();
        }
    }

    internal static bool IsCanonicalDigest(string? digest)
    {
        return digest is { Length: 64 }
               && digest.All(character =>
                   character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static TransferV3ImportState ParseFresh(JsonProperty[] properties)
    {
        if (properties.Length != 2)
            throw Malformed();

        return TransferV3ImportState.Fresh();
    }

    private static TransferV3ImportState ParseDigestState(
        JsonProperty[] properties,
        Func<string, TransferV3ImportState> factory)
    {
        if (properties.Length != 3
            || properties[2].Name != "manifestSha256"
            || properties[2].Value.ValueKind != JsonValueKind.String)
        {
            throw Malformed();
        }

        var digest = properties[2].Value.GetString();
        if (!IsCanonicalDigest(digest))
            throw Malformed();

        return factory(digest!);
    }

    private static string WithDigestJson(string state, string digest) =>
        $"{{\"formatVersion\":3,\"state\":\"{state}\",\"manifestSha256\":\"{digest}\"}}";

    private static void CopyCanonicalDigestToUtf8(
        string digest,
        Span<byte> destination)
    {
        for (var index = 0; index < digest.Length; index++)
            destination[index] = checked((byte)digest[index]);
    }

    private static ArgumentException InvalidDestinationLength(string parameterName) =>
        new(
            "The transfer-v3 canonical destination has an invalid length.",
            parameterName);

    private static FormatException Malformed() => new(MalformedValueMessage);
}
