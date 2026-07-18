using System.ComponentModel;
using System.Reflection;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

[Collection(nameof(SqliteMigrationContractEnvironmentCollection))]
public sealed class TransferV3PostgreSqlTargetDescriptorTests
{
    private const string PasswordCanary = "descriptor-password-canary";
    private const string Schema = "transfer_v3";

    private static readonly string[] NpgsqlAmbientVariables =
    [
        "PGUSER",
        "PGPASSWORD",
        "PGPASSFILE",
        "PGSSLCERT",
        "PGSSLKEY",
        "PGSSLROOTCERT",
        "PGCLIENTENCODING",
        "PGTZ",
        "PGOPTIONS",
        "PGTARGETSESSIONATTRS",
        "PGSSLNEGOTIATION",
        "PGGSSENCMODE",
        "PGREQUIREAUTH",
        "PGAPPNAME",
    ];

    private static readonly string[] AllowedCanonicalKeyValues =
    [
        "Host",
        "Port",
        "Database",
        "Username",
        "Password",
        "Application Name",
        "Search Path",
        "Client Encoding",
        "Timezone",
        "SSL Mode",
        "SSL Negotiation",
        "GSS Encryption Mode",
        "Require Auth",
        "Channel Binding",
        "Persist Security Info",
        "Log Parameters",
        "Include Error Detail",
        "Include Failed Batched Command",
        "Pooling",
        "Enlist",
        "Load Balance Hosts",
        "Multiplexing",
        "Target Session Attributes",
        "Timeout",
        "Command Timeout",
        "Cancellation Timeout",
        "Options",
    ];

    private static readonly string[] DisallowedCanonicalKeyValues =
    [
        "Passfile",
        "Encoding",
        "SSL Certificate",
        "SSL Key",
        "SSL Password",
        "Root Certificate",
        "Check Certificate Revocation",
        "Kerberos Service Name",
        "Include Realm",
        "Minimum Pool Size",
        "Maximum Pool Size",
        "Connection Idle Lifetime",
        "Connection Pruning Interval",
        "Connection Lifetime",
        "Host Recheck Seconds",
        "Keepalive",
        "TCP Keepalive",
        "TCP Keepalive Time",
        "TCP Keepalive Interval",
        "Read Buffer Size",
        "Write Buffer Size",
        "Socket Receive Buffer Size",
        "Socket Send Buffer Size",
        "Max Auto Prepare",
        "Auto Prepare Min Usages",
        "No Reset On Close",
        "Replication Mode",
        "Array Nullability Mode",
        "Write Coalescing Buffer Threshold Bytes",
        "Load Table Composites",
        "Server Compatibility Mode",
        "Trust Server Certificate",
        "Internal Command Timeout",
    ];

    private static readonly (string Canonical, string Alias)[] SupportedAliasPairs =
    [
        ("Host", "Server"),
        ("Database", "DB"),
        ("Username", "User Name"),
        ("Username", "UserId"),
        ("Username", "User Id"),
        ("Username", "UID"),
        ("Password", "PSW"),
        ("Password", "PWD"),
    ];

    private static readonly (string Canonical, string Alias)[] DisallowedAliasPairs =
    [
        ("Kerberos Service Name", "Krbsrvname"),
        ("Connection Lifetime", "Load Balance Timeout"),
    ];

    public static TheoryData<string, object> UnsafeAllowedSettings => new()
    {
        { "Persist Security Info", true },
        { "Log Parameters", true },
        { "Include Error Detail", true },
        { "Include Failed Batched Command", true },
        { "Options", "-c search_path=public" },
        { "Options", " " },
        { "Options", "\t" },
        { "Target Session Attributes", "primary" },
        { "Load Balance Hosts", true },
        { "Multiplexing", true },
        { "Pooling", true },
        { "Enlist", true },
        { "Client Encoding", "LATIN1" },
        { "SSL Mode", "Prefer" },
        { "SSL Negotiation", "Direct" },
        { "GSS Encryption Mode", "Prefer" },
        { "Require Auth", "Password" },
        { "Channel Binding", "Prefer" },
    };

