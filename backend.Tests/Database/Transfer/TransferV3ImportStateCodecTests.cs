using System.Text;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3ImportStateCodecTests
{
    private const string DigestA =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static TheoryData<string, string> CanonicalStates => new()
    {
        {
            "fresh",
            "{\"formatVersion\":3,\"state\":\"fresh\"}"
        },
        {
            "importing",
            $"{{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\"{DigestA}\"}}"
        },
        {
            "database-verified",
            $"{{\"formatVersion\":3,\"state\":\"database-verified\",\"manifestSha256\":\"{DigestA}\"}}"
        },
        {
            "failed",
            $"{{\"formatVersion\":3,\"state\":\"failed\",\"manifestSha256\":\"{DigestA}\"}}"
        },
    };

    [Theory]
    [MemberData(nameof(CanonicalStates))]
    public void CanonicalStatesRoundTripAsExactUtf8Bytes(
        string stateName,
        string expectedJson)
    {
        var state = stateName switch
        {
            "fresh" => TransferV3ImportState.Fresh(),
            "importing" => TransferV3ImportState.Importing(DigestA),
            "database-verified" => TransferV3ImportState.DatabaseVerified(DigestA),
            "failed" => TransferV3ImportState.Failed(DigestA),
            _ => throw new ArgumentOutOfRangeException(nameof(stateName)),
        };
        var bytes = TransferV3ImportStateCodec.Serialize(state);

        Assert.Equal(Encoding.UTF8.GetBytes(expectedJson), bytes);
        Assert.Equal(state, TransferV3ImportStateCodec.ParseCanonical(bytes));
    }

    [Fact]
    public void FreshHasNoDigestAndEveryNonFreshStateHasExactlyOneDigest()
    {
        Assert.Null(TransferV3ImportState.Fresh().ManifestSha256);
        Assert.Equal(DigestA, TransferV3ImportState.Importing(DigestA).ManifestSha256);
        Assert.Equal(DigestA, TransferV3ImportState.DatabaseVerified(DigestA).ManifestSha256);
        Assert.Equal(DigestA, TransferV3ImportState.Failed(DigestA).ManifestSha256);
    }

    [Fact]
    public void CanonicalSpanWritersProduceExactFreshAndImportingUtf8()
    {
        const string importingPrefix =
            "{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\"";
        var expectedFresh = Encoding.UTF8.GetBytes(
            TransferV3ImportStateCodec.FreshCanonicalJson);
        var expectedImporting = Encoding.UTF8.GetBytes($"{importingPrefix}{DigestA}\"}}");

        Assert.Equal(35, TransferV3ImportStateCodec.FreshCanonicalUtf8Length);
        Assert.Equal(123, TransferV3ImportStateCodec.ImportingCanonicalUtf8Length);

        var fresh = new byte[TransferV3ImportStateCodec.FreshCanonicalUtf8Length];
        TransferV3ImportStateCodec.WriteFreshCanonical(fresh);

        var importing = Enumerable.Repeat(
                byte.MaxValue,
                TransferV3ImportStateCodec.ImportingCanonicalUtf8Length)
            .ToArray();
        var manifestSha256Utf8 =
            TransferV3ImportStateCodec.InitializeImportingCanonical(importing);

        Assert.Equal(64, manifestSha256Utf8.Length);
        Assert.All(manifestSha256Utf8.ToArray(), value => Assert.Equal(0, value));
        manifestSha256Utf8[0] = (byte)'f';
        Assert.Equal((byte)'f', importing[57]);
        Encoding.ASCII.GetBytes(DigestA).CopyTo(manifestSha256Utf8);

        Assert.Equal(expectedFresh, fresh);
        Assert.Equal(expectedImporting, importing);
        Assert.True(
            TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
                fresh,
                importing));
    }

    [Fact]
    public void CanonicalSpanWritersRejectEveryWrongDestinationLength()
    {
        var shortFresh = Assert.Throws<ArgumentException>(() =>
            TransferV3ImportStateCodec.WriteFreshCanonical(
                new byte[TransferV3ImportStateCodec.FreshCanonicalUtf8Length - 1]));
        var longFresh = Assert.Throws<ArgumentException>(() =>
            TransferV3ImportStateCodec.WriteFreshCanonical(
                new byte[TransferV3ImportStateCodec.FreshCanonicalUtf8Length + 1]));
        var shortImporting = Assert.Throws<ArgumentException>(() =>
            InitializeImportingCanonical(
                new byte[TransferV3ImportStateCodec.ImportingCanonicalUtf8Length - 1]));
        var longImporting = Assert.Throws<ArgumentException>(() =>
            InitializeImportingCanonical(
                new byte[TransferV3ImportStateCodec.ImportingCanonicalUtf8Length + 1]));

        Assert.Equal("destination", shortFresh.ParamName);
        Assert.Equal("destination", longFresh.ParamName);
        Assert.Equal("destination", shortImporting.ParamName);
        Assert.Equal("destination", longImporting.ParamName);
    }

    [Fact]
    public void CanonicalFreshToImportingPredicateRejectsEveryFramingAndDigestMutation()
    {
        const int importingDigestOffset = 57;
        var (fresh, importing) = CreateCanonicalFreshToImportingTransition();

        Assert.False(TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
            fresh.AsSpan(1),
            importing));
        Assert.False(TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
            fresh,
            importing.AsSpan(1)));

        var mutatedFresh = (byte[])fresh.Clone();
        mutatedFresh[0] ^= 1;
        Assert.False(TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
            mutatedFresh,
            importing));

        var mutatedPrefix = (byte[])importing.Clone();
        mutatedPrefix[0] ^= 1;
        Assert.False(TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
            fresh,
            mutatedPrefix));

        var uppercaseDigest = (byte[])importing.Clone();
        uppercaseDigest[importingDigestOffset] = (byte)'A';
        Assert.False(TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
            fresh,
            uppercaseDigest));

        var invalidDigest = (byte[])importing.Clone();
        invalidDigest[importingDigestOffset + 1] = (byte)'g';
        Assert.False(TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
            fresh,
            invalidDigest));

        var mutatedSuffix = (byte[])importing.Clone();
        mutatedSuffix[^1] ^= 1;
        Assert.False(TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
            fresh,
            mutatedSuffix));
    }

    [Fact]
    public void CanonicalSpanWritersAllocateNoManagedMemory()
    {
        var fresh = new byte[TransferV3ImportStateCodec.FreshCanonicalUtf8Length];
        var importing = new byte[TransferV3ImportStateCodec.ImportingCanonicalUtf8Length];

        for (var index = 0; index < 1_024; index++)
        {
            TransferV3ImportStateCodec.WriteFreshCanonical(fresh);
            _ = TransferV3ImportStateCodec.InitializeImportingCanonical(importing);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var observedDigestBytes = 0;
        for (var index = 0; index < 1_024; index++)
        {
            TransferV3ImportStateCodec.WriteFreshCanonical(fresh);
            observedDigestBytes +=
                TransferV3ImportStateCodec.InitializeImportingCanonical(importing).Length;
        }
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(64 * 1_024, observedDigestBytes);
        Assert.Equal(0, allocatedBytes);
    }

    [Theory]
    [MemberData(nameof(MalformedCanonicalValues))]
    public void ParseCanonicalRejectsMalformedOrNoncanonicalValuesWithoutEchoingInput(string input)
    {
        var error = Assert.Throws<FormatException>(() =>
            TransferV3ImportStateCodec.ParseCanonical(Encoding.UTF8.GetBytes(input)));

        Assert.Equal(TransferV3ImportStateCodec.MalformedValueMessage, error.Message);
        if (!string.IsNullOrWhiteSpace(input))
            Assert.DoesNotContain(input, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(DigestA, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCanonicalRejectsUtf8BomAndOversizedInputBeforeParsing()
    {
        var fresh = TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Fresh());
        var withBom = new byte[fresh.Length + 3];
        withBom[0] = 0xef;
        withBom[1] = 0xbb;
        withBom[2] = 0xbf;
        fresh.CopyTo(withBom, 3);

        var bomError = Assert.Throws<FormatException>(() =>
            TransferV3ImportStateCodec.ParseCanonical(withBom));
        var oversizedError = Assert.Throws<FormatException>(() =>
            TransferV3ImportStateCodec.ParseCanonical(
                new byte[TransferV3ImportStateCodec.MaxCanonicalUtf8Bytes + 1]));

        Assert.Equal(TransferV3ImportStateCodec.MalformedValueMessage, bomError.Message);
        Assert.Equal(TransferV3ImportStateCodec.MalformedValueMessage, oversizedError.Message);
    }

    [Fact]
    public void ParseCanonicalRejectsInvalidUtf8WithoutEchoingBytes()
    {
        byte[] invalidUtf8 = [0x7b, 0x22, 0x80, 0x22, 0x7d];

        var error = Assert.Throws<FormatException>(() =>
            TransferV3ImportStateCodec.ParseCanonical(invalidUtf8));

        Assert.Equal(TransferV3ImportStateCodec.MalformedValueMessage, error.Message);
        Assert.DoesNotContain(Convert.ToHexString(invalidUtf8), error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeF")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(" 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void StateFactoriesRejectNoncanonicalDigestsWithoutEchoingThem(string digest)
    {
        var error = Assert.Throws<ArgumentException>(() => TransferV3ImportState.Importing(digest));

        Assert.Equal("manifestSha256", error.ParamName);
        if (digest.Length > 0)
            Assert.DoesNotContain(digest, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSqlBootstrapFreshLiteralsRemainTiedToTheCodecContract()
    {
        var repositoryRoot = SqliteContractTestSupport.RepositoryRoot;
        var baseline = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "backend",
            "Database",
            "PostgreSqlMigrations",
            "20260712000000_PostgreSqlNativeBaseline.cs"));
        var freshBootstrapContract = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "backend",
            "Database",
            "PostgreSqlFreshBootstrapContract.cs"));

        Assert.Equal(
            1,
            baseline.Split(
                TransferV3ImportStateCodec.FreshCanonicalJson,
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            nameof(TransferV3ReservedConfigPolicy) + "." +
            nameof(TransferV3ReservedConfigPolicy.ImportStateKey),
            freshBootstrapContract,
            StringComparison.Ordinal);
        Assert.Contains(
            nameof(TransferV3ImportStateCodec) + "." +
            nameof(TransferV3ImportStateCodec.FreshCanonicalJson),
            freshBootstrapContract,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"ConfigValue\" = '{\"formatVersion\":3,\"state\":\"fresh\"}'",
            freshBootstrapContract,
            StringComparison.Ordinal);
    }

    public static TheoryData<string> MalformedCanonicalValues => new()
    {
        "",
        " ",
        " {\"formatVersion\":3,\"state\":\"fresh\"}",
        "{\"formatVersion\":3,\"state\":\"fresh\"} ",
        "{\"formatVersion\":3,\"state\":\"fresh\"}\n",
        "{\"formatVersion\":3,/*comment*/\"state\":\"fresh\"}",
        "{\"formatVersion\":3,\"state\":\"fresh\",}",
        "{\"formatVersion\":3,\"state\":\"fresh\"}{}",
        "{\"state\":\"fresh\",\"formatVersion\":3}",
        "{\"formatVersion\":3}",
        "{\"formatVersion\":3,\"state\":\"fresh\",\"state\":\"fresh\"}",
        "{\"formatVersion\":3,\"state\":\"fresh\",\"unknown\":true}",
        "{\"FormatVersion\":3,\"state\":\"fresh\"}",
        "{\"formatVersion\":3,\"State\":\"fresh\"}",
        "{\"format\\u0056ersion\":3,\"state\":\"fresh\"}",
        "{\"formatVersion\":3,\"state\":\"fr\\u0065sh\"}",
        "{\"formatVersion\":2,\"state\":\"fresh\"}",
        "{\"formatVersion\":4,\"state\":\"fresh\"}",
        "{\"formatVersion\":\"3\",\"state\":\"fresh\"}",
        "{\"formatVersion\":3.0,\"state\":\"fresh\"}",
        "{\"formatVersion\":3e0,\"state\":\"fresh\"}",
        "{\"formatVersion\":3,\"state\":\"unknown\"}",
        $"{{\"formatVersion\":3,\"state\":\"fresh\",\"manifestSha256\":\"{DigestA}\"}}",
        "{\"formatVersion\":3,\"state\":\"importing\"}",
        $"{{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\"{DigestA[..63]}\"}}",
        $"{{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\"{DigestA}0\"}}",
        $"{{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\"{DigestA.ToUpperInvariant()}\"}}",
        $"{{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\"{DigestA[..63]}G\"}}",
        $"{{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\"sha256:{DigestA}\"}}",
        $"{{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\" {DigestA}\"}}",
        $"{{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\"\\u0030{DigestA[1..]}\"}}",
        $"{{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\"{DigestA}\",\"manifestSha256\":\"{DigestA}\"}}",
        $"{{\"formatVersion\":3,\"manifestSha256\":\"{DigestA}\",\"state\":\"importing\"}}",
        "{\"formatVersion\":3,\"state\":\"fresh\\u0000\"}",
        "{\"formatVersion\":3,\"state\":\"\\ud800\"}",
    };

    private static (byte[] Fresh, byte[] Importing)
        CreateCanonicalFreshToImportingTransition()
    {
        var fresh = new byte[TransferV3ImportStateCodec.FreshCanonicalUtf8Length];
        TransferV3ImportStateCodec.WriteFreshCanonical(fresh);

        var importing = new byte[TransferV3ImportStateCodec.ImportingCanonicalUtf8Length];
        var manifestSha256Utf8 =
            TransferV3ImportStateCodec.InitializeImportingCanonical(importing);
        Encoding.ASCII.GetBytes(DigestA).CopyTo(manifestSha256Utf8);
        return (fresh, importing);
    }

    private static void InitializeImportingCanonical(byte[] destination)
    {
        _ = TransferV3ImportStateCodec.InitializeImportingCanonical(destination);
    }
}
