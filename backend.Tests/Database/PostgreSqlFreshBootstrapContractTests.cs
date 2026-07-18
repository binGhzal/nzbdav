using System.Reflection;
using System.Text;
using Npgsql;
using NzbWebDAV.Database;

namespace backend.Tests.Database;

public sealed class PostgreSqlFreshBootstrapContractTests
{
    private static readonly string ApiKey = new('1', 32);
    private static readonly string StrmKey = new('2', 32);
    private const string Canary = "FRESH-BOOTSTRAP-CANARY-DO-NOT-ECHO";
    private const string RootId = "00000000-0000-0000-0000-000000000000";
    private const string Timestamp = "0001-01-01 00:00:00.000000";

    private static readonly string[] DavRows =
    [
        """{"Id":"00000000-0000-0000-0000-000000000000","CreatedAt":"0001-01-01 00:00:00.000000","FileBlobId":null,"FileSize":null,"HistoryItemId":null,"IdPrefix":"00000","LastHealthCheck":null,"Name":"/","NextHealthCheck":null,"ParentId":null,"Path":"/","ReleaseDate":null,"SubType":102,"Type":1,"NzbBlobId":null}""",
        """{"Id":"00000000-0000-0000-0000-000000000001","CreatedAt":"0001-01-01 00:00:00.000000","FileBlobId":null,"FileSize":null,"HistoryItemId":null,"IdPrefix":"00000","LastHealthCheck":null,"Name":"nzbs","NextHealthCheck":null,"ParentId":"00000000-0000-0000-0000-000000000000","Path":"/nzbs","ReleaseDate":null,"SubType":103,"Type":1,"NzbBlobId":null}""",
        """{"Id":"00000000-0000-0000-0000-000000000002","CreatedAt":"0001-01-01 00:00:00.000000","FileBlobId":null,"FileSize":null,"HistoryItemId":null,"IdPrefix":"00000","LastHealthCheck":null,"Name":"content","NextHealthCheck":null,"ParentId":"00000000-0000-0000-0000-000000000000","Path":"/content","ReleaseDate":null,"SubType":104,"Type":1,"NzbBlobId":null}""",
        """{"Id":"00000000-0000-0000-0000-000000000003","CreatedAt":"0001-01-01 00:00:00.000000","FileBlobId":null,"FileSize":null,"HistoryItemId":null,"IdPrefix":"00000","LastHealthCheck":null,"Name":"completed-symlinks","NextHealthCheck":null,"ParentId":"00000000-0000-0000-0000-000000000000","Path":"/completed-symlinks","ReleaseDate":null,"SubType":105,"Type":1,"NzbBlobId":null}""",
        """{"Id":"00000000-0000-0000-0000-000000000004","CreatedAt":"0001-01-01 00:00:00.000000","FileBlobId":null,"FileSize":null,"HistoryItemId":null,"IdPrefix":"00000","LastHealthCheck":null,"Name":".ids","NextHealthCheck":null,"ParentId":"00000000-0000-0000-0000-000000000000","Path":"/.ids","ReleaseDate":null,"SubType":106,"Type":1,"NzbBlobId":null}"""
    ];

    private static readonly string[] ConfigRows =
    [
        ConfigRow("api.key", ApiKey),
        ConfigRow("api.strm-key", StrmKey),
        """{"ConfigName":"database.import-state","ConfigValue":"{\"formatVersion\":3,\"state\":\"fresh\"}"}"""
    ];

    private static readonly string[] RelationNames =
    [
        "Accounts",
        "HistoryItems",
        "QueueItems",
        "RepairRuns",
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
        "HealthCheckStats"
    ];

    private static readonly string[] DavPropertyOrder =
    [
        "Id",
        "CreatedAt",
        "FileBlobId",
        "FileSize",
        "HistoryItemId",
        "IdPrefix",
        "LastHealthCheck",
        "Name",
        "NextHealthCheck",
        "ParentId",
        "Path",
        "ReleaseDate",
        "SubType",
        "Type",
        "NzbBlobId"
    ];

    [Fact]
    public void ValidateAcceptsOnlyTheIndependentReviewedSnapshot()
    {
        using var snapshot = ExactSnapshot();

        PostgreSqlFreshBootstrapContract.Validate(snapshot);

        Assert.Equal(5, DavRows.Length);
        Assert.Equal(15, DavPropertyOrder.Length);
        Assert.Equal(26, RelationNames.Length);
        Assert.Equal(ExactDavBytes(), snapshot.CanonicalDavItemsUtf8.ToArray());
        Assert.Equal(ExactConfigBytes(), snapshot.CanonicalConfigItemsUtf8.ToArray());
        Assert.Equal(ExactCounts(), snapshot.OtherRelationCounts);
    }

