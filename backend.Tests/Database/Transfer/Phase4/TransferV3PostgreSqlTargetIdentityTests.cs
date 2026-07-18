using System.Net;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

public sealed class TransferV3PostgreSqlTargetIdentityTests
{
    public enum RequiredName
    {
        DatabaseName,
        SchemaName,
        RoleName,
    }

    public enum RequiredOid
    {
        DatabaseOid,
        SchemaOid,
        RoleOid,
    }

    public static TheoryData<string> AcceptedSystemIdentifiers => new()
    {
        "1",
        "9223372036854775807",
        "9223372036854775808",
        "18446744073709551615",
    };

    public static TheoryData<string?> RejectedSystemIdentifiers => new()
    {
        null,
        string.Empty,
        " ",
        "0",
        "+1",
        "-1",
        "01",
        "00",
        "1 ",
        " 1",
        "1.0",
        "1e3",
        "1_000",
        "18446744073709551616",
        "99999999999999999999",
        "100000000000000000000",
        "abc",
        "١",
    };

    public static TheoryData<RequiredName, string?> RejectedNames => new()
    {
        { RequiredName.DatabaseName, null },
        { RequiredName.DatabaseName, string.Empty },
        { RequiredName.DatabaseName, " " },
        { RequiredName.DatabaseName, "\t" },
        { RequiredName.SchemaName, null },
        { RequiredName.SchemaName, string.Empty },
        { RequiredName.SchemaName, " " },
        { RequiredName.SchemaName, "\t" },
        { RequiredName.RoleName, null },
        { RequiredName.RoleName, string.Empty },
        { RequiredName.RoleName, " " },
        { RequiredName.RoleName, "\t" },
    };

    [Fact]
    public void ValidateProjection_CapturesEveryFieldAndCanonicalizesTcpAddress()
    {
        var settings = CreateValidSettings();
        var postmasterStart = new DateTimeOffset(
            2026, 7, 14, 8, 9, 10, 123, TimeSpan.Zero).AddTicks(4567);
        var projection = CreateIdentityProjection(builder =>
        {
            builder.SystemIdentifier = "9223372036854775808";
            builder.PostmasterStartTimeUtc = postmasterStart;
            builder.ServerAddress = IPAddress.Parse("2001:0db8:0000:0000:0000:0000:0000:0001");
        });

        var identity = TransferV3PostgreSqlServerContract.ValidateProjection(
            in settings,
            in projection,
            "UTC");

        Assert.Equal("9223372036854775808", identity.SystemIdentifier);
        Assert.Equal(postmasterStart, identity.PostmasterStartTimeUtc);
        Assert.Equal("nzbdav", identity.DatabaseName);
        Assert.Equal(16_384U, identity.DatabaseOid);
        Assert.Equal("transfer_v3", identity.SchemaName);
        Assert.Equal(16_385U, identity.SchemaOid);
        Assert.Equal("nzbdav_owner", identity.RoleName);
        Assert.Equal(16_386U, identity.RoleOid);
        Assert.Equal("16.14", identity.ServerVersion);
        Assert.Equal(160_014, identity.ServerVersionNumber);
        Assert.False(identity.IsInRecovery);
        Assert.False(identity.DefaultTransactionReadOnly);
        Assert.False(identity.TransactionReadOnly);
        Assert.Equal("2001:db8::1", identity.ServerAddress);
        Assert.Equal(5432, identity.ServerPort);
    }

    [Theory]
    [MemberData(nameof(AcceptedSystemIdentifiers))]
    public void ValidateProjection_AcceptsCanonicalUnsignedSystemIdentifierBoundaries(
        string systemIdentifier)
    {
        var settings = CreateValidSettings();
        var projection = CreateIdentityProjection(
            builder => builder.SystemIdentifier = systemIdentifier);

        var identity = TransferV3PostgreSqlServerContract.ValidateProjection(
            in settings,
            in projection,
            "UTC");

        Assert.Equal(systemIdentifier, identity.SystemIdentifier);
    }

    [Theory]
    [MemberData(nameof(RejectedSystemIdentifiers))]
    public void ValidateProjection_RejectsNonCanonicalOrOutOfRangeSystemIdentifier(
        string? systemIdentifier)
    {
        var settings = CreateValidSettings();
        var projection = CreateIdentityProjection(
            builder => builder.SystemIdentifier = systemIdentifier);

        AssertRejected(() => TransferV3PostgreSqlServerContract.ValidateProjection(
            in settings,
            in projection,
            "UTC"));
    }

    [Fact]
    public void ValidateProjection_RequiresFiniteExactUtcPostmasterIncarnation()
    {
        DateTimeOffset?[] rejected =
        [
            null,
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.FromHours(4)),
        ];