    public static TheoryData<string> DisallowedCanonicalKeys =>
        CreateTheoryData(DisallowedCanonicalKeyValues);

    public static TheoryData<string, string> SupportedAliases =>
        CreateTheoryData(SupportedAliasPairs);

    public static TheoryData<string, string> DisallowedAliases =>
        CreateTheoryData(DisallowedAliasPairs);

    public static TheoryData<string> AcceptedHosts => new()
    {
        "postgres.internal",
        "127.0.0.1",
        "::1",
        "2001:db8::1234",
        "[::1]",
        "[2001:db8::1234]",
        "/",
        "/tmp",
        "/tmp/nzbdav-postgresql",
        "@nzbdav-postgresql",
    };

    public static TheoryData<string> RejectedHosts => new()
    {
        "",
        " ",
        "postgres-a,postgres-b",
        "127.0.0.1,127.0.0.2",
        "postgres.internal:5432",
        "127.0.0.1:5432",
        "[::1]:5432",
        "[2001:db8::1234]:5432",
    };

    [Fact]
    public async Task Create_WithEveryExactAllowedSetting_BuildsAnUnopenedNormalizedOwnedDataSource()
    {
        using var environment = StrictEnvironment();
        var descriptor = TransferV3PostgreSqlTargetDescriptor.Create(
            BuildValidBuilder(includeOptionalSettings: true).ConnectionString);
        var dataSource = GetOwnedDataSource(descriptor);
        var settings = GetPrivateSettings(dataSource);

        Assert.Equal(Schema, descriptor.TargetSchema);
        Assert.Equal(TimeZoneInfo.Local.Id, descriptor.TimeZoneId);
        Assert.False(string.IsNullOrWhiteSpace(settings.Host));
        Assert.Equal(5432, settings.Port);
        Assert.Equal("nzbdav", settings.Database);
        Assert.Equal("nzbdav", settings.Username);
        Assert.False(string.IsNullOrWhiteSpace(settings.Password));
        Assert.Equal(TransferV3PostgreSqlTargetDescriptor.ApplicationName, settings.ApplicationName);
        Assert.Equal(Schema, settings.SearchPath);
        Assert.Equal("UTF8", settings.ClientEncoding);
        Assert.Equal(TimeZoneInfo.Local.Id, settings.Timezone);
        Assert.Equal(SslMode.Disable, settings.SslMode);
        Assert.Equal(SslNegotiation.Postgres, settings.SslNegotiation);
        Assert.Equal(GssEncryptionMode.Disable, settings.GssEncryptionMode);
        Assert.Equal("ScramSHA256", settings.RequireAuth);
        Assert.Equal(ChannelBinding.Disable, settings.ChannelBinding);
        Assert.False(settings.PersistSecurityInfo);
        Assert.False(settings.LogParameters);
        Assert.False(settings.IncludeErrorDetail);
        Assert.False(settings.IncludeFailedBatchedCommand);
        Assert.False(settings.Pooling);
        Assert.False(settings.Enlist);
        Assert.False(settings.LoadBalanceHosts);
        Assert.False(settings.Multiplexing);
        Assert.Equal("any", settings.TargetSessionAttributes);
        Assert.Equal(TransferV3PostgreSqlTargetDescriptor.ConnectionTimeoutSeconds, settings.Timeout);
        Assert.Equal(TransferV3PostgreSqlTargetDescriptor.CommandTimeoutSeconds, settings.CommandTimeout);
        Assert.Equal(TransferV3PostgreSqlTargetDescriptor.CancellationTimeoutMilliseconds,
            settings.CancellationTimeout);
        Assert.True(string.IsNullOrEmpty(settings.Options));

        // A password must remain privately available to the provider but never be exposed by its public string.
        Assert.DoesNotContain(PasswordCanary, dataSource.ConnectionString, StringComparison.Ordinal);
        await descriptor.DisposeAsync();
    }

