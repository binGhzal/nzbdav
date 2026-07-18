using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Npgsql;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

public sealed class TransferV3PostgreSqlAdmissionLockSetTests
{
    private const string LockSetSourcePath =
        "backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionLockSet.cs";

    private static readonly string[] ExpectedRelationNames =
    [
        "Accounts",
        "ConfigItems",
        "HistoryItems",
        "QueueItems",
        "RepairRuns",
        "DavItems",
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
        "HealthCheckStats",
        "__EFMigrationsHistory_PostgreSql",
    ];

    [Fact]
    public void PublicInternalContractHasExactReviewedShapeAndSeed()
    {
        var type = typeof(TransferV3PostgreSqlAdmissionLockSet);
        Assert.True(type.IsAbstract && type.IsSealed);
        Assert.False(type.IsPublic);

        var seed = Assert.IsAssignableFrom<FieldInfo>(type.GetField(
            "AdvisoryNamespaceSeed",
            BindingFlags.Static | BindingFlags.NonPublic));
        Assert.True(seed.IsLiteral);
        Assert.Equal(typeof(long), seed.FieldType);
        Assert.Equal(0x4E5A425456335034L, seed.GetRawConstantValue());

        var relationNames = Assert.IsAssignableFrom<PropertyInfo>(type.GetProperty(
            "RelationNames",
            BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Equal(typeof(IReadOnlyList<string>), relationNames.PropertyType);
        Assert.True(relationNames.GetMethod!.IsAssembly);
        Assert.Null(relationNames.SetMethod);

        AssertMethod("BuildRelationLockSql", typeof(string), typeof(string));
        AssertMethod(
            "TryAcquireAdvisoryAsync",
            typeof(Task<bool>),
            typeof(NpgsqlConnection),
            typeof(NpgsqlTransaction),
            typeof(int),
            typeof(CancellationToken));
        AssertMethod(
            "AcquireRelationsAsync",
            typeof(Task),
            typeof(NpgsqlConnection),
            typeof(NpgsqlTransaction),
            typeof(int),
            typeof(CancellationToken));

        var reviewedMethods = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic);
        Assert.Single(reviewedMethods, method => method.Name == "BuildRelationLockSql");
        Assert.Single(reviewedMethods, method => method.Name == "TryAcquireAdvisoryAsync");
        Assert.Single(reviewedMethods, method => method.Name == "AcquireRelationsAsync");
    }

    [Fact]
    public void RelationNamesAreTheIndependentImmutable29RelationOracle()
    {
        var first = TransferV3PostgreSqlAdmissionLockSet.RelationNames;
        var second = TransferV3PostgreSqlAdmissionLockSet.RelationNames;

        Assert.Same(first, second);
        Assert.Equal(29, first.Count);
        Assert.Equal(ExpectedRelationNames, first);
        Assert.Equal(
            ExpectedRelationNames.Length,
            ExpectedRelationNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            first.Count,
            first.Distinct(StringComparer.Ordinal).Count());

        var frozen = Assert.IsType<System.Collections.ObjectModel.ReadOnlyCollection<string>>(first);
        var mutableView = Assert.IsAssignableFrom<IList<string>>(frozen);
        Assert.True(mutableView.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = "changed");
        Assert.Throws<NotSupportedException>(() => mutableView.Add("changed"));
        Assert.Throws<NotSupportedException>(() => mutableView.RemoveAt(0));
        Assert.Equal(ExpectedRelationNames, TransferV3PostgreSqlAdmissionLockSet.RelationNames);
    }

    [Fact]
    public void RelationNamesAreDerivedFromTheFrozenTargetContractAndReadOnlyWrapper()
    {
        var lockSet = ParseLockSet();
        var builder = FindMethod(lockSet, "BuildRelationNames", parameterCount: 0);

        var load = Assert.Single(Invocations(builder, "LoadEmbedded"));
        Assert.Equal(
            "TransferV3PostgreSqlTargetContract.LoadEmbedded",
            load.Expression.ToString());
        Assert.Contains(
            builder.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            access => access.ToString() == "target.Tables");
        Assert.Contains(
            builder.DescendantNodes().OfType<ArgumentSyntax>(),
            argument => argument.Expression.ToString()
                        == "target.DerivedHealthCheckStats.Name");
        Assert.Contains(
            builder.DescendantNodes().OfType<ArgumentSyntax>(),
            argument => argument.Expression.ToString()
                        == "DatabaseMigrationPolicy.PostgreSqlHistoryTableName");
        var freeze = Assert.Single(Invocations(builder, "AsReadOnly"));
        Assert.Equal("Array.AsReadOnly", freeze.Expression.ToString());
        AssertInvocationArguments(freeze, "relationNames");

        Assert.DoesNotContain(
            lockSet.Members.OfType<FieldDeclarationSyntax>()
                .SelectMany(field => field.DescendantNodes().OfType<LiteralExpressionSyntax>()),
            literal => literal.IsKind(SyntaxKind.StringLiteralExpression)
                       && ExpectedRelationNames.Contains(
                           literal.Token.ValueText,
                           StringComparer.Ordinal));
    }

    [Fact]
    public void RelationLockSqlIsExactSchemaQualifiedIdentifierSafeAndSingleStatement()
    {
        const string targetSchema = "nzbdav_target";
        var sql = TransferV3PostgreSqlAdmissionLockSet.BuildRelationLockSql(targetSchema);

        Assert.Equal(ExpectedRelationLockSql(targetSchema), sql);
        Assert.StartsWith("LOCK TABLE\n", sql, StringComparison.Ordinal);
        Assert.EndsWith("\nIN EXCLUSIVE MODE NOWAIT", sql, StringComparison.Ordinal);
        Assert.Equal(29, Regex.Matches(sql, "\\\"nzbdav_target\\\"\\.\\\"").Count);
        Assert.Equal(28, sql.Count(character => character == ','));
        Assert.DoesNotContain(" ONLY ", $" {sql} ", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(';', sql);
    }

    [Fact]
    public void RelationLockSqlQuotesTheSchemaAsOneIdentifier()
    {
        const string targetSchema = "tenant.schema\"; DROP SCHEMA public; --";

        var sql = TransferV3PostgreSqlAdmissionLockSet.BuildRelationLockSql(targetSchema);

        Assert.Equal(ExpectedRelationLockSql(targetSchema), sql);
        Assert.Equal(29, Regex.Matches(
            sql,
            Regex.Escape("\"tenant.schema\"\"; DROP SCHEMA public; --\".")).Count);
        Assert.Single(Regex.Matches(
                sql,
                "IN EXCLUSIVE MODE NOWAIT",
                RegexOptions.CultureInvariant)
            .Cast<Match>());
    }

    [Fact]
    public void RelationLockSqlRejectsMissingSchemaBeforeBuildingSql()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TransferV3PostgreSqlAdmissionLockSet.BuildRelationLockSql(null!));
        Assert.Throws<ArgumentException>(() =>
            TransferV3PostgreSqlAdmissionLockSet.BuildRelationLockSql(string.Empty));
        Assert.Throws<ArgumentException>(() =>
            TransferV3PostgreSqlAdmissionLockSet.BuildRelationLockSql(" \t"));
    }

