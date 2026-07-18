using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using backend.Tests.Database;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Npgsql;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

public sealed class TransferV3PostgreSqlServerContractTests
{
    private const string ServerContractSourcePath =
        "backend/Database/Transfer/Phase4/TransferV3PostgreSqlServerContract.cs";

    public enum StringSetting
    {
        LogMinMessages,
        LogMinErrorStatement,
        LogErrorVerbosity,
        LogStatement,
        LogDuration,
        LogMinDurationStatement,
        LogMinDurationSample,
        LogTransactionSampleRate,
        LogParameterMaxLength,
        LogParameterMaxLengthOnError,
        DebugPrintParse,
        DebugPrintRewritten,
        DebugPrintPlan,
        SharedPreloadLibraries,
        SessionPreloadLibraries,
        LocalPreloadLibraries,
        LogDestination,
        LoggingCollector,
        Fsync,
        FullPageWrites,
        SynchronousCommit,
        ClientEncoding,
        DateStyle,
        SessionReplicationRole,
        DefaultTransactionReadOnly,
        TransactionReadOnly,
        TimeZone,
    }

    public static TheoryData<string> CanonicalIsoDateStyles => new()
    {
        "ISO, MDY",
        "ISO, DMY",
        "ISO, YMD",
    };

    public static IEnumerable<object?[]> InvalidStringSettingValues()
    {
        foreach (var setting in Enum.GetValues<StringSetting>())
        {
            var expected = ExpectedValue(setting);
            var alternatives = new HashSet<string?>(StringComparer.Ordinal)
            {
                null,
                "unsafe",
                " " + expected,
                expected + " ",
                "\t" + expected,
                expected + "\t",
                UnsafeAlternative(setting),
            };

            var caseChanged = SwapAsciiCase(expected);
            if (!string.Equals(caseChanged, expected, StringComparison.Ordinal))
                alternatives.Add(caseChanged);

            foreach (var alternative in alternatives)
                yield return [setting, alternative];
        }
    }

    [Fact]
    public void ValidateAndCapturePreservesOriginalAndAddsTransactionBoundOverload()
    {
        var overloads = typeof(TransferV3PostgreSqlServerContract)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == "ValidateAndCaptureAsync")
            .OrderBy(method => method.GetParameters().Length)
            .ToArray();