    [Theory]
    [MemberData(nameof(EveryDavFieldMutation))]
    public void ValidateRejectsMutationOfEveryFieldOnEveryRoot(
        int rowIndex,
        string propertyName,
        string replacementJson)
    {
        var mutated = ReplaceProperty(DavRows, rowIndex, propertyName, replacementJson);

        AssertRejected(DavDocument(mutated), ExactConfigBytes(), ExactCounts());
    }

    public static IEnumerable<object[]> EveryDavFieldMutation()
    {
        for (var row = 0; row < DavRows.Length; row++)
        {
            var id = $"00000000-0000-0000-0000-00000000000{row}";
            yield return [row, "Id", JsonString(id[..^1] + (row == 4 ? "3" : "4"))];
            yield return [row, "CreatedAt", JsonString("0001-01-01 00:00:00.000001")];
            yield return [row, "FileBlobId", JsonString(Canary)];
            yield return [row, "FileSize", "0"];
            yield return [row, "HistoryItemId", JsonString(Canary)];
            yield return [row, "IdPrefix", JsonString("00001")];
            yield return [row, "LastHealthCheck", JsonString(Timestamp)];
            yield return [row, "Name", JsonString(Canary)];
            yield return [row, "NextHealthCheck", JsonString(Timestamp)];
            yield return [row, "ParentId", row == 0 ? JsonString(RootId) : "null"];
            yield return [row, "Path", JsonString(Canary)];
            yield return [row, "ReleaseDate", JsonString(Timestamp)];
            yield return [row, "SubType", (202 + row).ToString(System.Globalization.CultureInfo.InvariantCulture)];
            yield return [row, "Type", "2"];
            yield return [row, "NzbBlobId", JsonString(Canary)];
        }
    }

    [Fact]
    public void ValidateRejectsDavRowCardinalityIdentityAndOrderDrift()
    {
        var wrong = new[]
        {
            DavDocument(DavRows[..^1]),
            DavDocument([.. DavRows, DavRows[4]]),
            DavDocument([DavRows[0], DavRows[2], DavRows[1], DavRows[3], DavRows[4]]),
            DavDocument([DavRows[0], DavRows[1], DavRows[1], DavRows[3], DavRows[4]])
        };

        Assert.All(wrong, bytes => AssertRejected(bytes, ExactConfigBytes(), ExactCounts()));
    }

    [Fact]
    public void ValidateRejectsDavPropertyMissingExtraAndOrderDrift()
    {
        var missing = RemoveProperty(DavRows, 0, "FileBlobId");
        var extra = DavRows.ToArray();
        extra[0] = extra[0][..^1] + $",\"{Canary}\":null}}";
        var reordered = DavRows.ToArray();
        reordered[0] = reordered[0].Replace(
            $"{{\"Id\":\"{RootId}\",\"CreatedAt\":\"{Timestamp}\"",
            $"{{\"CreatedAt\":\"{Timestamp}\",\"Id\":\"{RootId}\"",
            StringComparison.Ordinal);

        AssertRejected(DavDocument(missing), ExactConfigBytes(), ExactCounts());
        AssertRejected(DavDocument(extra), ExactConfigBytes(), ExactCounts());
        AssertRejected(DavDocument(reordered), ExactConfigBytes(), ExactCounts());
    }

    [Theory]
    [MemberData(nameof(NonCanonicalDavDocuments))]
    public void ValidateRejectsNonCanonicalDavBytes(byte[] bytes)
    {
        AssertRejected(bytes, ExactConfigBytes(), ExactCounts());
    }

    public static IEnumerable<object[]> NonCanonicalDavDocuments()
    {
        var canonical = ExactDavBytes();
        yield return [canonical[..^1]];
        yield return [canonical.Append((byte)' ').ToArray()];
        yield return [new byte[] { 0xef, 0xbb, 0xbf }.Concat(canonical).ToArray()];
        yield return [Encoding.UTF8.GetBytes(ExactDavJson().Replace("[{", "[ {", StringComparison.Ordinal))];
        yield return [Encoding.UTF8.GetBytes(ExactDavJson().Replace("\"/\"", "\"\\/\"", StringComparison.Ordinal))];

        var malformed = canonical.ToArray();
        var name = Encoding.UTF8.GetBytes("completed-symlinks");
        var offset = malformed.AsSpan().IndexOf(name);
        malformed[offset] = 0xc0;
        malformed[offset + 1] = 0xaf;
        yield return [malformed];
    }