    [Fact]
    public async Task Create_OmittedOptionalDefaults_NormalizesEveryFrozenValueExplicitly()
    {
        using var environment = StrictEnvironment();
        var descriptor = TransferV3PostgreSqlTargetDescriptor.Create(
            BuildValidBuilder(includeOptionalSettings: false).ConnectionString);
        var settings = GetPrivateSettings(GetOwnedDataSource(descriptor));

        Assert.False(settings.PersistSecurityInfo);
        Assert.False(settings.LogParameters);
        Assert.False(settings.IncludeErrorDetail);
        Assert.False(settings.IncludeFailedBatchedCommand);
        Assert.False(settings.Pooling);
        Assert.False(settings.Enlist);
        Assert.False(settings.LoadBalanceHosts);
        Assert.False(settings.Multiplexing);
        Assert.Equal("any", settings.TargetSessionAttributes);
        Assert.Equal(5, settings.Timeout);
        Assert.Equal(300, settings.CommandTimeout);
        Assert.Equal(2000, settings.CancellationTimeout);
        Assert.True(string.IsNullOrEmpty(settings.Options));

        await descriptor.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(SupportedAliases))]
    public async Task Create_ProviderSupportedAlias_CanonicalizesThenAcceptsExactSafeValue(
        string canonical,
        string alias)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        var value = builder[canonical];
        builder.Remove(canonical);
        var aliasInput = builder.ConnectionString + $";{alias}={value}";

        var descriptor = TransferV3PostgreSqlTargetDescriptor.Create(aliasInput);
        var settings = GetPrivateSettings(GetOwnedDataSource(descriptor));
        Assert.True(settings.ContainsKey(canonical));
        if (canonical == "Password")
            Assert.False(string.IsNullOrWhiteSpace(settings.Password));
        else
            Assert.Equal(value, settings[canonical]);

        await descriptor.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(UnsafeAllowedSettings))]
    public void Create_AllowedCanonicalKeyWithUnsafeValue_IsSanitizedArgumentFailure(
        string key,
        object value)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        builder[key] = value;

        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(builder.ConnectionString));
    }

    [Theory]
    [MemberData(nameof(DisallowedCanonicalKeys))]
    public void Create_EveryProviderCanonicalKeyOutsideExactAllowlist_IsRejected(string key)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        if (string.Equals(key, "Replication Mode", StringComparison.Ordinal))
        {
            AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(
                builder.ConnectionString + ";Replication Mode=Off"));
            return;
        }

        var property = Assert.Single(
            TypeDescriptor.GetProperties(typeof(NpgsqlConnectionStringBuilder))
                .Cast<PropertyDescriptor>(),
            candidate => string.Equals(candidate.DisplayName, key, StringComparison.Ordinal));
        var value = property.GetValue(new NpgsqlConnectionStringBuilder());
        builder[key] = value ?? "descriptor-disallowed-value";

        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(builder.ConnectionString));
    }

    [Theory]
    [MemberData(nameof(DisallowedAliases))]
    public void Create_ProviderAliasForDisallowedCanonicalKey_IsRejected(
        string canonical,
        string alias)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        var value = canonical == "Kerberos Service Name" ? "postgres" : "3600";

        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(
            builder.ConnectionString + $";{alias}={value}"));
    }

    [Fact]
    public void ReviewedCanonicalAndAliasTablesCoverTheExactProviderKeywordUniverse()
    {
        var mappings = GetProviderKeywordMappings();
        var reviewedCanonical = AllowedCanonicalKeyValues
            .Concat(DisallowedCanonicalKeyValues)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var providerCanonical = mappings
            .Select(mapping => mapping.Canonical)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(providerCanonical, reviewedCanonical);

        var reviewedAliases = SupportedAliasPairs
            .Concat(DisallowedAliasPairs)
            .OrderBy(pair => pair.Alias, StringComparer.Ordinal)
            .ThenBy(pair => pair.Canonical, StringComparer.Ordinal)
            .ToArray();
        var providerAliases = mappings
            .Where(mapping => mapping.Alias is not null)
            .Select(mapping => (mapping.Canonical, Alias: mapping.Alias!))
            .OrderBy(pair => pair.Alias, StringComparer.Ordinal)
            .ThenBy(pair => pair.Canonical, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(providerAliases, reviewedAliases);
    }

    [Theory]
    [InlineData("Integrated Security=true")]
    [InlineData("SSPI=true")]
    [InlineData("Kerberos Service Name=postgres")]
    [InlineData("Krbsrvname=postgres")]
    [InlineData("Include Realm=true")]
    public void Create_RejectsRawIntegratedSecurityAlternatives(string setting)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);

        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(
            builder.ConnectionString + ";" + setting));
    }

    [Theory]
    [MemberData(nameof(AcceptedHosts))]
    public async Task Create_AcceptsExactlyOneSupportedHostFormPlusSeparatePort(string host)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        builder.Host = host;

        var descriptor = TransferV3PostgreSqlTargetDescriptor.Create(builder.ConnectionString);
        var settings = GetPrivateSettings(GetOwnedDataSource(descriptor));
        Assert.Equal(host, settings.Host);
        Assert.Equal(5432, settings.Port);
        await descriptor.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(RejectedHosts))]
    public void Create_RejectsBlankMultiHostAndEmbeddedPortGrammar(string host)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        builder.Host = host;

        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(builder.ConnectionString));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public async Task Create_AcceptsPortBoundsWhenExplicit(int port)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        builder.Port = port;

        var descriptor = TransferV3PostgreSqlTargetDescriptor.Create(builder.ConnectionString);
        Assert.Equal(port, GetPrivateSettings(GetOwnedDataSource(descriptor)).Port);
        await descriptor.DisposeAsync();
    }

    [Fact]
    public void Create_RejectsMissingPortAndOutOfProtocolRange()
    {
        using var environment = StrictEnvironment();
        var missing = BuildValidBuilder(includeOptionalSettings: true);
        missing.Remove("Port");
        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(missing.ConnectionString));

        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(
            BuildValidBuilder(includeOptionalSettings: true).ConnectionString + ";Port=0"));
        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(
            BuildValidBuilder(includeOptionalSettings: true).ConnectionString + ";Port=65536"));
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Database")]
    [InlineData("Username")]
    [InlineData("Password")]
    [InlineData("Application Name")]
    [InlineData("Search Path")]
    [InlineData("Client Encoding")]
    [InlineData("Timezone")]
    [InlineData("SSL Mode")]
    [InlineData("SSL Negotiation")]
    [InlineData("GSS Encryption Mode")]
    [InlineData("Require Auth")]
    [InlineData("Channel Binding")]
    public void Create_RejectsEveryMissingRequiredSetting(string key)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        builder.Remove(key);

        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(builder.ConnectionString));
    }

    [Theory]
    [InlineData("Database")]
    [InlineData("Username")]
    [InlineData("Password")]
    public void Create_RejectsBlankRequiredIdentityValue(string key)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        builder[key] = string.Empty;

        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(builder.ConnectionString));
    }

    [Fact]
    public void Create_RejectsMissingOrNonfixedApplicationName()
    {
        using var environment = StrictEnvironment();
        var missing = BuildValidBuilder(includeOptionalSettings: true);
        missing.Remove("Application Name");
        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(missing.ConnectionString));

        var wrong = BuildValidBuilder(includeOptionalSettings: true);
        wrong.ApplicationName = "nzbdav-transfer-v3-phase4-other";
        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(wrong.ConnectionString));
    }

    [Theory]
    [InlineData(" transfer_v3")]
    [InlineData("transfer_v3 ")]
    [InlineData("\"transfer_v3\"")]
    [InlineData("transfer_v3,public")]
    [InlineData("$user")]
    public void Create_RejectsSchemaWhitespaceAliasQuotingAndMultiplicity(string searchPath)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        builder.SearchPath = searchPath;

        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(builder.ConnectionString));
    }

    [Theory]
    [InlineData("TRANSFER_V3")]
    [InlineData("other_safe_schema")]
    [InlineData("schema$1")]
    public async Task Create_AcceptsAnyExactSafeOperatorSelectedSchema(string searchPath)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        builder.SearchPath = searchPath;

        var descriptor = TransferV3PostgreSqlTargetDescriptor.Create(builder.ConnectionString);
        Assert.Equal(searchPath, descriptor.TargetSchema);
        Assert.Equal(
            searchPath,
            GetPrivateSettings(GetOwnedDataSource(descriptor)).SearchPath);
        await descriptor.DisposeAsync();
    }

    [Fact]
    public void Create_RejectsConnectionEnvironmentAndProcessTimezoneMismatchOrAlias()
    {
        using var environment = StrictEnvironment();
        var alias = string.Equals(TimeZoneInfo.Local.Id, "UTC", StringComparison.Ordinal)
            ? "Etc/UTC"
            : "UTC";

        var wrongConnection = BuildValidBuilder(includeOptionalSettings: true);
        wrongConnection.Timezone = alias;
        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(wrongConnection.ConnectionString));

        environment.Set(PostgreSqlConnectionPolicy.LegacyTimezoneVariable, alias);
        var matchingAlias = BuildValidBuilder(includeOptionalSettings: true);
        matchingAlias.Timezone = alias;
        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(matchingAlias.ConnectionString));

        environment.Set(PostgreSqlConnectionPolicy.LegacyTimezoneVariable, null);
        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(
            BuildValidBuilder(includeOptionalSettings: true).ConnectionString));
    }

    [Theory]
    [InlineData("Timeout", 0)]
    [InlineData("Timeout", 4)]
    [InlineData("Timeout", 6)]
    [InlineData("Command Timeout", 0)]
    [InlineData("Command Timeout", 299)]
    [InlineData("Command Timeout", 301)]
    [InlineData("Cancellation Timeout", -1)]
    [InlineData("Cancellation Timeout", 0)]
    [InlineData("Cancellation Timeout", 1999)]
    [InlineData("Cancellation Timeout", 2001)]
    public void Create_RejectsEveryAlternativeTimeoutIncludingInfiniteAndSkippedWait(string key, int value)
    {
        using var environment = StrictEnvironment();
        var builder = BuildValidBuilder(includeOptionalSettings: true);
        builder[key] = value;

        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(builder.ConnectionString));
    }

    [Fact]
    public async Task Create_AcceptsExactExplicitTimeouts()
    {
        using var environment = StrictEnvironment();
        var descriptor = TransferV3PostgreSqlTargetDescriptor.Create(
            BuildValidBuilder(includeOptionalSettings: true).ConnectionString);
        var settings = GetPrivateSettings(GetOwnedDataSource(descriptor));
        Assert.Equal(5, settings.Timeout);
        Assert.Equal(300, settings.CommandTimeout);
        Assert.Equal(2000, settings.CancellationTimeout);
        await descriptor.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(AmbientVariableNames))]
    public void Create_RejectsEveryNonemptyNpgsqlAmbientVariable(string variable)
    {
        using var environment = StrictEnvironment();
        environment.Set(variable, "ambient-canary");

        AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(
            BuildValidBuilder(includeOptionalSettings: true).ConnectionString));
    }

    [Fact]
    public async Task Create_AllowsHomeAndAppDataBecauseTheyAreRunnerOwnedRatherThanNpgsqlInputs()
    {
        using var environment = StrictEnvironment();
        environment.Set("HOME", Path.Combine(Path.GetTempPath(), "phase4-empty-home"));
        environment.Set("APPDATA", Path.Combine(Path.GetTempPath(), "phase4-empty-appdata"));

        var descriptor = TransferV3PostgreSqlTargetDescriptor.Create(
            BuildValidBuilder(includeOptionalSettings: true).ConnectionString);
        await descriptor.DisposeAsync();
    }

    [Fact]
    public void MetadataValidator_AcceptsOnlyExactRuntimeAssemblyIdentity()
    {
        TransferV3PostgreSqlTargetDescriptor.ValidateNpgsqlAssemblyIdentity(
            new Version(10, 0, 3, 0),
            "10.0.3+d3768398c17877b3a916c3c4d87e8e11698991fc");
    }

    [Theory]
    [MemberData(nameof(RejectedAssemblyIdentities))]
    public void MetadataValidator_RejectsNullWrongMalformedPrereleaseAndDifferentBuild(
        Version? version,
        string? informationalVersion)
    {
        AssertUnexpected(() => TransferV3PostgreSqlTargetDescriptor.ValidateNpgsqlAssemblyIdentity(
            version,
            informationalVersion));
    }

    [Fact]
    public void RuntimeNpgsqlAssemblyHasTheExactReviewedIdentity()
    {
        var assembly = typeof(NpgsqlDataSource).Assembly;
        TransferV3PostgreSqlTargetDescriptor.ValidateNpgsqlAssemblyIdentity(
            assembly.GetName().Version,
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    }

    [Fact]
    public async Task DisposeAsync_IsSequentiallyIdempotentAndDisposesTheExactOwnedDataSource()
    {
        using var environment = StrictEnvironment();
        var descriptor = TransferV3PostgreSqlTargetDescriptor.Create(
            BuildValidBuilder(includeOptionalSettings: true).ConnectionString);
        var dataSource = GetOwnedDataSource(descriptor);
        var disposed = Assert.IsAssignableFrom<FieldInfo>(typeof(NpgsqlDataSource).GetField(
            "_isDisposed",
            BindingFlags.Instance | BindingFlags.NonPublic));

        Assert.Equal(0, Assert.IsType<int>(disposed.GetValue(dataSource)));
        await descriptor.DisposeAsync();
        Assert.Equal(1, Assert.IsType<int>(disposed.GetValue(dataSource)));
        await descriptor.DisposeAsync();
        Assert.Equal(1, Assert.IsType<int>(disposed.GetValue(dataSource)));
    }

    [Fact]
    public void Create_ParseAndPolicyFailuresNeverEchoTheSecretBearingInput()
    {
        using var environment = StrictEnvironment();
        var malformed = "Host=unopened.invalid;Password=secret-that-must-not-echo;Unsupported Secret Key=value";

        var exception = AssertArgument(() => TransferV3PostgreSqlTargetDescriptor.Create(malformed));
        Assert.Equal("Transfer-v3 Phase 4 failed.", exception.Message);
        Assert.DoesNotContain("secret-that-must-not-echo", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(malformed, exception.ToString(), StringComparison.Ordinal);
    }

    public static IEnumerable<object?[]> AmbientVariableNames() =>
        NpgsqlAmbientVariables.Select(value => new object?[] { value });

    public static IEnumerable<object?[]> RejectedAssemblyIdentities()
    {
        const string exact = "10.0.3+d3768398c17877b3a916c3c4d87e8e11698991fc";
        yield return [null, exact];
        yield return [new Version(10, 0, 3), exact];
        yield return [new Version(10, 0, 2, 0), exact];
        yield return [new Version(10, 0, 3, 1), exact];
        yield return [new Version(11, 0, 3, 0), exact];
        yield return [new Version(10, 0, 3, 0), null];
        yield return [new Version(10, 0, 3, 0), string.Empty];
        yield return [new Version(10, 0, 3, 0), "not-an-informational-version"];
        yield return [new Version(10, 0, 3, 0), "10.0.3-preview.1+d3768398c17877b3a916c3c4d87e8e11698991fc"];
        yield return [new Version(10, 0, 3, 0), "10.0.3+different-build"];
    }

    private static IReadOnlyList<(string Canonical, string? Alias)>
        GetProviderKeywordMappings()
    {
        var mappings = new List<(string Canonical, string? Alias)>();
        foreach (var property in typeof(NpgsqlConnectionStringBuilder).GetProperties(
                     BindingFlags.Instance
                     | BindingFlags.Public
                     | BindingFlags.NonPublic))
        {
            var marker = property.CustomAttributes.SingleOrDefault(attribute =>
                string.Equals(
                    attribute.AttributeType.Name,
                    "NpgsqlConnectionStringPropertyAttribute",
                    StringComparison.Ordinal));
            if (marker is null)
                continue;

            var displayName = property.GetCustomAttribute<DisplayNameAttribute>()
                              ?? throw new InvalidOperationException(
                                  $"Provider property '{property.Name}' has no display name.");
            mappings.Add((displayName.DisplayName, null));

            if (marker.ConstructorArguments.Count != 1
                || marker.ConstructorArguments[0].Value
                is not IEnumerable<CustomAttributeTypedArgument> aliases)
            {
                continue;
            }

            foreach (var alias in aliases)
            {
                mappings.Add((
                    displayName.DisplayName,
                    Assert.IsType<string>(alias.Value)));
            }
        }

        return mappings;
    }

    private static TheoryData<string> CreateTheoryData(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (var value in values)
            data.Add(value);
        return data;
    }

    private static TheoryData<string, string> CreateTheoryData(
        IEnumerable<(string Canonical, string Alias)> values)
    {
        var data = new TheoryData<string, string>();
        foreach (var (canonical, alias) in values)
            data.Add(canonical, alias);
        return data;
    }

    private static NpgsqlConnectionStringBuilder BuildValidBuilder(bool includeOptionalSettings)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "unopened.invalid",
            Port = 5432,
            Database = "nzbdav",
            Username = "nzbdav",
            Password = PasswordCanary,
            ApplicationName = TransferV3PostgreSqlTargetDescriptor.ApplicationName,
            SearchPath = Schema,
            ClientEncoding = "UTF8",
            Timezone = TimeZoneInfo.Local.Id,
            SslMode = SslMode.Disable,
            SslNegotiation = SslNegotiation.Postgres,
            GssEncryptionMode = GssEncryptionMode.Disable,
            RequireAuth = "ScramSHA256",
            ChannelBinding = ChannelBinding.Disable,
        };

        if (!includeOptionalSettings)
            return builder;

        builder.PersistSecurityInfo = false;
        builder.LogParameters = false;
        builder.IncludeErrorDetail = false;
        builder.IncludeFailedBatchedCommand = false;
        builder.Pooling = false;
        builder.Enlist = false;
        builder.LoadBalanceHosts = false;
        builder.Multiplexing = false;
        builder.TargetSessionAttributes = "Any";
        builder.Timeout = 5;
        builder.CommandTimeout = 300;
        builder.CancellationTimeout = 2000;
        builder.Options = string.Empty;
        return builder;
    }

    private static NpgsqlDataSource GetOwnedDataSource(
        TransferV3PostgreSqlTargetDescriptor descriptor)
    {
        var field = Assert.Single(
            typeof(TransferV3PostgreSqlTargetDescriptor)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            candidate => candidate.FieldType == typeof(NpgsqlDataSource));
        return Assert.IsAssignableFrom<NpgsqlDataSource>(field.GetValue(descriptor));
    }

    private static NpgsqlConnectionStringBuilder GetPrivateSettings(NpgsqlDataSource dataSource)
    {
        var property = Assert.IsAssignableFrom<PropertyInfo>(typeof(NpgsqlDataSource).GetProperty(
            "Settings",
            BindingFlags.Instance | BindingFlags.NonPublic));
        return Assert.IsType<NpgsqlConnectionStringBuilder>(property.GetValue(dataSource));
    }

    private static TransferV3Phase4Exception AssertArgument(Action action) =>
        AssertCode("phase4-argument", action);

    private static TransferV3Phase4Exception AssertUnexpected(Action action) =>
        AssertCode("phase4-unexpected", action);

    private static TransferV3Phase4Exception AssertCode(string code, Action action)
    {
        var exception = Assert.Throws<TransferV3Phase4Exception>(action);
        Assert.Equal(code, exception.Code);
        Assert.Equal("Transfer-v3 Phase 4 failed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Null(exception.SqlState);
        Assert.Empty(exception.SecondaryCodes);
        return exception;
    }

    private static EnvironmentScope StrictEnvironment()
    {
        var environment = new EnvironmentScope();
        foreach (var variable in NpgsqlAmbientVariables)
            environment.Set(variable, null);
        environment.Set(PostgreSqlConnectionPolicy.LegacyTimezoneVariable, TimeZoneInfo.Local.Id);
        return environment;
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

        internal void Set(string name, string? value)
        {
            if (!_original.ContainsKey(name))
                _original.Add(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