        foreach (var postmasterStart in rejected)
        {
            var settings = CreateValidSettings();
            var projection = CreateIdentityProjection(
                builder => builder.PostmasterStartTimeUtc = postmasterStart);

            AssertRejected(() => TransferV3PostgreSqlServerContract.ValidateProjection(
                in settings,
                in projection,
                "UTC"));
        }
    }

    [Theory]
    [MemberData(nameof(RejectedNames))]
    public void ValidateProjection_RejectsNullOrBlankIdentityName(
        RequiredName name,
        string? value)
    {
        var settings = CreateValidSettings();
        var projection = CreateIdentityProjection(builder =>
        {
            switch (name)
            {
                case RequiredName.DatabaseName:
                    builder.DatabaseName = value;
                    break;
                case RequiredName.SchemaName:
                    builder.SchemaName = value;
                    break;
                case RequiredName.RoleName:
                    builder.RoleName = value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(name));
            }
        });

        AssertRejected(() => TransferV3PostgreSqlServerContract.ValidateProjection(
            in settings,
            in projection,
            "UTC"));
    }

    [Fact]
    public void ValidateProjection_PreservesNonblankQuotedIdentifierSpellingsExactly()
    {
        var settings = CreateValidSettings();
        var projection = CreateIdentityProjection(builder =>
        {
            builder.DatabaseName = " nzbdav ";
            builder.SchemaName = "transfer schema";
            builder.RoleName = "owner role";
        });

        var identity = TransferV3PostgreSqlServerContract.ValidateProjection(
            in settings,
            in projection,
            "UTC");

        Assert.Equal(" nzbdav ", identity.DatabaseName);
        Assert.Equal("transfer schema", identity.SchemaName);
        Assert.Equal("owner role", identity.RoleName);
    }

    [Theory]
    [InlineData(RequiredOid.DatabaseOid)]
    [InlineData(RequiredOid.SchemaOid)]
    [InlineData(RequiredOid.RoleOid)]
    public void ValidateProjection_RequiresEveryNonzeroOid(RequiredOid oid)
    {
        foreach (uint? rejectedValue in new uint?[] { null, 0 })
        {
            var settings = CreateValidSettings();
            var projection = CreateIdentityProjection(builder =>
            {
                switch (oid)
                {
                    case RequiredOid.DatabaseOid:
                        builder.DatabaseOid = rejectedValue;
                        break;
                    case RequiredOid.SchemaOid:
                        builder.SchemaOid = rejectedValue;
                        break;
                    case RequiredOid.RoleOid:
                        builder.RoleOid = rejectedValue;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(oid));
                }
            });

            AssertRejected(() => TransferV3PostgreSqlServerContract.ValidateProjection(
                in settings,
                in projection,
                "UTC"));
        }
    }

    [Fact]
    public void ValidateProjection_RequiresExactServerVersionAndPrimaryReadWriteServer()
    {
        Action<IdentityProjectionBuilder>[] mutations =
        [
            builder => builder.ServerVersion = null,
            builder => builder.ServerVersion = string.Empty,
            builder => builder.ServerVersion = " ",
            builder => builder.ServerVersion = "16.14 ",
            builder => builder.ServerVersion = "16.13",
            builder => builder.ServerVersion = "PostgreSQL 16.14",
            builder => builder.ServerVersionNumber = null,
            builder => builder.ServerVersionNumber = 0,
            builder => builder.ServerVersionNumber = -1,
            builder => builder.ServerVersionNumber = 160_013,
            builder => builder.ServerVersionNumber = 160_015,
            builder => builder.IsInRecovery = null,
            builder => builder.IsInRecovery = true,
        ];

        foreach (var mutate in mutations)
        {
            var settings = CreateValidSettings();
            var projection = CreateIdentityProjection(mutate);

            AssertRejected(() => TransferV3PostgreSqlServerContract.ValidateProjection(
                in settings,
                in projection,
                "UTC"));
        }
    }

    [Fact]
    public void ValidateProjection_AcceptsCanonicalIpv4Ipv6AndBothNullUnixSocketEndpoints()
    {
        var cases = new (IPAddress? Address, int? Port, string? ExpectedAddress)[]
        {
            (IPAddress.Parse("192.0.2.10"), 1, "192.0.2.10"),
            (IPAddress.Parse("2001:0db8:0000:0000:0000:0000:0000:0010"), 65_535,
                "2001:db8::10"),
            (null, null, null),
        };

        foreach (var (address, port, expectedAddress) in cases)
        {
            var settings = CreateValidSettings();
            var projection = CreateIdentityProjection(builder =>
            {
                builder.ServerAddress = address;
                builder.ServerPort = port;
            });

            var identity = TransferV3PostgreSqlServerContract.ValidateProjection(
                in settings,
                in projection,
                "UTC");

            Assert.Equal(expectedAddress, identity.ServerAddress);
            Assert.Equal(port, identity.ServerPort);
        }
    }

    [Fact]
    public void ValidateProjection_RejectsXorNullScopedIpv6AndOutOfRangeTcpPort()
    {
        var scopedAddress = new IPAddress(
            IPAddress.Parse("fe80::1").GetAddressBytes(),
            scopeid: 7);
        var cases = new (IPAddress? Address, int? Port)[]
        {
            (IPAddress.Parse("192.0.2.10"), null),
            (null, 5432),
            (scopedAddress, 5432),
            (IPAddress.Parse("192.0.2.10"), -1),
            (IPAddress.Parse("192.0.2.10"), 0),
            (IPAddress.Parse("192.0.2.10"), 65_536),
            (IPAddress.Parse("192.0.2.10"), int.MaxValue),
        };

        foreach (var (address, port) in cases)
        {
            var settings = CreateValidSettings();
            var projection = CreateIdentityProjection(builder =>
            {
                builder.ServerAddress = address;
                builder.ServerPort = port;
            });

            AssertRejected(() => TransferV3PostgreSqlServerContract.ValidateProjection(
                in settings,
                in projection,
                "UTC"));
        }
    }

    [Fact]
    public void TargetIdentityEqualityIncludesEveryCapturedField()
    {
        var settings = CreateValidSettings();
        var projection = CreateIdentityProjection();
        var identity = TransferV3PostgreSqlServerContract.ValidateProjection(
            in settings,
            in projection,
            "UTC");
        TransferV3PostgreSqlTargetIdentity[] changed =
        [
            identity with { SystemIdentifier = "1" },
            identity with { PostmasterStartTimeUtc = identity.PostmasterStartTimeUtc.AddTicks(1) },
            identity with { DatabaseName = "other_database" },
            identity with { DatabaseOid = identity.DatabaseOid + 1 },
            identity with { SchemaName = "other_schema" },
            identity with { SchemaOid = identity.SchemaOid + 1 },
            identity with { RoleName = "other_role" },
            identity with { RoleOid = identity.RoleOid + 1 },
            identity with { ServerVersion = "16.14-other" },
            identity with { ServerVersionNumber = identity.ServerVersionNumber + 1 },
            identity with { IsInRecovery = true },
            identity with { DefaultTransactionReadOnly = true },
            identity with { TransactionReadOnly = true },
            identity with { ServerAddress = "192.0.2.11" },
            identity with { ServerPort = identity.ServerPort + 1 },
        ];

        Assert.All(changed, candidate => Assert.NotEqual(identity, candidate));
        Assert.Equal(identity, identity with { });
    }

    private static TransferV3PostgreSqlServerSettingsProjection CreateValidSettings() => new(
        logMinMessages: "panic",
        logMinErrorStatement: "panic",
        logErrorVerbosity: "terse",
        logStatement: "none",
        logDuration: "off",
        logMinDurationStatement: "-1",
        logMinDurationSample: "-1",
        logTransactionSampleRate: "0",
        logParameterMaxLength: "0",
        logParameterMaxLengthOnError: "0",
        debugPrintParse: "off",
        debugPrintRewritten: "off",
        debugPrintPlan: "off",
        sharedPreloadLibraries: string.Empty,
        sessionPreloadLibraries: string.Empty,
        localPreloadLibraries: string.Empty,
        logDestination: "stderr",
        loggingCollector: "off",
        fsync: "on",
        fullPageWrites: "on",
        synchronousCommit: "on",
        clientEncoding: "UTF8",
        dateStyle: "ISO, MDY",
        sessionReplicationRole: "origin",
        temporarySchemaOid: 0,
        defaultTransactionReadOnly: "off",
        transactionReadOnly: "off",
        timeZone: "UTC",
        roleIsSuperuser: false,
        hasReadAllSettings: true);

    private static TransferV3PostgreSqlIdentityProjection CreateIdentityProjection(
        Action<IdentityProjectionBuilder>? configure = null)
    {
        var builder = new IdentityProjectionBuilder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    private static void AssertRejected(Func<object?> action)
    {
        Assert.NotNull(Record.Exception(action));
    }

    private sealed class IdentityProjectionBuilder
    {
        internal string? SystemIdentifier { get; set; } = "18446744073709551615";
        internal DateTimeOffset? PostmasterStartTimeUtc { get; set; } =
            new DateTimeOffset(2026, 7, 14, 8, 9, 10, TimeSpan.Zero);
        internal string? DatabaseName { get; set; } = "nzbdav";
        internal uint? DatabaseOid { get; set; } = 16_384;
        internal string? SchemaName { get; set; } = "transfer_v3";
        internal uint? SchemaOid { get; set; } = 16_385;
        internal string? RoleName { get; set; } = "nzbdav_owner";
        internal uint? RoleOid { get; set; } = 16_386;
        internal string? ServerVersion { get; set; } = "16.14";
        internal int? ServerVersionNumber { get; set; } = 160_014;
        internal bool? IsInRecovery { get; set; } = false;
        internal IPAddress? ServerAddress { get; set; } = IPAddress.Parse("2001:db8::1");
        internal int? ServerPort { get; set; } = 5432;

        internal TransferV3PostgreSqlIdentityProjection Build() => new(
            systemIdentifier: SystemIdentifier,
            postmasterStartTimeUtc: PostmasterStartTimeUtc,
            databaseName: DatabaseName,
            databaseOid: DatabaseOid,
            schemaName: SchemaName,
            schemaOid: SchemaOid,
            roleName: RoleName,
            roleOid: RoleOid,
            serverVersion: ServerVersion,
            serverVersionNumber: ServerVersionNumber,
            isInRecovery: IsInRecovery,
            serverAddress: ServerAddress,
            serverPort: ServerPort);
    }
}