    [Fact]
    public void ValidateRequiresExactConfigRowsNamesAndReservedBytes()
    {
        var wrongName = ReplaceProperty(ConfigRows, 0, "ConfigName", JsonString(Canary));
        var wrongReserved = ReplaceProperty(
            ConfigRows,
            2,
            "ConfigValue",
            JsonString("{\"state\":\"fresh\",\"formatVersion\":3}"));
        var wrongState = ReplaceProperty(
            ConfigRows,
            2,
            "ConfigValue",
            JsonString("{\"formatVersion\":3,\"state\":\"ready\"}"));

        AssertRejected(ExactDavBytes(), ConfigDocument(wrongName), ExactCounts());
        AssertRejected(ExactDavBytes(), ConfigDocument(wrongReserved), ExactCounts());
        AssertRejected(ExactDavBytes(), ConfigDocument(wrongState), ExactCounts());
    }

    [Fact]
    public void ValidateRejectsConfigRowCardinalityDuplicatesAndOrderDrift()
    {
        var wrong = new[]
        {
            ConfigDocument(ConfigRows[..^1]),
            ConfigDocument([.. ConfigRows, ConfigRows[2]]),
            ConfigDocument([ConfigRows[1], ConfigRows[0], ConfigRows[2]]),
            ConfigDocument([ConfigRows[0], ConfigRows[0], ConfigRows[2]])
        };

        Assert.All(wrong, bytes => AssertRejected(ExactDavBytes(), bytes, ExactCounts()));
    }

    [Theory]
    [MemberData(nameof(InvalidSecretPairs))]
    public void ValidateRejectsSecretGrammarAndDistinctness(string apiKey, string strmKey)
    {
        var config = ConfigDocument(
        [
            ConfigRow("api.key", apiKey),
            ConfigRow("api.strm-key", strmKey),
            ConfigRows[2]
        ]);

        AssertRejected(ExactDavBytes(), config, ExactCounts(), apiKey, strmKey);
    }

    public static IEnumerable<object[]> InvalidSecretPairs()
    {
        for (var index = 0; index < ApiKey.Length; index++)
        {
            var chars = ApiKey.ToCharArray();
            chars[index] = 'g';
            yield return [new string(chars), StrmKey];
        }

        yield return [ApiKey[..^1], StrmKey];
        yield return [ApiKey + "0", StrmKey];
        yield return ["A" + ApiKey[1..], StrmKey];
        yield return ["é" + ApiKey[1..], StrmKey];
        yield return [ApiKey[..^1] + "\n", StrmKey];
        yield return [ApiKey[..^1] + " ", StrmKey];
        yield return [ApiKey[..^1] + "\0", StrmKey];
        yield return [ApiKey, ApiKey];
    }

    [Fact]
    public void ValidateRejectsConfigPropertyMissingExtraAndOrderDrift()
    {
        var missing = ConfigRows.ToArray();
        missing[0] = missing[0].Replace(
            $",\"ConfigValue\":\"{ApiKey}\"",
            string.Empty,
            StringComparison.Ordinal);
        var extra = ConfigRows.ToArray();
        extra[0] = extra[0][..^1] + $",\"{Canary}\":null}}";
        var reordered = ConfigRows.ToArray();
        reordered[0] =
            $"{{\"ConfigValue\":\"{ApiKey}\",\"ConfigName\":\"api.key\"}}";

        AssertRejected(ExactDavBytes(), ConfigDocument(missing), ExactCounts());
        AssertRejected(ExactDavBytes(), ConfigDocument(extra), ExactCounts());
        AssertRejected(ExactDavBytes(), ConfigDocument(reordered), ExactCounts());
    }

    [Fact]
    public void ValidateRejectsOversizedDocumentsBeforeValueParsing()
    {
        var oversized = new byte[1024 * 1024];

        AssertRejected(oversized, ExactConfigBytes(), ExactCounts());
        AssertRejected(ExactDavBytes(), oversized, ExactCounts());
    }

    [Theory]
    [MemberData(nameof(NonCanonicalConfigDocuments))]
    public void ValidateRejectsNonCanonicalConfigBytes(byte[] bytes)
    {
        AssertRejected(ExactDavBytes(), bytes, ExactCounts());
    }