    [Fact]
    public void AdvisorySqlIsTheExactNestedComponentWiseDatabaseAndSchemaHash()
    {
        Assert.Equal(
            """
            SELECT pg_catalog.pg_try_advisory_xact_lock(
                pg_catalog.hashtextextended(
                    @targetSchema,
                    pg_catalog.hashtextextended(
                        pg_catalog.current_database(),
                        @namespaceSeed)))
            """,
            AdvisorySqlField().Sql);
    }

    [Fact]
    public async Task ArgumentValidationPrecedesEveryProviderAccess()
    {
        using var closedConnection = new NpgsqlConnection();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            TransferV3PostgreSqlAdmissionLockSet.TryAcquireAdvisoryAsync(
                null!,
                null!,
                0,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            TransferV3PostgreSqlAdmissionLockSet.TryAcquireAdvisoryAsync(
                null!,
                null!,
                1,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            TransferV3PostgreSqlAdmissionLockSet.TryAcquireAdvisoryAsync(
                closedConnection,
                null!,
                1,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            TransferV3PostgreSqlAdmissionLockSet.AcquireRelationsAsync(
                null!,
                null!,
                0,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            TransferV3PostgreSqlAdmissionLockSet.AcquireRelationsAsync(
                null!,
                null!,
                1,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            TransferV3PostgreSqlAdmissionLockSet.AcquireRelationsAsync(
                closedConnection,
                null!,
                1,
                CancellationToken.None));
    }

    [Fact]
    public void BothOperationsValidateReadCommittedOwnershipBeforeSchemaOrCommandAccess()
    {
        var lockSet = ParseLockSet();
        var transactionValidationMethod = FindReadCommittedValidation(lockSet);
        var transactionValidationName = transactionValidationMethod.Identifier.ValueText;
        foreach (var methodName in new[] { "TryAcquireAdvisoryAsync", "AcquireRelationsAsync" })
        {
            var method = FindMethod(lockSet, methodName, parameterCount: 4);
            var timeout = Assert.Single(Invocations(method, "ThrowIfLessThan"));
            var nullChecks = Invocations(method, "ThrowIfNull")
                .OrderBy(call => call.SpanStart)
                .ToArray();
            var transactionValidation = Assert.Single(
                Invocations(method, transactionValidationName));
            var cancellation = Assert.Single(
                Invocations(method, "ThrowIfCancellationRequested"));
            var targetSchema = Assert.Single(Invocations(method, "GetRequiredTargetSchema"));
            var createCommand = Assert.Single(Invocations(method, "CreateCommand"));

            AssertInvocationArguments(timeout, "commandTimeoutSeconds", "1");
            Assert.Equal(2, nullChecks.Length);
            Assert.Equal(
                new[] { "connection", "transaction" },
                nullChecks.Select(OnlyArgument).ToArray());
            AssertInvocationArguments(transactionValidation, "connection", "transaction");
            Assert.Equal(
                "cancellationToken.ThrowIfCancellationRequested",
                cancellation.Expression.ToString());
            AssertInvocationArguments(targetSchema, "connection.ConnectionString");
            Assert.Equal("connection.CreateCommand", createCommand.Expression.ToString());

            Assert.True(timeout.SpanStart < nullChecks[0].SpanStart);
            Assert.True(nullChecks[^1].SpanStart < transactionValidation.SpanStart);
            Assert.True(transactionValidation.SpanStart < cancellation.SpanStart);
            Assert.True(cancellation.SpanStart < targetSchema.SpanStart);
            Assert.True(targetSchema.SpanStart < createCommand.SpanStart);

            Assert.IsType<ExpressionStatementSyntax>(DirectBodyStatement(method, timeout));
            Assert.All(
                nullChecks,
                call => Assert.IsType<ExpressionStatementSyntax>(DirectBodyStatement(method, call)));
            Assert.IsType<ExpressionStatementSyntax>(
                DirectBodyStatement(method, transactionValidation));
            Assert.IsType<ExpressionStatementSyntax>(DirectBodyStatement(method, cancellation));
            Assert.IsType<LocalDeclarationStatementSyntax>(DirectBodyStatement(method, targetSchema));
            Assert.IsType<TryStatementSyntax>(DirectBodyStatement(method, createCommand));

            var directStatements = method.Body!.Statements;
            Assert.Equal(
                new[] { 0, 1, 2, 3, 4, 5 },
                new SyntaxNode[]
                {
                    timeout,
                    nullChecks[0],
                    nullChecks[1],
                    transactionValidation,
                    cancellation,
                    targetSchema,
                }
                .Select(node => directStatements.IndexOf(DirectBodyStatement(method, node)))
                .ToArray());
            Assert.True(
                directStatements.IndexOf(DirectBodyStatement(method, targetSchema))
                < directStatements.IndexOf(DirectBodyStatement(method, createCommand)));
        }
    }

    [Fact]
    public void TransactionValidationProvesOpenExactOwnerReadyAndReadCommittedInOrder()
    {
        var validation = FindReadCommittedValidation(ParseLockSet());
        var openState = Assert.Single(
            validation.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            member => member.Expression.ToString() == "connection"
                      && member.Name.Identifier.ValueText == "State");
        var openGuard = Assert.IsType<BinaryExpressionSyntax>(openState.Parent);
        Assert.True(openGuard.IsKind(SyntaxKind.NotEqualsExpression));
        Assert.Equal("ConnectionState.Open", openGuard.Right.ToString());
        var transactionConnection = Assert.Single(
            validation.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            member => member.Expression.ToString() == "transaction"
                      && member.Name.Identifier.ValueText == "Connection");
        var ownershipGuard = Assert.Single(
            validation.Body!.Statements.OfType<IfStatementSyntax>(),
            statement => statement.Condition is PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LogicalNotExpression,
                Operand: InvocationExpressionSyntax invocation,
            }
            && InvocationName(invocation) == "ReferenceEquals");
        var ownershipComparison = Assert.IsType<InvocationExpressionSyntax>(
            Assert.IsType<PrefixUnaryExpressionSyntax>(ownershipGuard.Condition).Operand);
        AssertInvocationArguments(
            ownershipComparison,
            "transactionConnection",
            "connection");
        var isolationRead = Assert.Single(
            validation.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            member => member.Expression.ToString() == "transaction"
                      && member.Name.Identifier.ValueText == "IsolationLevel");
        var isolationAssignment = Assert.Single(
            validation.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            assignment => assignment.Right == isolationRead);
        var isolationVariable = isolationAssignment.Left.ToString();
        var isolationGuard = Assert.Single(
            validation.Body.Statements.OfType<IfStatementSyntax>(),
            statement => statement.Condition is BinaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.NotEqualsExpression,
            } comparison
            && comparison.Left.ToString() == isolationVariable
            && comparison.Right.ToString() == "IsolationLevel.ReadCommitted");
        var caughtTypes = validation.DescendantNodes().OfType<CatchFilterClauseSyntax>()
            .Select(filter => filter.FilterExpression.ToString())
            .ToArray();

        Assert.Equal(2, caughtTypes.Length);
        Assert.All(caughtTypes, filter =>
        {
            Assert.Contains("InvalidOperationException", filter, StringComparison.Ordinal);
            Assert.Contains("ObjectDisposedException", filter, StringComparison.Ordinal);
        });
        Assert.True(openState.SpanStart < transactionConnection.SpanStart);
        Assert.True(transactionConnection.SpanStart < ownershipGuard.SpanStart);
        Assert.True(ownershipGuard.SpanStart < isolationRead.SpanStart);
        Assert.True(isolationRead.SpanStart < isolationGuard.SpanStart);
    }

    [Fact]
    public void AdvisoryCommandBindsExactSqlTypesTransactionTimeoutAndResultShape()
    {
        var method = FindMethod(
            ParseLockSet(),
            "TryAcquireAdvisoryAsync",
            parameterCount: 4);

        AssertCommandBinding(method, AdvisorySqlField().Name);
        AssertNpgsqlParameters(
            method,
            ("@targetSchema", "NpgsqlDbType.Text", "targetSchema"),
            ("@namespaceSeed", "NpgsqlDbType.Bigint", "AdvisoryNamespaceSeed"));

        var execute = Assert.Single(Invocations(method, "ExecuteScalarAsync"));
        AssertInvocationArguments(execute, "cancellationToken");
        var shapeGuard = Assert.Single(
            method.DescendantNodes().OfType<IfStatementSyntax>(),
            statement => statement.Condition is IsPatternExpressionSyntax pattern
                         && pattern.Expression.ToString() == "scalar"
                         && pattern.Pattern is UnaryPatternSyntax
                         {
                             Pattern: DeclarationPatternSyntax
                             {
                                 Type: PredefinedTypeSyntax
                                 {
                                     Keyword.RawKind: (int)SyntaxKind.BoolKeyword,
                                 },
                             },
                         });
        var shapePattern = Assert.IsType<IsPatternExpressionSyntax>(shapeGuard.Condition);
        var notPattern = Assert.IsType<UnaryPatternSyntax>(shapePattern.Pattern);
        var declarationPattern = Assert.IsType<DeclarationPatternSyntax>(notPattern.Pattern);
        var designation = Assert.IsType<SingleVariableDesignationSyntax>(
            declarationPattern.Designation).Identifier.ValueText;
        Assert.True(execute.SpanStart < shapeGuard.SpanStart);
        Assert.Contains(
            method.Body!.Statements.OfType<ReturnStatementSyntax>(),
            statement => statement.Expression?.ToString() == designation);

        var cleanup = Assert.Single(Invocations(method, "DisposeReaderThenCommandAsync"));
        var shapeFailure = Assert.Single(
            shapeGuard.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>());
        Assert.Contains(
            "TransferV3Phase4FailureMapper.Sanitize",
            shapeFailure.Expression?.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "TransferV3Phase4Boundary.PostgreSqlCommand",
            shapeFailure.Expression?.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "cancellationToken",
            shapeFailure.Expression?.ToString(),
            StringComparison.Ordinal);
        Assert.True(cleanup.SpanStart < shapeGuard.SpanStart);
    }

    [Fact]
    public void RelationCommandBindsExactSharedSqlTransactionTimeoutAndNoParameters()
    {
        var method = FindMethod(
            ParseLockSet(),
            "AcquireRelationsAsync",
            parameterCount: 4);

        AssertCommandBinding(method, "BuildRelationLockSql(targetSchema)");
        Assert.Empty(Invocations(method, "AddNpgsqlParameter"));
        var execute = Assert.Single(Invocations(method, "ExecuteNonQueryAsync"));
        AssertInvocationArguments(execute, "cancellationToken");
    }

    [Fact]
    public void BothCommandsSanitizePrimaryBeforeExplicitPrimaryPreservingCleanup()
    {
        var lockSet = ParseLockSet();
        foreach (var methodName in new[] { "TryAcquireAdvisoryAsync", "AcquireRelationsAsync" })
        {
            var method = FindMethod(lockSet, methodName, parameterCount: 4);
            Assert.DoesNotContain(
                method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(),
                declaration => declaration.AwaitKeyword.RawKind != 0
                               && declaration.UsingKeyword.RawKind != 0);

            var execution = Assert.Single(
                method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                invocation => InvocationName(invocation) is
                    "ExecuteScalarAsync" or "ExecuteNonQueryAsync");
            var primaryCapture = Assert.Single(
                method.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
                assignment => assignment.Left.ToString() == "primaryFailure"
                              && assignment.Right.ToString().Contains(
                                  "TransferV3Phase4FailureMapper.Sanitize",
                                  StringComparison.Ordinal));
            var cleanup = Assert.Single(Invocations(method, "DisposeReaderThenCommandAsync"));
            AssertInvocationArguments(cleanup, "null", "command", "primaryFailure");

            Assert.True(execution.SpanStart < primaryCapture.SpanStart);
            Assert.True(primaryCapture.SpanStart < cleanup.SpanStart);
            Assert.Contains(
                "TransferV3Phase4Boundary.PostgreSqlCommand",
                primaryCapture.Right.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "cancellationToken",
                primaryCapture.Right.ToString(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ParameterHelperUsesTheSuppliedNpgsqlTypeAndValueWithoutInference()
    {
        var helper = FindMethod(ParseLockSet(), "AddNpgsqlParameter", parameterCount: 4);
        var add = Assert.Single(
            Invocations(helper, "Add"),
            invocation => invocation.Expression.ToString() == "command.Parameters.Add");
        AssertInvocationArguments(add, "name", "type");
        var value = Assert.Single(
            helper.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            assignment => assignment.Left.ToString() == "parameter.Value");
        Assert.Equal("value", value.Right.ToString());
        Assert.Empty(Invocations(helper, "AddWithValue"));
    }

    [Fact]
    public void LockSetNeverOwnsCallerConnectionOrTransactionLifecycle()
    {
        var source = ParseLockSet().ToFullString();

        Assert.DoesNotContain("new NpgsqlConnection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Open(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginTransaction", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(
                "\\.\\s*Commit(?:Async)?\\s*\\(",
                RegexOptions.CultureInvariant),
            source);
        Assert.DoesNotContain("Rollback", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Quarantine", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(
                "(?:connection|transaction)\\s*\\.\\s*(?:Close|CloseAsync|Dispose|DisposeAsync)\\s*\\(",
                RegexOptions.CultureInvariant),
            source);
    }

    private static string ExpectedRelationLockSql(string targetSchema)
    {
        var quotedSchema = QuoteIdentifier(targetSchema);
        var relations = ExpectedRelationNames.Select(name =>
            $"  {quotedSchema}.{QuoteIdentifier(name)}");
        return "LOCK TABLE\n"
               + string.Join(",\n", relations)
               + "\nIN EXCLUSIVE MODE NOWAIT";
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void AssertCommandBinding(
        MethodDeclarationSyntax method,
        string expectedCommandText)
    {
        AssertSingleAssignment(method, "command.Transaction", "transaction");
        AssertSingleAssignment(method, "command.CommandTimeout", "commandTimeoutSeconds");
        AssertSingleAssignment(method, "command.CommandText", expectedCommandText);
        var createCommand = Assert.Single(Invocations(method, "CreateCommand"));
        AssertInvocationArguments(createCommand);
        Assert.Equal("connection.CreateCommand", createCommand.Expression.ToString());
    }

    private static void AssertNpgsqlParameters(
        MethodDeclarationSyntax method,
        params (string Name, string Type, string Value)[] expected)
    {
        var calls = Invocations(method, "AddNpgsqlParameter")
            .OrderBy(call => call.SpanStart)
            .ToArray();
        Assert.Equal(expected.Length, calls.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            AssertInvocationArguments(
                calls[index],
                "command",
                $"\"{expected[index].Name}\"",
                expected[index].Type,
                expected[index].Value);
        }
    }

    private static void AssertSingleAssignment(
        SyntaxNode node,
        string target,
        string value)
    {
        var assignment = Assert.Single(
            node.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            candidate => candidate.Left.ToString() == target);
        Assert.Equal(value, assignment.Right.ToString());
    }

    private static void AssertMethod(
        string name,
        Type returnType,
        params Type[] parameterTypes)
    {
        var method = typeof(TransferV3PostgreSqlAdmissionLockSet).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            parameterTypes,
            modifiers: null);
        Assert.NotNull(method);
        Assert.Equal(returnType, method.ReturnType);
        Assert.True(method.IsAssembly);
    }

    private static (string Name, string Sql) AdvisorySqlField()
    {
        var expected =
            """
            SELECT pg_catalog.pg_try_advisory_xact_lock(
                pg_catalog.hashtextextended(
                    @targetSchema,
                    pg_catalog.hashtextextended(
                        pg_catalog.current_database(),
                        @namespaceSeed)))
            """;
        var field = Assert.Single(
            typeof(TransferV3PostgreSqlAdmissionLockSet).GetFields(
                BindingFlags.Static | BindingFlags.NonPublic),
            candidate => candidate.IsLiteral
                         && candidate.FieldType == typeof(string)
                         && string.Equals(
                             candidate.GetRawConstantValue() as string,
                             expected,
                             StringComparison.Ordinal));
        Assert.True(field.IsLiteral);
        return (field.Name, Assert.IsType<string>(field.GetRawConstantValue()));
    }

    private static ClassDeclarationSyntax ParseLockSet()
    {
        var path = SqliteContractTestSupport.AbsolutePath(LockSetSourcePath);
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: LockSetSourcePath);
        var errors = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
        return tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText
                            == nameof(TransferV3PostgreSqlAdmissionLockSet));
    }

    private static MethodDeclarationSyntax FindMethod(
        ClassDeclarationSyntax type,
        string name,
        int parameterCount) =>
        type.Members.OfType<MethodDeclarationSyntax>().Single(method =>
            method.Identifier.ValueText == name
            && method.ParameterList.Parameters.Count == parameterCount);

    private static MethodDeclarationSyntax FindReadCommittedValidation(
        ClassDeclarationSyntax type) =>
        Assert.Single(
            type.Members.OfType<MethodDeclarationSyntax>(),
            method => method.ParameterList.Parameters.Count == 2
                      && method.ToFullString().Contains(
                          "IsolationLevel.ReadCommitted",
                          StringComparison.Ordinal)
                      && Invocations(method, "ReferenceEquals").Any());

    private static StatementSyntax DirectBodyStatement(
        MethodDeclarationSyntax method,
        SyntaxNode node) =>
        Assert.Single(
            node.AncestorsAndSelf().OfType<StatementSyntax>(),
            statement => ReferenceEquals(statement.Parent, method.Body));

    private static IEnumerable<InvocationExpressionSyntax> Invocations(
        SyntaxNode node,
        string name) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) == name);

    private static string InvocationName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => string.Empty,
        };

    private static void AssertInvocationArguments(
        InvocationExpressionSyntax invocation,
        params string[] expected)
    {
        Assert.Equal(
            expected,
            invocation.ArgumentList.Arguments
                .Select(argument => argument.Expression.ToString())
                .ToArray());
    }

    private static string OnlyArgument(InvocationExpressionSyntax invocation) =>
        Assert.Single(invocation.ArgumentList.Arguments).Expression.ToString();
}