        Assert.Collection(
            overloads,
            method => Assert.Equal(
                [
                    typeof(NpgsqlConnection),
                    typeof(string),
                    typeof(int),
                    typeof(CancellationToken),
                ],
                method.GetParameters().Select(parameter => parameter.ParameterType)),
            method => Assert.Equal(
                [
                    typeof(NpgsqlConnection),
                    typeof(NpgsqlTransaction),
                    typeof(string),
                    typeof(int),
                    typeof(CancellationToken),
                ],
                method.GetParameters().Select(parameter => parameter.ParameterType)));
    }

    [Fact]
    public void TransactionBoundValidateAndCaptureProvesOwnershipAndBindsTheCommand()
    {
        var source = File.ReadAllText(
            SqliteContractTestSupport.AbsolutePath(ServerContractSourcePath));
        var root = CSharpSyntaxTree.ParseText(source, path: ServerContractSourcePath).GetRoot();
        var wrapper = Assert.Single(
            root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            declaration => declaration.Identifier.ValueText == "ValidateAndCaptureAsync"
                           && declaration.ParameterList.Parameters.Count == 5);
        var ownership = Assert.Single(
            wrapper.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ValidateTransactionContext");
        var delegation = Assert.Single(
            wrapper.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ValidateAndCaptureCoreAsync");
        Assert.Contains(
            delegation.ArgumentList.Arguments,
            argument => argument.Expression.ToString() == "transaction");

        var core = Assert.Single(
            root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            declaration => declaration.Identifier.ValueText == "ValidateAndCaptureCoreAsync");
        var nodes = core.DescendantNodes().ToArray();
        var transactionAssignment = Assert.Single(
            nodes.OfType<AssignmentExpressionSyntax>(),
            assignment => assignment.Left.ToString() == "command.Transaction");
        var execution = Assert.Single(
            nodes.OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ExecuteReaderAsync");

        Assert.Equal("transaction", transactionAssignment.Right.ToString());
        Assert.True(ownership.SpanStart < delegation.SpanStart);
        Assert.True(transactionAssignment.SpanStart < execution.SpanStart);
    }

    [Fact]
    public void ProjectionSeamsAreOrdinaryReadonlyStructsWithoutGeneratedValueFormatting()
    {
        AssertOrdinaryReadonlyStruct(
            typeof(TransferV3PostgreSqlServerSettingsProjection),
            [
                "ClientEncoding",
                "DateStyle",
                "DebugPrintParse",
                "DebugPrintPlan",
                "DebugPrintRewritten",
                "DefaultTransactionReadOnly",
                "Fsync",
                "FullPageWrites",
                "HasReadAllSettings",
                "LocalPreloadLibraries",
                "LogDestination",
                "LogDuration",
                "LoggingCollector",
                "LogErrorVerbosity",
                "LogMinDurationSample",
                "LogMinDurationStatement",
                "LogMinErrorStatement",
                "LogMinMessages",
                "LogParameterMaxLength",
                "LogParameterMaxLengthOnError",
                "LogStatement",
                "LogTransactionSampleRate",
                "RoleIsSuperuser",
                "SessionPreloadLibraries",
                "SessionReplicationRole",
                "SharedPreloadLibraries",
                "SynchronousCommit",
                "TemporarySchemaOid",
                "TimeZone",
                "TransactionReadOnly",
            ]);
        AssertOrdinaryReadonlyStruct(
            typeof(TransferV3PostgreSqlIdentityProjection),
            [
                "DatabaseName",
                "DatabaseOid",
                "IsInRecovery",
                "PostmasterStartTimeUtc",
                "RoleName",
                "RoleOid",
                "SchemaName",
                "SchemaOid",
                "ServerAddress",
                "ServerPort",
                "ServerVersion",
                "ServerVersionNumber",
                "SystemIdentifier",
            ]);
    }

    [Theory]
    [MemberData(nameof(CanonicalIsoDateStyles))]
    public void ValidateProjection_AcceptsEveryExactCanonicalIsoDateStyle(string dateStyle)
    {
        var settings = CreateSettings(StringSetting.DateStyle, dateStyle);
        var projection = CreateIdentityProjection();

        var identity = TransferV3PostgreSqlServerContract.ValidateProjection(
            in settings,
            in projection,
            "UTC");

        Assert.Equal(dateStyle, settings.DateStyle);
        Assert.Equal("18446744073709551615", identity.SystemIdentifier);
        Assert.False(identity.DefaultTransactionReadOnly);
        Assert.False(identity.TransactionReadOnly);
    }

    [Theory]
    [MemberData(nameof(InvalidStringSettingValues))]
    public void ValidateProjection_RejectsEveryNonExactStringSetting(
        StringSetting setting,
        string? value)
    {
        var settings = CreateSettings(setting, value);
        var projection = CreateIdentityProjection();

        AssertRejected(() => TransferV3PostgreSqlServerContract.ValidateProjection(
            in settings,
            in projection,
            "UTC"));
    }

    [Fact]
    public void ValidateProjection_RequiresNoTemporarySchema()
    {
        foreach (uint? temporarySchemaOid in new uint?[] { null, 1, uint.MaxValue })
        {
            var settings = CreateSettings(temporarySchemaOid: temporarySchemaOid);
            var projection = CreateIdentityProjection();

            AssertRejected(() => TransferV3PostgreSqlServerContract.ValidateProjection(
                in settings,
                in projection,
                "UTC"));
        }
    }

    [Fact]
    public void ValidateProjection_RequiresANonSuperuserWithImmediatelyUsableReadAllSettings()
    {
        var rejectedRoleFacts = new (bool? IsSuperuser, bool? HasReadAllSettings)[]
        {
            (null, true),
            (true, true),
            (false, null),
            (false, false),
            (true, false),
        };

        foreach (var (isSuperuser, hasReadAllSettings) in rejectedRoleFacts)
        {
            var settings = CreateSettings(
                roleIsSuperuser: isSuperuser,
                hasReadAllSettings: hasReadAllSettings);
            var projection = CreateIdentityProjection();

            AssertRejected(() => TransferV3PostgreSqlServerContract.ValidateProjection(
                in settings,
                in projection,
                "UTC"));
        }
    }

    [Fact]
    public void ValidateProjection_RequiresExactNonblankExpectedTimeZone()
    {
        var projection = CreateIdentityProjection();
        var utcSettings = CreateSettings();
        var dubaiSettings = CreateSettings(StringSetting.TimeZone, "Asia/Dubai");

        foreach (var invalidExpected in new string?[] { null, string.Empty, " ", "utc", "UTC " })
        {
            AssertRejected(() => TransferV3PostgreSqlServerContract.ValidateProjection(
                in utcSettings,
                in projection,
                invalidExpected!));
        }

        AssertRejected(() => TransferV3PostgreSqlServerContract.ValidateProjection(
            in dubaiSettings,
            in projection,
            "UTC"));
    }

    [Fact]
    public void SettingsIdentitySqlIsOneFixedParameterFreeReadOnlyPreflight()
    {
        var field = Assert.IsAssignableFrom<FieldInfo>(
            typeof(TransferV3PostgreSqlServerContract).GetField(
                "SettingsIdentitySql",
                BindingFlags.Static | BindingFlags.NonPublic));
        Assert.True(field.IsLiteral);
        Assert.True(field.IsPrivate);
        var sql = Assert.IsType<string>(field.GetRawConstantValue());
        var normalized = Regex.Replace(sql, @"\s+", " ").Trim();

        Assert.StartsWith("SELECT ", normalized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("}", sql, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"@[A-Za-z_]", sql);
        Assert.DoesNotMatch(@"\$[0-9]+", sql);
        Assert.DoesNotMatch(@"(?<!:):[A-Za-z_]", sql);
        Assert.DoesNotMatch(
            @"(?i)\b(SET|RESET|DISCARD|INSERT|UPDATE|DELETE|MERGE|COPY|CALL|DO|ALTER|CREATE|DROP|TRUNCATE)\b",
            sql);
        Assert.DoesNotMatch(
            @"(?i)\b(manifest|digest|state|row|key|uuid|path|copy)\b",
            sql);

        foreach (var setting in ExpectedSqlSettings())
        {
            Assert.Contains(
                $"current_setting('{setting}')",
                normalized,
                StringComparison.Ordinal);
        }

        Assert.Contains("pg_my_temp_schema()", normalized, StringComparison.Ordinal);
        Assert.Contains("pg_has_role(current_user, 'pg_read_all_settings', 'USAGE')", normalized,
            StringComparison.Ordinal);
        Assert.Contains("rolsuper", normalized, StringComparison.Ordinal);
        Assert.Contains("pg_control_system()", normalized, StringComparison.Ordinal);
        Assert.Contains("18446744073709551616", normalized, StringComparison.Ordinal);
        Assert.Matches(
            @"(?is)\bCASE\s+WHEN\s+[^;]*system_identifier\s*<\s*0\s+THEN\s+[^;]*system_identifier\s*::\s*numeric\s*\+\s*18446744073709551616",
            sql);
        Assert.Contains("pg_postmaster_start_time()", normalized, StringComparison.Ordinal);
        Assert.Contains("current_database()", normalized, StringComparison.Ordinal);
        Assert.Contains("current_schema()", normalized, StringComparison.Ordinal);
        Assert.Contains("current_user", normalized, StringComparison.Ordinal);
        Assert.Contains("pg_is_in_recovery()", normalized, StringComparison.Ordinal);
        Assert.Contains("inet_server_addr()", normalized, StringComparison.Ordinal);
        Assert.Contains("inet_server_port()", normalized, StringComparison.Ordinal);
        Assert.Matches(
            @"current_setting\('server_version_num'\)\s*::\s*integer",
            normalized);
    }

    [Fact]
    public void SettingsIdentitySqlProjectionOrderAndTypedOrdinalReadsAreExact()
    {
        var expected = ExpectedProjectionColumns();
        Assert.Equal(43, expected.Length);
        Assert.Equal(Enumerable.Range(0, 43), expected.Select(column => column.Ordinal));

        var sqlField = Assert.IsAssignableFrom<FieldInfo>(
            typeof(TransferV3PostgreSqlServerContract).GetField(
                "SettingsIdentitySql",
                BindingFlags.Static | BindingFlags.NonPublic));
        var sql = Assert.IsType<string>(sqlField.GetRawConstantValue());
        Assert.Equal(
            expected.Select(column => column.SqlExpression),
            ParseTopLevelSelectExpressions(sql));

        var source = File.ReadAllText(
            SqliteContractTestSupport.AbsolutePath(ServerContractSourcePath));
        var method = CSharpSyntaxTree.ParseText(source, path: ServerContractSourcePath)
            .GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(candidate => candidate.Identifier.ValueText
                == "ValidateAndCaptureCoreAsync");
        var projectionCreations = method.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => creation.Type.ToString() is
                nameof(TransferV3PostgreSqlServerSettingsProjection)
                or nameof(TransferV3PostgreSqlIdentityProjection))
            .ToDictionary(
                creation => creation.Type.ToString(),
                StringComparer.Ordinal);
        Assert.Equal(2, projectionCreations.Count);

        var observedOrdinals = new List<int>(expected.Length);
        foreach (var column in expected)
        {
            var creation = projectionCreations[column.ProjectionType];
            var arguments = Assert.IsType<ArgumentListSyntax>(creation.ArgumentList).Arguments;
            var argument = Assert.Single(
                arguments,
                candidate => candidate.NameColon?.Name.Identifier.ValueText
                    == column.NamedArgument);
            var read = Assert.IsType<InvocationExpressionSyntax>(argument.Expression);

            var (helper, genericType) = read.Expression switch
            {
                IdentifierNameSyntax identifier =>
                    (identifier.Identifier.ValueText, (string?)null),
                GenericNameSyntax generic =>
                    (generic.Identifier.ValueText,
                        Assert.Single(generic.TypeArgumentList.Arguments).ToString()),
                _ => throw new Xunit.Sdk.XunitException(
                    $"Unexpected projection read expression '{read.Expression}'."),
            };
            Assert.Equal(column.ReadHelper, helper);
            Assert.Equal(column.GenericType, genericType);
            Assert.Collection(
                read.ArgumentList.Arguments,
                reader => Assert.Equal("reader", reader.Expression.ToString()),
                ordinal => Assert.Equal(
                    column.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ordinal.Expression.ToString()));
            observedOrdinals.Add(column.Ordinal);
        }

        var projectionReads = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => ProjectionReadHelper(invocation) is not null)
            .ToArray();
        Assert.Equal(43, projectionReads.Length);
        Assert.Equal(
            Enumerable.Range(0, 43),
            observedOrdinals.OrderBy(ordinal => ordinal));
        foreach (var (projectionType, creation) in projectionCreations)
        {
            var expectedArguments = expected
                .Where(column => column.ProjectionType == projectionType)
                .Select(column => column.NamedArgument)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var actualArguments = creation.ArgumentList!.Arguments
                .Select(argument => Assert.IsType<IdentifierNameSyntax>(
                    argument.NameColon?.Name).Identifier.ValueText)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedArguments, actualArguments);
        }
    }

    [Fact]
    public void ValidateAndCaptureAssignsPositiveTimeoutAndProvesExactlyOneCompleteRow()
    {
        var source = File.ReadAllText(
            SqliteContractTestSupport.AbsolutePath(ServerContractSourcePath));
        var root = CSharpSyntaxTree.ParseText(source, path: ServerContractSourcePath).GetRoot();
        var contract = Assert.Single(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            declaration => declaration.Identifier.ValueText
                == "TransferV3PostgreSqlServerContract");
        var method = Assert.Single(
            contract.Members.OfType<MethodDeclarationSyntax>(),
            declaration => declaration.Identifier.ValueText == "ValidateAndCaptureCoreAsync");
        var nodes = method.DescendantNodes().ToArray();

        var timeoutGuard = Assert.Single(
            nodes.OfType<IfStatementSyntax>(),
            statement => statement.Condition.ToString().Contains(
                "commandTimeoutSeconds",
                StringComparison.Ordinal));
        var firstConnectionUse = Assert.Single(
            nodes.Where(node => node is IdentifierNameSyntax identifier
                                && identifier.Identifier.ValueText == "connection")
                .OrderBy(node => node.SpanStart)
                .Take(1));
        Assert.True(timeoutGuard.SpanStart < firstConnectionUse.SpanStart);

        var commandTextAssignment = Assert.Single(
            nodes.OfType<AssignmentExpressionSyntax>(),
            assignment => assignment.Left is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "CommandText"
            });
        Assert.Equal("SettingsIdentitySql", commandTextAssignment.Right.ToString());

        var timeoutAssignment = Assert.Single(
            nodes.OfType<AssignmentExpressionSyntax>(),
            assignment => assignment.Left is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "CommandTimeout"
            });
        Assert.Equal("commandTimeoutSeconds", timeoutAssignment.Right.ToString());

        Assert.DoesNotContain(
            nodes.OfType<MemberAccessExpressionSyntax>(),
            access => access.Name.Identifier.ValueText == "Parameters");
        Assert.DoesNotContain(
            nodes.OfType<MemberAccessExpressionSyntax>(),
            access => access.Name.Identifier.ValueText == "SingleRow");
        Assert.Single(
            nodes.OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ExecuteReaderAsync");
        Assert.Equal(
            2,
            nodes.OfType<InvocationExpressionSyntax>()
                .Count(invocation => InvocationName(invocation) == "ReadAsync"));
        Assert.Contains(
            nodes.OfType<MemberAccessExpressionSyntax>(),
            access => access.Name.Identifier.ValueText == "FieldCount");
        Assert.Contains(
            nodes.OfType<LiteralExpressionSyntax>(),
            literal => literal.Token.ValueText == "43");

        var execute = Assert.Single(
            nodes.OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ExecuteReaderAsync");
        Assert.True(commandTextAssignment.SpanStart < execute.SpanStart);
        Assert.True(timeoutAssignment.SpanStart < execute.SpanStart);
    }

    [Fact]
    public void ValidateAndCaptureSanitizesPrimaryBeforeExplicitPreservingDisposal()
    {
        var source = File.ReadAllText(
            SqliteContractTestSupport.AbsolutePath(ServerContractSourcePath));
        var method = CSharpSyntaxTree.ParseText(source, path: ServerContractSourcePath)
            .GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(candidate => candidate.Identifier.ValueText
                == "ValidateAndCaptureCoreAsync");

        Assert.DoesNotContain(
            method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(),
            declaration => declaration.AwaitKeyword.RawKind != 0
                           && declaration.UsingKeyword.RawKind != 0);
        var disposal = Assert.Single(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation)
                == "DisposeReaderThenCommandAsync");
        Assert.Equal(
            ["reader", "command", "primaryFailure"],
            disposal.ArgumentList.Arguments.Select(argument => argument.Expression.ToString()));
        var primaryCapture = Assert.Single(
            method.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            assignment => assignment.Left.ToString() == "primaryFailure"
                          && assignment.Right.ToString().Contains(
                              "TransferV3Phase4FailureMapper.Sanitize",
                              StringComparison.Ordinal));
        Assert.True(primaryCapture.SpanStart < disposal.SpanStart);
    }

    [Fact]
    public async Task ValidateAndCapture_MapsAClosedPostOpenConnectionToTheCommandBoundary()
    {
        await using var connection = new NpgsqlConnection();

        var failure = await Assert.ThrowsAsync<TransferV3Phase4Exception>(() =>
            TransferV3PostgreSqlServerContract.ValidateAndCaptureAsync(
                connection,
                "UTC",
                300,
                CancellationToken.None));

        Assert.Equal("phase4-postgresql-command", failure.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ValidateAndCapture_RejectsInvalidTimeoutBeforeConnectionState(
        int commandTimeoutSeconds)
    {
        await using var connection = new NpgsqlConnection();

        var failure = await Assert.ThrowsAsync<TransferV3Phase4Exception>(() =>
            TransferV3PostgreSqlServerContract.ValidateAndCaptureAsync(
                connection,
                "UTC",
                commandTimeoutSeconds,
                CancellationToken.None));

        Assert.Equal("phase4-argument", failure.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task ValidateAndCapture_RejectsInvalidTimeZoneBeforeConnectionState(
        string expectedTimeZoneId)
    {
        await using var connection = new NpgsqlConnection();

        var failure = await Assert.ThrowsAsync<TransferV3Phase4Exception>(() =>
            TransferV3PostgreSqlServerContract.ValidateAndCaptureAsync(
                connection,
                expectedTimeZoneId,
                300,
                CancellationToken.None));

        Assert.Equal("phase4-argument", failure.Code);
    }

    [Fact]
    public async Task ValidateAndCapture_RejectsNullConnectionBeforeProviderAccess()
    {
        var failure = await Assert.ThrowsAsync<TransferV3Phase4Exception>(() =>
            TransferV3PostgreSqlServerContract.ValidateAndCaptureAsync(
                null!,
                "UTC",
                300,
                CancellationToken.None));

        Assert.Equal("phase4-argument", failure.Code);
    }

    private static TransferV3PostgreSqlServerSettingsProjection CreateSettings(
        StringSetting? changedSetting = null,
        string? changedValue = null,
        uint? temporarySchemaOid = 0,
        bool? roleIsSuperuser = false,
        bool? hasReadAllSettings = true)
    {
        string? Value(StringSetting setting) => changedSetting == setting
            ? changedValue
            : ExpectedValue(setting);

        return new TransferV3PostgreSqlServerSettingsProjection(
            logMinMessages: Value(StringSetting.LogMinMessages),
            logMinErrorStatement: Value(StringSetting.LogMinErrorStatement),
            logErrorVerbosity: Value(StringSetting.LogErrorVerbosity),
            logStatement: Value(StringSetting.LogStatement),
            logDuration: Value(StringSetting.LogDuration),
            logMinDurationStatement: Value(StringSetting.LogMinDurationStatement),
            logMinDurationSample: Value(StringSetting.LogMinDurationSample),
            logTransactionSampleRate: Value(StringSetting.LogTransactionSampleRate),
            logParameterMaxLength: Value(StringSetting.LogParameterMaxLength),
            logParameterMaxLengthOnError: Value(StringSetting.LogParameterMaxLengthOnError),
            debugPrintParse: Value(StringSetting.DebugPrintParse),
            debugPrintRewritten: Value(StringSetting.DebugPrintRewritten),
            debugPrintPlan: Value(StringSetting.DebugPrintPlan),
            sharedPreloadLibraries: Value(StringSetting.SharedPreloadLibraries),
            sessionPreloadLibraries: Value(StringSetting.SessionPreloadLibraries),
            localPreloadLibraries: Value(StringSetting.LocalPreloadLibraries),
            logDestination: Value(StringSetting.LogDestination),
            loggingCollector: Value(StringSetting.LoggingCollector),
            fsync: Value(StringSetting.Fsync),
            fullPageWrites: Value(StringSetting.FullPageWrites),
            synchronousCommit: Value(StringSetting.SynchronousCommit),
            clientEncoding: Value(StringSetting.ClientEncoding),
            dateStyle: Value(StringSetting.DateStyle),
            sessionReplicationRole: Value(StringSetting.SessionReplicationRole),
            temporarySchemaOid: temporarySchemaOid,
            defaultTransactionReadOnly: Value(StringSetting.DefaultTransactionReadOnly),
            transactionReadOnly: Value(StringSetting.TransactionReadOnly),
            timeZone: Value(StringSetting.TimeZone),
            roleIsSuperuser: roleIsSuperuser,
            hasReadAllSettings: hasReadAllSettings);
    }

    private static TransferV3PostgreSqlIdentityProjection CreateIdentityProjection() => new(
        systemIdentifier: "18446744073709551615",
        postmasterStartTimeUtc: new DateTimeOffset(2026, 7, 14, 8, 9, 10, TimeSpan.Zero),
        databaseName: "nzbdav",
        databaseOid: 16_384,
        schemaName: "transfer_v3",
        schemaOid: 16_385,
        roleName: "nzbdav_owner",
        roleOid: 16_386,
        serverVersion: "16.14",
        serverVersionNumber: 160_014,
        isInRecovery: false,
        serverAddress: System.Net.IPAddress.Parse("2001:db8::1"),
        serverPort: 5432);

    private static string ExpectedValue(StringSetting setting) => setting switch
    {
        StringSetting.LogMinMessages => "panic",
        StringSetting.LogMinErrorStatement => "panic",
        StringSetting.LogErrorVerbosity => "terse",
        StringSetting.LogStatement => "none",
        StringSetting.LogDuration => "off",
        StringSetting.LogMinDurationStatement => "-1",
        StringSetting.LogMinDurationSample => "-1",
        StringSetting.LogTransactionSampleRate => "0",
        StringSetting.LogParameterMaxLength => "0",
        StringSetting.LogParameterMaxLengthOnError => "0",
        StringSetting.DebugPrintParse => "off",
        StringSetting.DebugPrintRewritten => "off",
        StringSetting.DebugPrintPlan => "off",
        StringSetting.SharedPreloadLibraries => string.Empty,
        StringSetting.SessionPreloadLibraries => string.Empty,
        StringSetting.LocalPreloadLibraries => string.Empty,
        StringSetting.LogDestination => "stderr",
        StringSetting.LoggingCollector => "off",
        StringSetting.Fsync => "on",
        StringSetting.FullPageWrites => "on",
        StringSetting.SynchronousCommit => "on",
        StringSetting.ClientEncoding => "UTF8",
        StringSetting.DateStyle => "ISO, MDY",
        StringSetting.SessionReplicationRole => "origin",
        StringSetting.DefaultTransactionReadOnly => "off",
        StringSetting.TransactionReadOnly => "off",
        StringSetting.TimeZone => "UTC",
        _ => throw new ArgumentOutOfRangeException(nameof(setting)),
    };

    private static string UnsafeAlternative(StringSetting setting) => setting switch
    {
        StringSetting.LogMinMessages => "warning",
        StringSetting.LogMinErrorStatement => "error",
        StringSetting.LogErrorVerbosity => "default",
        StringSetting.LogStatement => "all",
        StringSetting.LogDuration => "on",
        StringSetting.LogMinDurationStatement => "0",
        StringSetting.LogMinDurationSample => "0",
        StringSetting.LogTransactionSampleRate => "1",
        StringSetting.LogParameterMaxLength => "-1",
        StringSetting.LogParameterMaxLengthOnError => "-1",
        StringSetting.DebugPrintParse => "on",
        StringSetting.DebugPrintRewritten => "on",
        StringSetting.DebugPrintPlan => "on",
        StringSetting.SharedPreloadLibraries => "auto_explain",
        StringSetting.SessionPreloadLibraries => "auto_explain",
        StringSetting.LocalPreloadLibraries => "auto_explain",
        StringSetting.LogDestination => "csvlog",
        StringSetting.LoggingCollector => "on",
        StringSetting.Fsync => "off",
        StringSetting.FullPageWrites => "off",
        StringSetting.SynchronousCommit => "local",
        StringSetting.ClientEncoding => "LATIN1",
        StringSetting.DateStyle => "SQL, MDY",
        StringSetting.SessionReplicationRole => "replica",
        StringSetting.DefaultTransactionReadOnly => "on",
        StringSetting.TransactionReadOnly => "on",
        StringSetting.TimeZone => "Etc/UTC",
        _ => throw new ArgumentOutOfRangeException(nameof(setting)),
    };

    private static IEnumerable<string> ExpectedSqlSettings()
    {
        yield return "log_min_messages";
        yield return "log_min_error_statement";
        yield return "log_error_verbosity";
        yield return "log_statement";
        yield return "log_duration";
        yield return "log_min_duration_statement";
        yield return "log_min_duration_sample";
        yield return "log_transaction_sample_rate";
        yield return "log_parameter_max_length";
        yield return "log_parameter_max_length_on_error";
        yield return "debug_print_parse";
        yield return "debug_print_rewritten";
        yield return "debug_print_plan";
        yield return "shared_preload_libraries";
        yield return "session_preload_libraries";
        yield return "local_preload_libraries";
        yield return "log_destination";
        yield return "logging_collector";
        yield return "fsync";
        yield return "full_page_writes";
        yield return "synchronous_commit";
        yield return "client_encoding";
        yield return "DateStyle";
        yield return "session_replication_role";
        yield return "default_transaction_read_only";
        yield return "transaction_read_only";
        yield return "TimeZone";
        yield return "server_version";
        yield return "server_version_num";
    }

    private static ProjectionColumnContract[] ExpectedProjectionColumns() =>
    [
        StringColumn(0, "current_setting('log_min_messages')", "logMinMessages"),
        StringColumn(1, "current_setting('log_min_error_statement')", "logMinErrorStatement"),
        StringColumn(2, "current_setting('log_error_verbosity')", "logErrorVerbosity"),
        StringColumn(3, "current_setting('log_statement')", "logStatement"),
        StringColumn(4, "current_setting('log_duration')", "logDuration"),
        StringColumn(5, "current_setting('log_min_duration_statement')", "logMinDurationStatement"),
        StringColumn(6, "current_setting('log_min_duration_sample')", "logMinDurationSample"),
        StringColumn(7, "current_setting('log_transaction_sample_rate')", "logTransactionSampleRate"),
        StringColumn(8, "current_setting('log_parameter_max_length')", "logParameterMaxLength"),
        StringColumn(9, "current_setting('log_parameter_max_length_on_error')", "logParameterMaxLengthOnError"),
        StringColumn(10, "current_setting('debug_print_parse')", "debugPrintParse"),
        StringColumn(11, "current_setting('debug_print_rewritten')", "debugPrintRewritten"),
        StringColumn(12, "current_setting('debug_print_plan')", "debugPrintPlan"),
        StringColumn(13, "current_setting('shared_preload_libraries')", "sharedPreloadLibraries"),
        StringColumn(14, "current_setting('session_preload_libraries')", "sessionPreloadLibraries"),
        StringColumn(15, "current_setting('local_preload_libraries')", "localPreloadLibraries"),
        StringColumn(16, "current_setting('log_destination')", "logDestination"),
        StringColumn(17, "current_setting('logging_collector')", "loggingCollector"),
        StringColumn(18, "current_setting('fsync')", "fsync"),
        StringColumn(19, "current_setting('full_page_writes')", "fullPageWrites"),
        StringColumn(20, "current_setting('synchronous_commit')", "synchronousCommit"),
        StringColumn(21, "current_setting('client_encoding')", "clientEncoding"),
        StringColumn(22, "current_setting('DateStyle')", "dateStyle"),
        StringColumn(23, "current_setting('session_replication_role')", "sessionReplicationRole"),
        StringColumn(24, "current_setting('default_transaction_read_only')", "defaultTransactionReadOnly"),
        StringColumn(25, "current_setting('transaction_read_only')", "transactionReadOnly"),
        StringColumn(26, "current_setting('TimeZone')", "timeZone"),
        ValueColumn(27, "pg_my_temp_schema()", "temporarySchemaOid", "uint"),
        ValueColumn(28, "role_info.rolsuper", "roleIsSuperuser", "bool"),
        ValueColumn(
            29,
            "pg_has_role(current_user, 'pg_read_all_settings', 'USAGE')",
            "hasReadAllSettings",
            "bool"),
        IdentityStringColumn(
            30,
            "CASE WHEN control.system_identifier < 0 THEN " +
            "(control.system_identifier::numeric + 18446744073709551616)::text " +
            "ELSE control.system_identifier::text END",
            "systemIdentifier"),
        IdentityValueColumn(31, "pg_postmaster_start_time()", "postmasterStartTimeUtc", "DateTimeOffset"),
        IdentityStringColumn(32, "current_database()", "databaseName"),
        IdentityValueColumn(33, "database_info.oid", "databaseOid", "uint"),
        IdentityStringColumn(34, "current_schema()", "schemaName"),
        IdentityValueColumn(35, "schema_info.oid", "schemaOid", "uint"),
        IdentityStringColumn(36, "current_user", "roleName"),
        IdentityValueColumn(37, "role_info.oid", "roleOid", "uint"),
        IdentityStringColumn(38, "current_setting('server_version')", "serverVersion"),
        IdentityValueColumn(
            39,
            "current_setting('server_version_num')::integer",
            "serverVersionNumber",
            "int"),
        IdentityValueColumn(40, "pg_is_in_recovery()", "isInRecovery", "bool"),
        new ProjectionColumnContract(
            41,
            "inet_server_addr()",
            nameof(TransferV3PostgreSqlIdentityProjection),
            "serverAddress",
            "ReadNullableAddress",
            GenericType: null),
        IdentityValueColumn(42, "inet_server_port()", "serverPort", "int"),
    ];

    private static ProjectionColumnContract StringColumn(
        int ordinal,
        string sqlExpression,
        string namedArgument) => new(
            ordinal,
            sqlExpression,
            nameof(TransferV3PostgreSqlServerSettingsProjection),
            namedArgument,
            "ReadNullableString",
            GenericType: null);

    private static ProjectionColumnContract ValueColumn(
        int ordinal,
        string sqlExpression,
        string namedArgument,
        string genericType) => new(
            ordinal,
            sqlExpression,
            nameof(TransferV3PostgreSqlServerSettingsProjection),
            namedArgument,
            "ReadNullableValue",
            genericType);

    private static ProjectionColumnContract IdentityStringColumn(
        int ordinal,
        string sqlExpression,
        string namedArgument) => new(
            ordinal,
            sqlExpression,
            nameof(TransferV3PostgreSqlIdentityProjection),
            namedArgument,
            "ReadNullableString",
            GenericType: null);

    private static ProjectionColumnContract IdentityValueColumn(
        int ordinal,
        string sqlExpression,
        string namedArgument,
        string genericType) => new(
            ordinal,
            sqlExpression,
            nameof(TransferV3PostgreSqlIdentityProjection),
            namedArgument,
            "ReadNullableValue",
            genericType);

    private static string? ProjectionReadHelper(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax identifier when identifier.Identifier.ValueText is
                "ReadNullableString" or "ReadNullableAddress" =>
                identifier.Identifier.ValueText,
            GenericNameSyntax generic when generic.Identifier.ValueText
                == "ReadNullableValue" => generic.Identifier.ValueText,
            _ => null,
        };

    private static string[] ParseTopLevelSelectExpressions(string sql)
    {
        const string selectMarker = "SELECT";
        const string fromMarker = "FROM pg_control_system()";
        var selectStart = sql.IndexOf(selectMarker, StringComparison.Ordinal);
        var fromStart = sql.IndexOf(
            fromMarker,
            selectStart + selectMarker.Length,
            StringComparison.Ordinal);
        Assert.True(selectStart >= 0);
        Assert.True(fromStart > selectStart);

        var projection = sql[(selectStart + selectMarker.Length)..fromStart];
        var expressions = new List<string>();
        var expressionStart = 0;
        var parenthesisDepth = 0;
        var insideString = false;
        for (var index = 0; index < projection.Length; index++)
        {
            var character = projection[index];
            if (character == '\'')
            {
                if (insideString
                    && index + 1 < projection.Length
                    && projection[index + 1] == '\'')
                {
                    index++;
                }
                else
                {
                    insideString = !insideString;
                }
                continue;
            }

            if (insideString)
                continue;
            if (character == '(')
            {
                parenthesisDepth++;
                continue;
            }
            if (character == ')')
            {
                parenthesisDepth--;
                Assert.True(parenthesisDepth >= 0);
                continue;
            }
            if (character != ',' || parenthesisDepth != 0)
                continue;

            expressions.Add(NormalizeSqlExpression(
                projection[expressionStart..index]));
            expressionStart = index + 1;
        }

        Assert.False(insideString);
        Assert.Equal(0, parenthesisDepth);
        expressions.Add(NormalizeSqlExpression(projection[expressionStart..]));
        return [.. expressions];
    }

    private static string NormalizeSqlExpression(string expression) =>
        Regex.Replace(expression, @"\s+", " ").Trim();

    private static string SwapAsciiCase(string value)
    {
        return string.Concat(value.Select(character => character switch
        {
            >= 'a' and <= 'z' => (char)(character - ('a' - 'A')),
            >= 'A' and <= 'Z' => (char)(character + ('a' - 'A')),
            _ => character,
        }));
    }

    private static void AssertOrdinaryReadonlyStruct(Type type, string[] expectedProperties)
    {
        Assert.True(type.IsValueType);
        Assert.False(type.IsEnum);
        Assert.True(type.IsDefined(typeof(IsReadOnlyAttribute), inherit: false));
        Assert.DoesNotContain(
            type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            method => method.Name is "PrintMembers" or "get_EqualityContract");
        Assert.NotEqual(type, type.GetMethod(nameof(ToString), Type.EmptyTypes)?.DeclaringType);
        Assert.Equal(
            expectedProperties.Order(StringComparer.Ordinal),
            type.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    private static void AssertRejected(Func<object?> action)
    {
        Assert.NotNull(Record.Exception(action));
    }

    private static string? InvocationName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null,
        };

    private sealed record ProjectionColumnContract(
        int Ordinal,
        string SqlExpression,
        string ProjectionType,
        string NamedArgument,
        string ReadHelper,
        string? GenericType);
}