    public static IEnumerable<object[]> NonCanonicalConfigDocuments()
    {
        var canonical = ExactConfigBytes();
        yield return [canonical[..^1]];
        yield return [canonical.Append((byte)'\n').ToArray()];
        yield return [new byte[] { 0xef, 0xbb, 0xbf }.Concat(canonical).ToArray()];
        yield return [Encoding.UTF8.GetBytes(ExactConfigJson().Replace("[{", "[ {", StringComparison.Ordinal))];
        yield return [Encoding.UTF8.GetBytes(ExactConfigJson().Replace("api.key", "api\\u002ekey", StringComparison.Ordinal))];

        var malformed = canonical.ToArray();
        var offset = malformed.AsSpan().IndexOf(Encoding.UTF8.GetBytes("api.strm-key"));
        malformed[offset] = 0xc0;
        malformed[offset + 1] = 0xaf;
        yield return [malformed];
    }

    [Fact]
    public void ValidateRequiresAllTwentySixZeroCountsInExactContractOrder()
    {
        var exact = ExactCounts();
        Assert.Equal(RelationNames, exact.Select(value => value.RelationName));
        Assert.All(exact, value => Assert.Equal(0, value.Count));

        for (var index = 0; index < exact.Length; index++)
        {
            var nonzero = exact.ToArray();
            nonzero[index] = nonzero[index] with { Count = 1 };
            AssertRejected(ExactDavBytes(), ExactConfigBytes(), nonzero);

            var negative = exact.ToArray();
            negative[index] = negative[index] with { Count = -1 };
            AssertRejected(ExactDavBytes(), ExactConfigBytes(), negative);
        }
    }

    [Fact]
    public void ValidateRejectsRelationMissingExtraDuplicateRenameAndReorder()
    {
        var exact = ExactCounts();
        var renamed = exact.ToArray();
        renamed[0] = new PostgreSqlApplicationRelationCount(Canary, 0);
        var nullName = exact.ToArray();
        nullName[0] = new PostgreSqlApplicationRelationCount(null!, 0);
        var forbiddenApplicationRelation = exact.ToArray();
        forbiddenApplicationRelation[0] =
            new PostgreSqlApplicationRelationCount("DavItems", 0);
        var forbiddenHistoryRelation = exact.ToArray();
        forbiddenHistoryRelation[0] = new PostgreSqlApplicationRelationCount(
            "__EFMigrationsHistory_PostgreSql",
            0);
        var reordered = exact.ToArray();
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);

        PostgreSqlApplicationRelationCount[][] wrong =
        [
            exact[..^1],
            [.. exact, new PostgreSqlApplicationRelationCount(Canary, 0)],
            [exact[0], exact[0], .. exact[2..]],
            renamed,
            nullName,
            forbiddenApplicationRelation,
            forbiddenHistoryRelation,
            reordered
        ];

