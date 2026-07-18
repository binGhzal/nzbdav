using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database;

[Collection(nameof(PostgreSqlSerialCollection))]
public sealed class PostgreSqlEnvironmentContractTests
{
    [Fact]
    public void ValidateAsyncPreservesOldSignaturesAndAddsTransactionBoundOverload()
    {
        var overloads = typeof(PostgreSqlNativeMigrator).Assembly
            .GetType("NzbWebDAV.Database.PostgreSqlEnvironmentContract")!
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == "ValidateAsync")
            .ToArray();

        Assert.Collection(
            overloads.OrderBy(method => method.GetParameters().Length)
                .ThenBy(method => string.Join(",", method.GetParameters()
                    .Select(parameter => parameter.ParameterType.FullName)), StringComparer.Ordinal),
            method => AssertValidateAsyncSignature(
                method,
                typeof(NpgsqlConnection),
                typeof(CancellationToken)),
            method => AssertValidateAsyncSignature(
                method,
                typeof(NpgsqlConnection),
                typeof(int),
                typeof(CancellationToken)),
            method => AssertValidateAsyncSignature(
                method,
                typeof(NpgsqlConnection),
                typeof(NpgsqlTransaction),
                typeof(int),
                typeof(CancellationToken)),
            method => AssertValidateAsyncSignature(
                method,
                typeof(NpgsqlConnection),
                typeof(string),
                typeof(int),
                typeof(CancellationToken)),
            method => AssertValidateAsyncSignature(
                method,
                typeof(NpgsqlConnection),
                typeof(string),
                typeof(int),
                typeof(int),
                typeof(CancellationToken)));
    }

    [Fact]
    public void TransactionBoundEnvironmentValidationProvesOwnershipAndBindsTheCommand()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Database/PostgreSqlEnvironmentContract.cs"));
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var method = Assert.Single(
            root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            candidate => candidate.Identifier.ValueText == "ValidateAsync"
                         && candidate.ParameterList.Parameters.Count == 4
                         && candidate.ParameterList.Parameters[1].Type?.ToString()
                         == "NpgsqlTransaction");
        var ownership = Assert.Single(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ValidateTransactionContext");
        var delegation = Assert.Single(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ValidateCoreAsync");
        Assert.Contains(
            delegation.ArgumentList.Arguments,
            argument => argument.Expression.ToString() == "transaction");

        var core = Assert.Single(
            root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            candidate => candidate.Identifier.ValueText == "ValidateCoreAsync");
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task ExplicitTimeoutOverloadsValidateTimeoutBeforeConnectionAccess(
        int invalidCommandTimeoutSeconds)
    {
        var contractType = typeof(PostgreSqlNativeMigrator).Assembly.GetType(
            "NzbWebDAV.Database.PostgreSqlEnvironmentContract");
        Assert.NotNull(contractType);

        var ordinaryOverload = GetValidateAsync(
            contractType,
            typeof(NpgsqlConnection),
            typeof(int),
            typeof(CancellationToken));
        var expectedVersionOverload = GetValidateAsync(
            contractType,
            typeof(NpgsqlConnection),
            typeof(string),
            typeof(int),
            typeof(int),
            typeof(CancellationToken));

        var ordinaryError = await InvokeValidateAsync(
            ordinaryOverload,
            [null, invalidCommandTimeoutSeconds, CancellationToken.None]);
        var expectedVersionError = await InvokeValidateAsync(
            expectedVersionOverload,
            [
                null,
                PostgreSqlEnvironmentContract.RequiredServerVersion,
                PostgreSqlEnvironmentContract.RequiredServerVersionNumber,
                invalidCommandTimeoutSeconds,
                CancellationToken.None
            ]);

        Assert.Equal(
            "commandTimeoutSeconds",
            Assert.IsType<ArgumentOutOfRangeException>(ordinaryError).ParamName);
        Assert.Equal(
            "commandTimeoutSeconds",
            Assert.IsType<ArgumentOutOfRangeException>(expectedVersionError).ParamName);
    }

    [Fact]
    public async Task OldValidateAsyncOverloadsRefuseInfiniteEffectiveCommandTimeoutBeforeStateAccess()
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 5432,
            Database = "postgres",
            Username = "nzbdav_test",
            SearchPath = "nzbdav_test",
            CommandTimeout = 0
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        Assert.Equal(0, connection.CommandTimeout);

        var ordinaryError = await Record.ExceptionAsync(
            () => PostgreSqlEnvironmentContract.ValidateAsync(
                connection,
                CancellationToken.None));
        var expectedVersionError = await Record.ExceptionAsync(
            () => PostgreSqlEnvironmentContract.ValidateAsync(
                connection,
                PostgreSqlEnvironmentContract.RequiredServerVersion,
                PostgreSqlEnvironmentContract.RequiredServerVersionNumber,
                CancellationToken.None));

        Assert.Equal(
            "commandTimeoutSeconds",
            Assert.IsType<ArgumentOutOfRangeException>(ordinaryError).ParamName);
        Assert.Equal(
            "commandTimeoutSeconds",
            Assert.IsType<ArgumentOutOfRangeException>(expectedVersionError).ParamName);
    }

    [Fact]
    public void EnvironmentQueryAssignsExplicitTimeoutBeforeExecuteReader()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Database/PostgreSqlEnvironmentContract.cs"));
        var tree = CSharpSyntaxTree.ParseText(source);
        var errors = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
        var contract = tree.GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "PostgreSqlEnvironmentContract");
        var execution = contract.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => invocation.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "ExecuteReaderAsync"
            });
        var containingMethod = execution.Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .Single();
        var assignment = containingMethod.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .SingleOrDefault(candidate =>
                candidate.Left is MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "command" },
                    Name.Identifier.ValueText: "CommandTimeout"
                }
                && candidate.Right is IdentifierNameSyntax
                {
                    Identifier.ValueText: "commandTimeoutSeconds"
                });

        var timeoutAssignment = Assert.IsType<AssignmentExpressionSyntax>(assignment);
        Assert.True(
            timeoutAssignment.SpanStart < execution.SpanStart,
            "The explicit positive timeout must be assigned before the environment query executes.");
    }

    [Fact]
    public void EnvironmentValidationUsesExplicitPrimaryPreservingDisposal()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath(
            "backend/Database/PostgreSqlEnvironmentContract.cs"));
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var method = Assert.Single(
            root.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            candidate => candidate.Identifier.ValueText == "ValidateCoreAsync");

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
                          && assignment.Right.ToString() == "raw");
        Assert.True(primaryCapture.SpanStart < disposal.SpanStart);
    }

    [Fact]
    public async Task PrimaryPreservingDisposalKeepsRawPrimaryByReferenceAndCleansInOrder()
    {
        var calls = new List<string>();
        var primary = new InvalidOperationException("primary-CANARY");
        var reader = new RecordingAsyncDisposable(
            "reader",
            calls,
            new InvalidOperationException("reader-cleanup-CANARY"));
        var command = new RecordingAsyncDisposable(
            "command",
            calls,
            new InvalidOperationException("command-cleanup-CANARY"));

        var observed = await InvokePrimaryPreservingDisposalAsync(
            reader,
            command,
            primary);

        Assert.Same(primary, observed);
        Assert.Equal(["reader", "command"], calls);
    }

    [Fact]
    public async Task PrimaryPreservingDisposalKeepsFirstCleanupFailureAndStillCleansCommand()
    {
        var calls = new List<string>();
        var readerFailure = new PostgresException(
            "reader-cleanup-CANARY",
            "ERROR",
            "ERROR",
            "23505");
        var commandFailure = new PostgresException(
            "command-cleanup-CANARY",
            "ERROR",
            "ERROR",
            "40001");
        var reader = new RecordingAsyncDisposable("reader", calls, readerFailure);
        var command = new RecordingAsyncDisposable("command", calls, commandFailure);

        var observed = await InvokePrimaryPreservingDisposalAsync(
            reader,
            command,
            primaryFailure: null);

        var sanitized = Assert.IsType<TransferV3Phase4Exception>(observed);
        Assert.Equal("phase4-postgresql-command", sanitized.Code);
        Assert.Equal("23505", sanitized.SqlState);
        Assert.DoesNotContain("CANARY", sanitized.ToString(), StringComparison.Ordinal);
        Assert.Equal(["reader", "command"], calls);
    }

    [Fact]
    public async Task PrimaryPreservingDisposalSurfacesCommandCleanupWhenItIsFirstFailure()
    {
        var calls = new List<string>();
        var commandFailure = new PostgresException(
            "command-cleanup-CANARY",
            "ERROR",
            "ERROR",
            "40001");
        var reader = new RecordingAsyncDisposable("reader", calls, failure: null);
        var command = new RecordingAsyncDisposable("command", calls, commandFailure);

        var observed = await InvokePrimaryPreservingDisposalAsync(
            reader,
            command,
            primaryFailure: null);

        var sanitized = Assert.IsType<TransferV3Phase4Exception>(observed);
        Assert.Equal("phase4-postgresql-command", sanitized.Code);
        Assert.Equal("40001", sanitized.SqlState);
        Assert.DoesNotContain("CANARY", sanitized.ToString(), StringComparison.Ordinal);
        Assert.Equal(["reader", "command"], calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanupOnlyCancellationIsAlwaysSanitizedAsACommandFailure(
        bool readerFails)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = new List<string>();
        var cleanupFailure = new OperationCanceledException(
            "cleanup-cancellation-CANARY",
            cancellation.Token);
        var reader = new RecordingAsyncDisposable(
            "reader",
            calls,
            readerFails ? cleanupFailure : null);
        var command = new RecordingAsyncDisposable(
            "command",
            calls,
            readerFails ? null : cleanupFailure);

        var observed = await InvokePrimaryPreservingDisposalAsync(
            reader,
            command,
            primaryFailure: null);

        var sanitized = Assert.IsType<TransferV3Phase4Exception>(observed);
        Assert.Equal("phase4-postgresql-command", sanitized.Code);
        Assert.DoesNotContain("CANARY", sanitized.ToString(), StringComparison.Ordinal);
        Assert.Equal(["reader", "command"], calls);
    }

    [PostgreSqlFact]
    public async Task WrongRequiredServerPatchIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_wrong_patch");
        await using var connection = await schema.OpenConnectionAsync();
        var before = await CaptureMutationStateAsync(connection, schema.SchemaName);
        var contractType = typeof(PostgreSqlNativeMigrator).Assembly.GetType(
            "NzbWebDAV.Database.PostgreSqlEnvironmentContract");
        Assert.NotNull(contractType);
        var validate = contractType.GetMethod(
            "ValidateAsync",
            System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [typeof(NpgsqlConnection), typeof(string), typeof(int), typeof(CancellationToken)],
            modifiers: null);
        Assert.NotNull(validate);

        var task = Assert.IsAssignableFrom<Task<string>>(validate.Invoke(
            null,
            [connection, "16.13", 160013, CancellationToken.None]));
        var error = await Record.ExceptionAsync(() => task);

        Assert.Equal(before, await CaptureMutationStateAsync(connection, schema.SchemaName));
        var refusal = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("16.13", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("16.14", refusal.Message, StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task MultiSchemaSearchPathIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_multi_path");
        var extraSchema = $"extra_{Guid.NewGuid():N}";
        await schema.ExecuteAsync($"CREATE SCHEMA \"{extraSchema}\"");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(schema.ConnectionString)
            {
                SearchPath = $"{schema.SchemaName},{extraSchema}"
            };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();

            await AssertRefusedWithoutMutationAsync(connection, schema.SchemaName, "single target schema");
        }
        finally
        {
            await schema.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{extraSchema}\" CASCADE");
        }
    }

    [PostgreSqlFact]
    public async Task WrongEffectiveTargetSchemaIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_wrong_path");
        var extraSchema = $"wrong_{Guid.NewGuid():N}";
        await schema.ExecuteAsync($"CREATE SCHEMA \"{extraSchema}\"");
        try
        {
            await using var connection = await schema.OpenConnectionAsync();
            await PostgreSqlTestSchema.ExecuteAsync(
                connection,
                $"SET search_path TO \"{extraSchema}\"");

            await AssertRefusedWithoutMutationAsync(
                connection,
                schema.SchemaName,
                "single target schema");
        }
        finally
        {
            await schema.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{extraSchema}\" CASCADE");
        }
    }

    [PostgreSqlFact]
    public async Task TemporaryShadowSchemaIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_temp_shadow");
        await using var connection = await schema.OpenConnectionAsync();
        await PostgreSqlTestSchema.ExecuteAsync(
            connection,
            "CREATE TEMP TABLE \"__EFMigrationsHistory_PostgreSql\" (\"MigrationId\" text)");

        await AssertRefusedWithoutMutationAsync(connection, schema.SchemaName, "temporary schema");

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT to_regclass('pg_temp.\"__EFMigrationsHistory_PostgreSql\"') IS NOT NULL";
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    [PostgreSqlFact]
    public async Task EventTriggerIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_event_trigger");
        var suffix = Guid.NewGuid().ToString("N");
        var functionName = $"nzbdav_event_{suffix}";
        var triggerName = $"nzbdav_event_trigger_{suffix}";
        await schema.ExecuteAsync(
            $$"""
            CREATE FUNCTION public."{{functionName}}"()
            RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN END $$;
            CREATE EVENT TRIGGER "{{triggerName}}"
            ON ddl_command_start EXECUTE FUNCTION public."{{functionName}}"();
            """);
        try
        {
            await using var connection = await schema.OpenConnectionAsync();
            await AssertRefusedWithoutMutationAsync(connection, schema.SchemaName, "event trigger");
        }
        finally
        {
            await schema.ExecuteAsync(
                $$"""
                DROP EVENT TRIGGER IF EXISTS "{{triggerName}}";
                DROP FUNCTION IF EXISTS public."{{functionName}}"();
                """);
        }
    }

    [PostgreSqlFact]
    public async Task PublicationForAllTablesIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_publication_all");
        var publication = $"nzbdav_all_{Guid.NewGuid():N}";
        await schema.ExecuteAsync($"CREATE PUBLICATION \"{publication}\" FOR ALL TABLES");
        try
        {
            await using var connection = await schema.OpenConnectionAsync();
            await AssertRefusedWithoutMutationAsync(connection, schema.SchemaName, "publication");
        }
        finally
        {
            await schema.ExecuteAsync($"DROP PUBLICATION IF EXISTS \"{publication}\"");
        }
    }

    [PostgreSqlFact]
    public async Task SchemaPublicationIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_publication_schema");
        var publication = $"nzbdav_schema_{Guid.NewGuid():N}";
        await schema.ExecuteAsync(
            $"CREATE PUBLICATION \"{publication}\" FOR TABLES IN SCHEMA \"{schema.SchemaName}\"");
        try
        {
            await using var connection = await schema.OpenConnectionAsync();
            await AssertRefusedWithoutMutationAsync(connection, schema.SchemaName, "publication");
        }
        finally
        {
            await schema.ExecuteAsync($"DROP PUBLICATION IF EXISTS \"{publication}\"");
        }
    }

    [PostgreSqlFact]
    public async Task PublicSchemaCreateGrantIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_schema_acl");
        await schema.ExecuteAsync($"GRANT CREATE ON SCHEMA \"{schema.SchemaName}\" TO PUBLIC");
        try
        {
            await using var connection = await schema.OpenConnectionAsync();
            await AssertRefusedWithoutMutationAsync(connection, schema.SchemaName, "exclusive CREATE");
        }
        finally
        {
            await schema.ExecuteAsync($"REVOKE CREATE ON SCHEMA \"{schema.SchemaName}\" FROM PUBLIC");
        }
    }

    [PostgreSqlFact]
    public async Task PublicDatabaseCreateGrantIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_database_acl");
        var databaseName = await schema.ScalarAsync<string>("SELECT current_database()");
        var quotedDatabase = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        await using var connection = await schema.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await PostgreSqlTestSchema.ExecuteAsync(
                connection,
                $"GRANT CREATE ON DATABASE {quotedDatabase} TO PUBLIC");
            await AssertRefusedWithoutMutationAsync(connection, schema.SchemaName, "exclusive CREATE");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [PostgreSqlFact]
    public async Task SchemaOwnedByAnotherRoleIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_wrong_owner");
        var otherRole = $"nzbdav_other_{Guid.NewGuid():N}";
        var quotedRole = new NpgsqlCommandBuilder().QuoteIdentifier(otherRole);
        var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(schema.SchemaName);
        var currentRole = await schema.ScalarAsync<string>("SELECT current_user");
        var quotedCurrentRole = new NpgsqlCommandBuilder().QuoteIdentifier(currentRole);
        await schema.ExecuteAsync($"CREATE ROLE {quotedRole}");
        await schema.ExecuteAsync($"ALTER SCHEMA {quotedSchema} OWNER TO {quotedRole}");
        try
        {
            await using var connection = await schema.OpenConnectionAsync();
            await AssertRefusedWithoutMutationAsync(connection, schema.SchemaName, "must own target schema");
        }
        finally
        {
            await schema.ExecuteAsync($"ALTER SCHEMA {quotedSchema} OWNER TO {quotedCurrentRole}");
            await schema.ExecuteAsync($"DROP ROLE {quotedRole}");
        }
    }

    [PostgreSqlFact]
    public async Task DisabledSubscriptionIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_subscription");
        var subscription = $"nzbdav_subscription_{Guid.NewGuid():N}";
        var quotedSubscription = new NpgsqlCommandBuilder().QuoteIdentifier(subscription);
        await schema.ExecuteAsync(
            $"CREATE SUBSCRIPTION {quotedSubscription} " +
            "CONNECTION 'host=127.0.0.1 port=1 dbname=none user=none' " +
            "PUBLICATION nzbdav_none WITH (connect=false, enabled=false, create_slot=false)");
        try
        {
            await using var connection = await schema.OpenConnectionAsync();
            await AssertRefusedWithoutMutationAsync(connection, schema.SchemaName, "subscription");
        }
        finally
        {
            await schema.ExecuteAsync(
                $"ALTER SUBSCRIPTION {quotedSubscription} SET (slot_name=NONE)");
            await schema.ExecuteAsync($"DROP SUBSCRIPTION IF EXISTS {quotedSubscription}");
        }
    }

    [PostgreSqlFact]
    public async Task UnsafeDefaultTableAclIsRefusedWithoutMutation()
    {
        await using var schema = await PostgreSqlTestSchema.CreateAsync("environment_default_acl");
        await schema.ExecuteAsync(
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA \"{schema.SchemaName}\" GRANT SELECT ON TABLES TO PUBLIC");
        try
        {
            await using var connection = await schema.OpenConnectionAsync();
            await AssertRefusedWithoutMutationAsync(connection, schema.SchemaName, "default ACL");
        }
        finally
        {
            await schema.ExecuteAsync(
                $"ALTER DEFAULT PRIVILEGES IN SCHEMA \"{schema.SchemaName}\" REVOKE SELECT ON TABLES FROM PUBLIC");
        }
    }

    private static async Task AssertRefusedWithoutMutationAsync(
        NpgsqlConnection connection,
        string targetSchema,
        string expectedMessage)
    {
        var before = await CaptureMutationStateAsync(connection, targetSchema);

        var error = await Record.ExceptionAsync(
            () => PostgreSqlNativeMigrator.MigrateOpenConnectionAsync(connection));

        var after = await CaptureMutationStateAsync(connection, targetSchema);
        Assert.Equal(before, after);
        var refusal = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains(expectedMessage, refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> CaptureMutationStateAsync(
        NpgsqlConnection connection,
        string targetSchema)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH target_namespace AS (
                SELECT oid FROM pg_namespace WHERE nspname = @target_schema
            ), entries AS (
                SELECT concat_ws('|', 'scope', current_database(), current_user,
                    current_setting('search_path'), pg_my_temp_schema()::text,
                    d.datdba::text, coalesce(d.datacl::text, ''),
                    n.oid::text, n.nspowner::text, coalesce(n.nspacl::text, '')) AS entry
                FROM pg_database AS d
                CROSS JOIN pg_namespace AS n
                WHERE d.datname = current_database() AND n.nspname = @target_schema
                UNION ALL
                SELECT concat_ws('|', 'class', c.oid::text, n.nspname, c.relname, c.relkind,
                    c.relowner::text, c.relfilenode::text, coalesce(c.relacl::text, ''))
                FROM pg_class AS c JOIN pg_namespace AS n ON n.oid = c.relnamespace
                WHERE n.oid IN (SELECT oid FROM target_namespace)
                   OR n.oid = pg_my_temp_schema()
                UNION ALL
                SELECT concat_ws('|', 'attribute', a.attrelid::text, a.attnum::text, a.attname,
                    a.atttypid::text, a.atttypmod::text, a.attnotnull::text,
                    a.atthasdef::text, a.attisdropped::text, coalesce(a.attacl::text, ''))
                FROM pg_attribute AS a JOIN pg_class AS c ON c.oid = a.attrelid
                WHERE c.relnamespace IN (SELECT oid FROM target_namespace)
                   OR c.relnamespace = pg_my_temp_schema()
                UNION ALL
                SELECT concat_ws('|', 'constraint', con.oid::text, con.conname,
                    con.conrelid::text, con.contype, con.convalidated::text)
                FROM pg_constraint AS con
                WHERE con.connamespace IN (SELECT oid FROM target_namespace)
                UNION ALL
                SELECT concat_ws('|', 'procedure', p.oid::text, p.proname, p.proowner::text,
                    coalesce(p.proacl::text, ''))
                FROM pg_proc AS p WHERE p.pronamespace IN (SELECT oid FROM target_namespace)
                UNION ALL
                SELECT concat_ws('|', 'type', t.oid::text, t.typname, t.typtype,
                    t.typowner::text, t.typisdefined::text)
                FROM pg_type AS t WHERE t.typnamespace IN (SELECT oid FROM target_namespace)
                UNION ALL
                SELECT concat_ws('|', 'trigger', tr.oid::text, tr.tgrelid::text,
                    tr.tgname, tr.tgenabled, tr.tgisinternal::text)
                FROM pg_trigger AS tr JOIN pg_class AS c ON c.oid = tr.tgrelid
                WHERE c.relnamespace IN (SELECT oid FROM target_namespace)
                UNION ALL
                SELECT concat_ws('|', 'default-acl', da.oid::text, da.defaclrole::text,
                    da.defaclnamespace::text, da.defaclobjtype, coalesce(da.defaclacl::text, ''))
                FROM pg_default_acl AS da
                WHERE da.defaclnamespace IN (SELECT oid FROM target_namespace)
                UNION ALL
                SELECT concat_ws('|', 'event-trigger', e.oid::text, e.evtname,
                    e.evtevent, e.evtenabled, e.evtfoid::text)
                FROM pg_event_trigger AS e
                UNION ALL
                SELECT concat_ws('|', 'publication', p.oid::text, p.pubname,
                    p.puballtables::text, p.pubinsert::text, p.pubupdate::text,
                    p.pubdelete::text, p.pubtruncate::text)
                FROM pg_publication AS p
                UNION ALL
                SELECT concat_ws('|', 'publication-namespace', pn.pnpubid::text, pn.pnnspid::text)
                FROM pg_publication_namespace AS pn
                UNION ALL
                SELECT concat_ws('|', 'subscription', s.oid::text, s.subdbid::text,
                    s.subname, s.subenabled::text)
                FROM pg_subscription AS s
                WHERE s.subdbid = (SELECT oid FROM pg_database WHERE datname = current_database())
            )
            SELECT coalesce(string_agg(entry, E'\n' ORDER BY entry), '') FROM entries
            """;
        command.Parameters.AddWithValue("target_schema", targetSchema);
        var catalog = (string)(await command.ExecuteScalarAsync())!;

        await using var historyExistsCommand = connection.CreateCommand();
        historyExistsCommand.CommandText =
            "SELECT to_regclass(format('%I.%I', @schema, @table)) IS NOT NULL";
        historyExistsCommand.Parameters.AddWithValue("schema", targetSchema);
        historyExistsCommand.Parameters.AddWithValue(
            "table",
            DatabaseMigrationPolicy.PostgreSqlHistoryTableName);
        if (!(bool)(await historyExistsCommand.ExecuteScalarAsync())!) return catalog;

        var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(targetSchema);
        await using var historyCommand = connection.CreateCommand();
        historyCommand.CommandText =
            $"SELECT \"MigrationId\" || '|' || \"ProductVersion\" FROM {quotedSchema}.\"{DatabaseMigrationPolicy.PostgreSqlHistoryTableName}\" ORDER BY \"MigrationId\"";
        var history = new StringBuilder(catalog);
        await using var reader = await historyCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync()) history.Append("\nhistory-row|").Append(reader.GetString(0));
        return history.ToString();
    }

    private static MethodInfo GetValidateAsync(Type contractType, params Type[] parameterTypes)
    {
        var method = contractType.GetMethod(
            "ValidateAsync",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            parameterTypes,
            modifiers: null);
        Assert.NotNull(method);
        return method;
    }

    private static void AssertValidateAsyncSignature(MethodInfo method, params Type[] parameterTypes)
    {
        Assert.Equal(typeof(Task<string>), method.ReturnType);
        Assert.Equal(parameterTypes, method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static async Task<Exception?> InvokeValidateAsync(MethodInfo method, object?[] arguments)
    {
        try
        {
            var task = Assert.IsAssignableFrom<Task<string>>(method.Invoke(null, arguments));
            return await Record.ExceptionAsync(() => task);
        }
        catch (TargetInvocationException error)
        {
            return error.InnerException;
        }
    }

    private static async Task<Exception?> InvokePrimaryPreservingDisposalAsync(
        IAsyncDisposable? reader,
        IAsyncDisposable? command,
        Exception? primaryFailure)
    {
        return await Record.ExceptionAsync(() =>
            PostgreSqlPrimaryPreservingAsyncDisposal.DisposeReaderThenCommandAsync(
                    reader,
                    command,
                    primaryFailure)
                .AsTask());
    }

    private static string? InvocationName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null,
        };

    private sealed class RecordingAsyncDisposable(
        string name,
        ICollection<string> calls,
        Exception? failure) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            calls.Add(name);
            return failure is null
                ? ValueTask.CompletedTask
                : new ValueTask(Task.FromException(failure));
        }
    }
}

[CollectionDefinition(nameof(PostgreSqlSerialCollection), DisableParallelization = true)]
public sealed class PostgreSqlSerialCollection;