        Assert.All(wrong, counts => AssertRejected(ExactDavBytes(), ExactConfigBytes(), counts));
    }

    [Fact]
    public void SnapshotOwnsFrozenCopiesAndDisposeZeroesItsConfigBuffer()
    {
        var davInput = ExactDavBytes();
        var configInput = ExactConfigBytes();
        var countInput = ExactCounts().ToList();
        using var snapshot = new PostgreSqlFreshBootstrapSnapshot(davInput, configInput, countInput);
        var ownedDav = snapshot.CanonicalDavItemsUtf8;
        var ownedConfig = snapshot.CanonicalConfigItemsUtf8;

        davInput.AsSpan().Fill((byte)'x');
        configInput.AsSpan().Fill((byte)'x');
        countInput[0] = new PostgreSqlApplicationRelationCount(Canary, 99);

        Assert.Equal(ExactDavBytes(), ownedDav.ToArray());
        Assert.Equal(ExactConfigBytes(), ownedConfig.ToArray());
        Assert.Equal(ExactCounts(), snapshot.OtherRelationCounts);
        if (snapshot.OtherRelationCounts is IList<PostgreSqlApplicationRelationCount> list)
            Assert.True(list.IsReadOnly);

        snapshot.Dispose();
        Assert.All(ownedConfig.ToArray(), value => Assert.Equal(0, value));
        snapshot.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task LiveReadersRejectNonpositiveTimeoutBeforeProviderAccess(int timeout)
    {
        var capture = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PostgreSqlFreshBootstrapContract.CaptureAsync(
                null!,
                null!,
                timeout,
                CancellationToken.None));
        var validate = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PostgreSqlFreshBootstrapContract.ValidateAsync(
                null!,
                null!,
                timeout,
                CancellationToken.None));

        Assert.Equal("commandTimeoutSeconds", capture.ParamName);
        Assert.Equal("commandTimeoutSeconds", validate.ParamName);
    }

    [Fact]
    public async Task LiveReadersRejectNullCallerResourcesWithoutOpeningAProvider()
    {
        var connection = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            PostgreSqlFreshBootstrapContract.CaptureAsync(
                null!,
                null!,
                1,
                CancellationToken.None));
        await using var closed = new NpgsqlConnection();
        var transaction = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            PostgreSqlFreshBootstrapContract.CaptureAsync(
                closed,
                null!,
                1,
                CancellationToken.None));

        Assert.Equal("connection", connection.ParamName);
        Assert.Equal("transaction", transaction.ParamName);
    }

    [Fact]
    public void ContractSurfaceMatchesTheLockedDisposableSnapshot()
    {
        var snapshot = typeof(PostgreSqlFreshBootstrapSnapshot);
        Assert.True(snapshot.IsSealed);
        Assert.Contains(typeof(IDisposable), snapshot.GetInterfaces());
        Assert.Equal(typeof(ReadOnlyMemory<byte>), Property(snapshot, "CanonicalDavItemsUtf8").PropertyType);
        Assert.Equal(typeof(ReadOnlyMemory<byte>), Property(snapshot, "CanonicalConfigItemsUtf8").PropertyType);
        Assert.Equal(
            typeof(IReadOnlyList<PostgreSqlApplicationRelationCount>),
            Property(snapshot, "OtherRelationCounts").PropertyType);

        AssertMethod(typeof(PostgreSqlFreshBootstrapContract), "Validate", typeof(void), snapshot);
        AssertMethod(
            typeof(PostgreSqlFreshBootstrapContract),
            "CaptureAsync",
            typeof(Task<PostgreSqlFreshBootstrapSnapshot>),
            typeof(NpgsqlConnection),
            typeof(NpgsqlTransaction),
            typeof(int),
            typeof(CancellationToken));
        AssertMethod(
            typeof(PostgreSqlFreshBootstrapContract),
            "ValidateAsync",
            typeof(Task),
            typeof(NpgsqlConnection),
            typeof(NpgsqlTransaction),
            typeof(int),
            typeof(CancellationToken));
    }

    [Fact]
    public void CaptureSourceUsesBoundedSentinelsStrictBytesAndCallerTransaction()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Database/PostgreSqlFreshBootstrapContract.cs"));

        Assert.Contains("octet_length", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("convert_to", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CASE WHEN octet_length", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ELSE ''::bytea", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("substring(convert_to", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "TIMESTAMP '0001-01-01 00:00:00'",
            source,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXTRACT(ERA", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT 6", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT 4", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CommandTimeout", source, StringComparison.Ordinal);
        Assert.Contains("commandTimeoutSeconds", source, StringComparison.Ordinal);
        Assert.Contains("Transaction", source, StringComparison.Ordinal);
        Assert.Contains("transaction", source, StringComparison.Ordinal);
        Assert.Contains("new UTF8Encoding(false, true)", source, StringComparison.Ordinal);
        Assert.Contains("TransferV3PostgreSqlTargetContract.LoadEmbedded", source, StringComparison.Ordinal);
        Assert.Contains("DerivedHealthCheckStats", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Regex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT *", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginTransaction", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommitAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RollbackAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigSerializationUsesOneFixedCapacityZeroingBufferWithoutGrowth()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Database/PostgreSqlFreshBootstrapContract.cs"));

        Assert.Contains("IBufferWriter<byte>", source, StringComparison.Ordinal);
        Assert.Contains("SensitiveConfigBufferWriter", source, StringComparison.Ordinal);
        Assert.True(
            source.Split("new SensitiveConfigBufferWriter", StringSplitOptions.None).Length - 1 >= 2,
            "Both expected-config and captured-config serialization must use the fixed sensitive buffer.");
        Assert.Contains("MaximumConfigDocumentUtf8Bytes", source, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory", source, StringComparison.Ordinal);
        Assert.Contains("catch (DecoderFallbackException)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeMigratorDelegatesFreshValidationToTheSharedTransactionContract()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Database/PostgreSqlNativeMigrator.cs"));

        Assert.Contains("PostgreSqlFreshBootstrapContract.ValidateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ValidateFreshBootstrapRowsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("distinct generated API secrets", source, StringComparison.Ordinal);
        Assert.DoesNotContain("root bootstrap rows", source, StringComparison.Ordinal);
    }

    private static PostgreSqlFreshBootstrapSnapshot ExactSnapshot() =>
        new(ExactDavBytes(), ExactConfigBytes(), ExactCounts());

    private static byte[] ExactDavBytes() => Encoding.UTF8.GetBytes(ExactDavJson());

    private static byte[] ExactConfigBytes() => Encoding.UTF8.GetBytes(ExactConfigJson());

    private static string ExactDavJson() => $"[{string.Join(',', DavRows)}]";

    private static string ExactConfigJson() => $"[{string.Join(',', ConfigRows)}]";

    private static byte[] DavDocument(IEnumerable<string> rows) =>
        Encoding.UTF8.GetBytes($"[{string.Join(',', rows)}]");

    private static byte[] ConfigDocument(IEnumerable<string> rows) =>
        Encoding.UTF8.GetBytes($"[{string.Join(',', rows)}]");

    private static string ConfigRow(string name, string value) =>
        $"{{\"ConfigName\":{JsonString(name)},\"ConfigValue\":{JsonString(value)}}}";

    private static PostgreSqlApplicationRelationCount[] ExactCounts() =>
        RelationNames.Select(name => new PostgreSqlApplicationRelationCount(name, 0)).ToArray();

    private static string JsonString(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string[] ReplaceProperty(
        IReadOnlyList<string> sourceRows,
        int rowIndex,
        string propertyName,
        string replacementJson)
    {
        var rows = sourceRows.ToArray();
        var row = rows[rowIndex];
        var marker = $"\"{propertyName}\":";
        var markerIndex = row.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Approved fixture property {propertyName} was not found.");
        var valueStart = markerIndex + marker.Length;
        var valueEnd = ValueEnd(row, valueStart);
        rows[rowIndex] = row[..valueStart] + replacementJson + row[valueEnd..];
        return rows;
    }

    private static string[] RemoveProperty(
        IReadOnlyList<string> sourceRows,
        int rowIndex,
        string propertyName)
    {
        var rows = sourceRows.ToArray();
        var row = rows[rowIndex];
        var markerIndex = row.IndexOf($"\"{propertyName}\":", StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        var start = markerIndex > 1 && row[markerIndex - 1] == ','
            ? markerIndex - 1
            : markerIndex;
        var valueStart = markerIndex + propertyName.Length + 3;
        var end = ValueEnd(row, valueStart);
        if (start == markerIndex && end < row.Length && row[end] == ',') end++;
        rows[rowIndex] = row.Remove(start, end - start);
        return rows;
    }

    private static int ValueEnd(string row, int start)
    {
        if (row[start] != '"')
        {
            var delimiter = row.IndexOfAny([',', '}'], start);
            return delimiter < 0 ? row.Length : delimiter;
        }

        var escaped = false;
        for (var index = start + 1; index < row.Length; index++)
        {
            if (!escaped && row[index] == '"') return index + 1;
            escaped = !escaped && row[index] == '\\';
            if (row[index] != '\\') escaped = false;
        }

        throw new InvalidOperationException("Approved fixture string is unterminated.");
    }

    private static void AssertRejected(
        byte[] dav,
        byte[] config,
        IReadOnlyList<PostgreSqlApplicationRelationCount> counts,
        params string[] forbiddenValues)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var snapshot = new PostgreSqlFreshBootstrapSnapshot(dav, config, counts);
            PostgreSqlFreshBootstrapContract.Validate(snapshot);
        });

        Assert.Equal("PostgreSQL fresh-bootstrap validation failed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Empty(exception.Data);
        var rendered = exception.ToString();
        Assert.DoesNotContain(ApiKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(StrmKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, rendered, StringComparison.Ordinal);
        Assert.All(
            forbiddenValues,
            value => Assert.DoesNotContain(value, rendered, StringComparison.Ordinal));
    }

    private static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Property {name} is missing.");

    private static void AssertMethod(
        Type type,
        string name,
        Type returnType,
        params Type[] parameterTypes)
    {
        var method = type.GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            parameterTypes,
            modifiers: null);
        Assert.NotNull(method);
        Assert.Equal(returnType, method.ReturnType);
    }
}
