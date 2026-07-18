using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Npgsql;
using NzbWebDAV.Database;

namespace backend.Tests.Database;

public sealed class PostgreSqlNativeMigrationContractTests
{
    private const string BaselineMigrationId =
        "20260712000000_PostgreSqlNativeBaseline";
    private const string OperationalMigrationId =
        "20260712000100_PostgreSqlOperationalTriggers";
    private const string ProductVersion = "10.0.9";
    private const string Canary = "MIGRATION-CONTRACT-CANARY-DO-NOT-ECHO";

    [Fact]
    public void HeadPublishesOnlyTheExactImmutableReviewedRows()
    {
        var head = PostgreSqlNativeMigrationContract.Head;

        Assert.Equal(ExactHead(), head);
        Assert.IsNotType<PostgreSqlMigrationHistoryEntry[]>(head);

        if (head is IList<PostgreSqlMigrationHistoryEntry> list)
        {
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list[0] =
                new PostgreSqlMigrationHistoryEntry(Canary, Canary));
        }

        Assert.Equal(ExactHead(), PostgreSqlNativeMigrationContract.Head);
    }

    [Fact]
    public void ValidatePrefixAcceptsOnlyTheThreeExactOrderedPrefixes()
    {
        PostgreSqlNativeMigrationContract.ValidatePrefix(
            Array.Empty<PostgreSqlMigrationHistoryEntry>());
        PostgreSqlNativeMigrationContract.ValidatePrefix(
            [new PostgreSqlMigrationHistoryEntry(BaselineMigrationId, ProductVersion)]);
        PostgreSqlNativeMigrationContract.ValidatePrefix(ExactHead());
    }

    [Fact]
    public void ValidateHeadAcceptsTheExactReviewedHead()
    {
        PostgreSqlNativeMigrationContract.ValidateHead(ExactHead());
    }

    [Fact]
    public void ValidatePrefixRejectsWrongIdsVersionsAndOrder()
    {
        var rejected = new PostgreSqlMigrationHistoryEntry[][]
        {
            [new(Canary, ProductVersion)],
            [new(BaselineMigrationId, Canary)],
            [new(BaselineMigrationId.ToLowerInvariant(), ProductVersion)],
            [new(BaselineMigrationId, "10.0.8")],
            [new($" {BaselineMigrationId}", ProductVersion)],
            [new(BaselineMigrationId, $"{ProductVersion} ")],
            [new(BaselineMigrationId.Replace('P', '\uFF30'), ProductVersion)],
            [new(BaselineMigrationId, "\uFF11\uFF10.0.9")],
            [
                new(BaselineMigrationId, ProductVersion),
                new(Canary, ProductVersion)
            ],
            [
                new(BaselineMigrationId, ProductVersion),
                new(OperationalMigrationId, Canary)
            ],
            [
                new(OperationalMigrationId, ProductVersion),
                new(BaselineMigrationId, ProductVersion)
            ]
        };

        Assert.All(rejected, rows =>
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => PostgreSqlNativeMigrationContract.ValidatePrefix(rows));
            Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ValidatePrefixRejectsDuplicateAndExtraRows()
    {
        var duplicate = new PostgreSqlMigrationHistoryEntry(BaselineMigrationId, ProductVersion);
        var rejected = new PostgreSqlMigrationHistoryEntry[][]
        {
            [duplicate, duplicate],
            [.. ExactHead(), new PostgreSqlMigrationHistoryEntry(Canary, ProductVersion)]
        };

        Assert.All(rejected, rows =>
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => PostgreSqlNativeMigrationContract.ValidatePrefix(rows));
            Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ValidateHeadRejectsMissingExtraDuplicateAndReorderedRows()
    {
        var rejected = new PostgreSqlMigrationHistoryEntry[][]
        {
            [],
            [new(BaselineMigrationId, ProductVersion)],
            [
                new(OperationalMigrationId, ProductVersion),
                new(BaselineMigrationId, ProductVersion)
            ],
            [
                new(BaselineMigrationId, ProductVersion),
                new(BaselineMigrationId, ProductVersion)
            ],
            [.. ExactHead(), new PostgreSqlMigrationHistoryEntry(Canary, ProductVersion)]
        };

        Assert.All(rejected, rows =>
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => PostgreSqlNativeMigrationContract.ValidateHead(rows));
            Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ValidateHeadRejectsWrongIdsAndProductVersions()
    {
        var rejected = new PostgreSqlMigrationHistoryEntry[][]
        {
            [
                new(BaselineMigrationId, "10.0.8"),
                new(OperationalMigrationId, ProductVersion)
            ],
            [
                new(BaselineMigrationId, ProductVersion),
                new(OperationalMigrationId, Canary)
            ],
            [
                new(BaselineMigrationId, ProductVersion),
                new(Canary, ProductVersion)
            ]
        };

        Assert.All(rejected, rows =>
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => PostgreSqlNativeMigrationContract.ValidateHead(rows));
            Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ValidatorsRejectNullCollectionsEntriesAndFields()
    {
        Assert.Equal(
            "capturedRows",
            Assert.Throws<ArgumentNullException>(
                () => PostgreSqlNativeMigrationContract.ValidatePrefix(null!)).ParamName);
        Assert.Equal(
            "capturedRows",
            Assert.Throws<ArgumentNullException>(
                () => PostgreSqlNativeMigrationContract.ValidateHead(null!)).ParamName);

        var rejected = new PostgreSqlMigrationHistoryEntry[][]
        {
            [null!],
            [new(null!, ProductVersion)],
            [new(BaselineMigrationId, null!)],
            [new(string.Empty, ProductVersion)],
            [new(BaselineMigrationId, string.Empty)]
        };

        Assert.All(rejected, rows =>
            Assert.Throws<InvalidOperationException>(
                () => PostgreSqlNativeMigrationContract.ValidatePrefix(rows)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task LiveReadersRejectNonpositiveTimeoutBeforeProviderAccess(
        int commandTimeoutSeconds)
    {
        var captureError = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PostgreSqlNativeMigrationContract.CaptureAsync(
                null!,
                null!,
                commandTimeoutSeconds,
                CancellationToken.None));
        var prefixError = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PostgreSqlNativeMigrationContract.ValidatePrefixAsync(
                null!,
                null!,
                commandTimeoutSeconds,
                CancellationToken.None));
        var headError = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PostgreSqlNativeMigrationContract.ValidateHeadAsync(
                null!,
                null!,
                commandTimeoutSeconds,
                CancellationToken.None));
        var catalogError = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PostgreSqlPhysicalCatalogContract.ValidateAsync(
                null!,
                null!,
                PostgreSqlCatalogState.Head,
                commandTimeoutSeconds,
                CancellationToken.None));

        Assert.Equal("commandTimeoutSeconds", captureError.ParamName);
        Assert.Equal("commandTimeoutSeconds", prefixError.ParamName);
        Assert.Equal("commandTimeoutSeconds", headError.ParamName);
        Assert.Equal("commandTimeoutSeconds", catalogError.ParamName);
    }

    [Fact]
    public async Task LiveReadersRejectNullConnectionWithPositiveTimeout()
    {
        var captureError = await Assert.ThrowsAsync<ArgumentNullException>(
            () => PostgreSqlNativeMigrationContract.CaptureAsync(
                null!,
                null!,
                1,
                CancellationToken.None));
        var prefixError = await Assert.ThrowsAsync<ArgumentNullException>(
            () => PostgreSqlNativeMigrationContract.ValidatePrefixAsync(
                null!,
                null!,
                1,
                CancellationToken.None));
        var headError = await Assert.ThrowsAsync<ArgumentNullException>(
            () => PostgreSqlNativeMigrationContract.ValidateHeadAsync(
                null!,
                null!,
                1,
                CancellationToken.None));
        var catalogError = await Assert.ThrowsAsync<ArgumentNullException>(
            () => PostgreSqlPhysicalCatalogContract.ValidateAsync(
                null!,
                null!,
                PostgreSqlCatalogState.Head,
                1,
                CancellationToken.None));

        Assert.Equal("connection", captureError.ParamName);
        Assert.Equal("connection", prefixError.ParamName);
        Assert.Equal("connection", headError.ParamName);
        Assert.Equal("connection", catalogError.ParamName);
    }

    [Fact]
    public async Task LiveReadersRejectNullTransactionBeforeClosedConnectionState()
    {
        await using var connection = new NpgsqlConnection();

        var captureError = await Assert.ThrowsAsync<ArgumentNullException>(
            () => PostgreSqlNativeMigrationContract.CaptureAsync(
                connection,
                null!,
                1,
                CancellationToken.None));
        var catalogError = await Assert.ThrowsAsync<ArgumentNullException>(
            () => PostgreSqlPhysicalCatalogContract.ValidateAsync(
                connection,
                null!,
                PostgreSqlCatalogState.Head,
                1,
                CancellationToken.None));

        Assert.Equal("transaction", captureError.ParamName);
        Assert.Equal("transaction", catalogError.ParamName);
    }

    [Fact]
    public void ContractSurfaceHasOnlyTheLockedHistoryOperations()
    {
        var type = typeof(PostgreSqlNativeMigrationContract);
        var entryType = typeof(PostgreSqlMigrationHistoryEntry);

        Assert.True(entryType.IsSealed);
        var entryProperties = entryType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        Assert.Equal(
            ["MigrationId", "ProductVersion"],
            entryProperties
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.All(entryProperties, property =>
        {
            Assert.NotNull(property.SetMethod);
            Assert.Contains(
                typeof(System.Runtime.CompilerServices.IsExternalInit),
                property.SetMethod.ReturnParameter.GetRequiredCustomModifiers());
        });

        var sourcePath = SqliteContractTestSupport.AbsolutePath(
            "backend/Database/PostgreSqlNativeMigrationContract.cs");
        var sourceRoot = CSharpSyntaxTree.ParseText(
                File.ReadAllText(sourcePath),
                path: sourcePath)
            .GetRoot();
        var entryDeclaration = Assert.Single(
            sourceRoot.DescendantNodes().OfType<RecordDeclarationSyntax>(),
            declaration => declaration.Identifier.ValueText
                           == "PostgreSqlMigrationHistoryEntry");
        Assert.Contains(entryDeclaration.Modifiers, modifier =>
            modifier.IsKind(SyntaxKind.SealedKeyword));
        Assert.Equal(
            typeof(IReadOnlyList<PostgreSqlMigrationHistoryEntry>),
            type.GetProperty("Head", BindingFlags.Static | BindingFlags.NonPublic)!.PropertyType);

        AssertMethod(
            type,
            "ValidatePrefix",
            typeof(void),
            typeof(IReadOnlyList<PostgreSqlMigrationHistoryEntry>));
        AssertMethod(
            type,
            "ValidateHead",
            typeof(void),
            typeof(IReadOnlyList<PostgreSqlMigrationHistoryEntry>));
        AssertMethod(
            type,
            "CaptureAsync",
            typeof(Task<IReadOnlyList<PostgreSqlMigrationHistoryEntry>>),
            typeof(NpgsqlConnection),
            typeof(NpgsqlTransaction),
            typeof(int),
            typeof(CancellationToken));
        AssertMethod(
            type,
            "ValidatePrefixAsync",
            typeof(Task),
            typeof(NpgsqlConnection),
            typeof(NpgsqlTransaction),
            typeof(int),
            typeof(CancellationToken));
        AssertMethod(
            type,
            "ValidateHeadAsync",
            typeof(Task),
            typeof(NpgsqlConnection),
            typeof(NpgsqlTransaction),
            typeof(int),
            typeof(CancellationToken));
    }

    [Fact]
    public void CaptureQueryReadsBothColumnsInCOrderWithoutSuppressingCardinality()
    {
        var contract = ParseClass(
            "backend/Database/PostgreSqlNativeMigrationContract.cs",
            "PostgreSqlNativeMigrationContract");
        var capture = FindMethod(contract, "CaptureAsync", parameterCount: 4);
        var query = Assert.Single(
            StringExpressions(contract),
            candidate => candidate.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                         && candidate.Contains("\"MigrationId\"", StringComparison.Ordinal)
                         && candidate.Contains("\"ProductVersion\"", StringComparison.Ordinal));

        Assert.Contains("ORDER BY", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"MigrationId\" COLLATE \"C\"", query, StringComparison.Ordinal);
        Assert.Contains("\"ProductVersion\" COLLATE \"C\"", query, StringComparison.Ordinal);
        Assert.True(
            query.Contains("__EFMigrationsHistory_PostgreSql", StringComparison.Ordinal)
            || query.Contains(
                "DatabaseMigrationPolicy.PostgreSqlHistoryTableName",
                StringComparison.Ordinal),
            "The reviewed query must read the native EF history relation.");
        Assert.DoesNotContain("LIMIT", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SingleRow", capture.ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Take(", capture.ToFullString(), StringComparison.Ordinal);

        var contractSource = contract.ToFullString();
        Assert.DoesNotContain("ToHashSet", contractSource, StringComparison.Ordinal);
        Assert.DoesNotContain("HashSet<", contractSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetEquals", contractSource, StringComparison.Ordinal);

        var readerMethods = contract.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(method => Invocations(method, "ExecuteReaderAsync").Any())
            .ToArray();
        Assert.Single(readerMethods);
        Assert.Equal("CaptureAsync", readerMethods[0].Identifier.ValueText);
    }

    [Fact]
    public void CaptureBindsItsCallerTransactionAndTimeoutBeforeReading()
    {
        var contract = ParseClass(
            "backend/Database/PostgreSqlNativeMigrationContract.cs",
            "PostgreSqlNativeMigrationContract");
        var capture = FindMethod(contract, "CaptureAsync", parameterCount: 4);
        var execution = Assert.Single(Invocations(capture, "ExecuteReaderAsync"));
        AssertArgumentsContain(execution, "cancellationToken");
        var commandReceiver = Assert.IsType<MemberAccessExpressionSyntax>(execution.Expression)
            .Expression.ToString();
        var transactionAssignment = FindAssignment(
            capture,
            commandReceiver,
            "Transaction",
            "transaction");
        var timeoutAssignment = FindAssignment(
            capture,
            commandReceiver,
            "CommandTimeout",
            "commandTimeoutSeconds");
        var read = Assert.Single(Invocations(capture, "ReadAsync"));
        AssertArgumentsContain(read, "cancellationToken");
        var readerLoop = read.Ancestors().FirstOrDefault(node =>
            node is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax);

        Assert.True(transactionAssignment.SpanStart < execution.SpanStart);
        Assert.True(timeoutAssignment.SpanStart < execution.SpanStart);
        Assert.NotNull(readerLoop);
        Assert.DoesNotContain(readerLoop!.DescendantNodes(), node =>
            node is BreakStatementSyntax or ReturnStatementSyntax);
        var thirdRowGuard = Assert.Single(
            readerLoop.DescendantNodes().OfType<IfStatementSyntax>(),
            statement => statement.Condition.ToString().Contains(".Count", StringComparison.Ordinal)
                         && statement.Condition.ToString().Contains("Head.Count", StringComparison.Ordinal));
        Assert.Contains(
            thirdRowGuard.DescendantNodesAndSelf(),
            node => node is ThrowStatementSyntax);

        var add = Assert.Single(Invocations(readerLoop, "Add"));
        var capturedRows = Assert.IsType<MemberAccessExpressionSyntax>(add.Expression)
            .Expression.ToString();
        var entryCreation = Assert.IsAssignableFrom<BaseObjectCreationExpressionSyntax>(
            Assert.Single(add.ArgumentList.Arguments).Expression);
        if (entryCreation is ObjectCreationExpressionSyntax explicitCreation)
        {
            Assert.Equal(
                "PostgreSqlMigrationHistoryEntry",
                BaseTypeName(explicitCreation.Type));
        }
        else
        {
            Assert.Contains(
                "List<PostgreSqlMigrationHistoryEntry>",
                capture.ToFullString(),
                StringComparison.Ordinal);
        }

        Assert.Equal(
            [0, 1],
            entryCreation.ArgumentList!.Arguments
                .Select(argument => ResolveReaderOrdinal(readerLoop, argument.Expression)));
        var result = Assert.Single(
            capture.DescendantNodes().OfType<ReturnStatementSyntax>(),
            statement => statement.Expression is not null);
        Assert.Contains(capturedRows, result.Expression!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            capture.DescendantNodes().OfType<ReturnStatementSyntax>(),
            statement => statement.Expression?.ToString().Contains("Head", StringComparison.Ordinal)
                         == true);

        Assert.Contains(
            Invocations(capture, "ValidateTransactionContext"),
            _ => true);
        var contractSource = contract.ToFullString();
        Assert.Contains("ConnectionState.Open", contractSource, StringComparison.Ordinal);
        Assert.Contains("transaction.Connection", contractSource, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals", contractSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureOwnsNeitherTheConnectionNorTheTransaction()
    {
        var contract = ParseClass(
            "backend/Database/PostgreSqlNativeMigrationContract.cs",
            "PostgreSqlNativeMigrationContract");
        var capture = FindMethod(contract, "CaptureAsync", parameterCount: 4);
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "Open",
            "OpenAsync",
            "BeginTransaction",
            "BeginTransactionAsync",
            "Commit",
            "CommitAsync",
            "Rollback",
            "RollbackAsync",
            "Close",
            "CloseAsync",
            "Dispose",
            "DisposeAsync"
        };

        Assert.DoesNotContain(
            capture.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression is MemberAccessExpressionSyntax member
                          && (member.Expression.ToString() is "connection" or "transaction")
                          && forbidden.Contains(member.Name.Identifier.ValueText));
        Assert.DoesNotContain(
            capture.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(),
            declaration => declaration.UsingKeyword.RawKind != 0
                           && declaration.Declaration.Variables.Any(variable =>
                               variable.Initializer?.Value.ToString() is "connection" or "transaction"));
    }

    [Fact]
    public void AsyncValidatorsUseCaptureAsTheirOnlyLiveReader()
    {
        var contract = ParseClass(
            "backend/Database/PostgreSqlNativeMigrationContract.cs",
            "PostgreSqlNativeMigrationContract");

        AssertAsyncValidatorUsesCapture(contract, "ValidatePrefixAsync", "ValidatePrefix");
        AssertAsyncValidatorUsesCapture(contract, "ValidateHeadAsync", "ValidateHead");
    }

    [Fact]
    public void NativeMigratorUsesSharedHistoryContractAndNoPrivateIdOnlyDefinition()
    {
        var path = SqliteContractTestSupport.AbsolutePath(
            "backend/Database/PostgreSqlNativeMigrator.cs");
        var source = File.ReadAllText(path);
        var migrator = ParseClass(
            "backend/Database/PostgreSqlNativeMigrator.cs",
            "PostgreSqlNativeMigrator");

        Assert.DoesNotContain("MigrationIds", source, StringComparison.Ordinal);
        Assert.Contains(
            migrator.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "CaptureAsync"
                          && invocation.Expression.ToString().Contains(
                              "PostgreSqlNativeMigrationContract",
                              StringComparison.Ordinal));
        Assert.Contains(
            migrator.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ValidatePrefix"
                          && invocation.Expression.ToString().Contains(
                              "PostgreSqlNativeMigrationContract",
                              StringComparison.Ordinal));
        Assert.Contains(
            migrator.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ValidateHeadAsync"
                          && invocation.Expression.ToString().Contains(
                              "PostgreSqlNativeMigrationContract",
                              StringComparison.Ordinal));

        Assert.DoesNotContain(
            StringExpressions(migrator),
            query => query.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                     && query.Contains("\"MigrationId\"", StringComparison.Ordinal)
                     && query.Contains("FROM", StringComparison.OrdinalIgnoreCase)
                     && !query.Contains("\"ProductVersion\"", StringComparison.Ordinal));

        var catalogCalls = migrator.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) == "ValidateAsync"
                                 && invocation.Expression.ToString().Contains(
                                     "PostgreSqlPhysicalCatalogContract",
                                     StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(catalogCalls);
        Assert.All(catalogCalls, call =>
        {
            Assert.Equal(5, call.ArgumentList.Arguments.Count);
            Assert.Equal("transaction", call.ArgumentList.Arguments[1].Expression.ToString());
            AssertProvablyPositiveTimeout(call.ArgumentList.Arguments[3].Expression);
        });
    }

    [Fact]
    public void NativeMigratorFencesPreflightAndFinalValidationWithDistinctReadOnlySnapshots()
    {
        var migrator = ParseClass(
            "backend/Database/PostgreSqlNativeMigrator.cs",
            "PostgreSqlNativeMigrator");
        var allInvocations = migrator.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .ToArray();
        var beginTransactions = allInvocations
            .Where(invocation => InvocationName(invocation) == "BeginTransactionAsync")
            .ToArray();
        var commits = allInvocations
            .Where(invocation => InvocationName(invocation) == "CommitAsync")
            .ToArray();

        Assert.NotEmpty(beginTransactions);
        Assert.All(beginTransactions, begin => Assert.Contains(
            "IsolationLevel.RepeatableRead",
            begin.ArgumentList.ToString(),
            StringComparison.Ordinal));
        Assert.NotEmpty(commits);

        if (beginTransactions.Length == 1)
        {
            var owner = beginTransactions[0].Ancestors().OfType<MethodDeclarationSyntax>().Single();
            var migrationEntryPoint = FindMethod(
                migrator,
                "MigrateOpenConnectionAsync",
                parameterCount: 2);
            var efMigration = Assert.Single(
                Invocations(migrationEntryPoint, "MigrateAsync"));
            var validationPhases = Invocations(
                    migrationEntryPoint,
                    owner.Identifier.ValueText)
                .OrderBy(invocation => invocation.SpanStart)
                .ToArray();

            Assert.True(
                allInvocations.Count(invocation => InvocationName(invocation)
                    == owner.Identifier.ValueText) >= 2,
                "A shared transaction helper must be invoked separately for preflight and final validation.");
            Assert.Equal(2, validationPhases.Length);
            Assert.True(validationPhases[0].SpanStart < efMigration.SpanStart);
            Assert.True(efMigration.SpanStart < validationPhases[1].SpanStart);
            Assert.All(validationPhases, phase => Assert.Contains(
                phase.Ancestors(),
                ancestor => ancestor is AwaitExpressionSyntax));
            Assert.Contains(
                Invocations(validationPhases[0], "PreflightAsync"),
                _ => true);
            var preflight = FindMethod(migrator, "PreflightAsync", parameterCount: 7);
            Assert.Contains(
                Invocations(preflight, "CaptureAsync"),
                _ => true);
            Assert.Contains(
                Invocations(preflight, "ValidatePrefix"),
                _ => true);
            Assert.Contains(
                Invocations(validationPhases[1], "ValidateHeadAsync"),
                _ => true);
            Assert.Contains(
                owner.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                invocation => InvocationName(invocation) == "CommitAsync");
        }
        else
        {
            Assert.True(
                commits.Length >= beginTransactions.Length,
                "Every explicit validation transaction must commit before migration continues.");
        }

        var readOnlyMethod = Assert.Single(
            migrator.Members.OfType<MethodDeclarationSyntax>(),
            method => StringExpressions(method).Any(value =>
                value.Contains("SET TRANSACTION READ ONLY", StringComparison.OrdinalIgnoreCase)));
        var readOnlyExecution = Assert.Single(
            readOnlyMethod.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) is "ExecuteNonQueryAsync" or "ExecuteScalarAsync");
        var transactionAssignment = FindAssignment(
            readOnlyMethod,
            Assert.IsType<MemberAccessExpressionSyntax>(readOnlyExecution.Expression)
                .Expression.ToString(),
            "Transaction",
            "transaction");
        var timeoutAssignment = FindAssignment(
            readOnlyMethod,
            Assert.IsType<MemberAccessExpressionSyntax>(readOnlyExecution.Expression)
                .Expression.ToString(),
            "CommandTimeout",
            "commandTimeoutSeconds");

        Assert.True(transactionAssignment.SpanStart < readOnlyExecution.SpanStart);
        Assert.True(timeoutAssignment.SpanStart < readOnlyExecution.SpanStart);

        var headCalls = allInvocations
            .Where(invocation => InvocationName(invocation) == "ValidateHeadAsync")
            .ToArray();
        Assert.Single(headCalls);
        AssertUsesProvablyPositiveTimeout(headCalls[0]);
    }

    [Fact]
    public void EveryTransactionBoundContractProbesPinnedNpgsqlTransactionReadiness()
    {
        var contracts = new[]
        {
            ParseClass(
                "backend/Database/PostgreSqlNativeMigrationContract.cs",
                "PostgreSqlNativeMigrationContract"),
            ParseClass(
                "backend/Database/PostgreSqlFreshBootstrapContract.cs",
                "PostgreSqlFreshBootstrapContract"),
            ParseClass(
                "backend/Database/PostgreSqlPhysicalCatalogContract.cs",
                "PostgreSqlPhysicalCatalogContract")
        };

        Assert.All(contracts, contract =>
        {
            var validation = FindMethod(
                contract,
                "ValidateTransactionContext",
                parameterCount: 3);
            var readinessProbe = Assert.Single(
                validation.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
                member => member.Expression.ToString() == "transaction"
                          && member.Name.Identifier.ValueText == "IsolationLevel");
            var ownershipGuard = Assert.Single(
                validation.Body!.Statements.OfType<IfStatementSyntax>(),
                statement => statement.Condition is PrefixUnaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.LogicalNotExpression,
                    Operand: InvocationExpressionSyntax invocation
                }
                && InvocationName(invocation) == "ReferenceEquals");
            var logicalNot = Assert.IsType<PrefixUnaryExpressionSyntax>(
                ownershipGuard.Condition);
            var ownershipProbe = Assert.IsType<InvocationExpressionSyntax>(
                logicalNot.Operand);
            Assert.Collection(
                ownershipProbe.ArgumentList.Arguments,
                argument => Assert.Equal("transactionConnection", argument.Expression.ToString()),
                argument => Assert.Equal("connection", argument.Expression.ToString()));
            var ownershipFailure = Assert.IsType<ThrowStatementSyntax>(
                ownershipGuard.Statement);
            Assert.Equal(
                "TransactionFailure",
                InvocationName(Assert.IsType<InvocationExpressionSyntax>(
                    ownershipFailure.Expression)));
            var readinessTry = Assert.Single(
                validation.Body.Statements.OfType<TryStatementSyntax>(),
                statement => statement.Block.DescendantNodes()
                    .Contains(readinessProbe));
            Assert.True(ownershipGuard.SpanStart < readinessTry.SpanStart);
        });
    }

    [Fact]
    public void NativeHistoryRelationIsLockedAndRevalidatedBeforeEachHistoryRead()
    {
        var migrator = ParseClass(
            "backend/Database/PostgreSqlNativeMigrator.cs",
            "PostgreSqlNativeMigrator");
        var preflight = FindMethod(migrator, "PreflightAsync", parameterCount: 7);
        Assert.Empty(Invocations(preflight, "ScalarAsync"));
        var preflightLock = Assert.Single(Invocations(preflight, "LockHistoryTableAsync"));
        var preflightShape = Assert.Single(Invocations(preflight, "ValidateHistoryTableShapeAsync"));
        var preflightHistory = Assert.Single(
            Invocations(preflight, "CaptureAsync"),
            invocation => invocation.Expression.ToString().Contains(
                "PostgreSqlNativeMigrationContract",
                StringComparison.Ordinal));
        Assert.True(preflightLock.SpanStart < preflightShape.SpanStart);
        Assert.True(preflightShape.SpanStart < preflightHistory.SpanStart);

        var entryPoint = FindMethod(
            migrator,
            "MigrateOpenConnectionAsync",
            parameterCount: 2);
        var head = Assert.Single(Invocations(entryPoint, "ValidateHeadAsync"));
        var finalValidation = head.Ancestors()
            .OfType<AnonymousFunctionExpressionSyntax>()
            .First();
        var finalLock = Assert.Single(Invocations(finalValidation, "LockHistoryTableAsync"));
        var finalShape = Assert.Single(
            Invocations(finalValidation, "ValidateHistoryTableShapeAsync"));
        Assert.True(finalLock.SpanStart < finalShape.SpanStart);
        Assert.True(finalShape.SpanStart < head.SpanStart);

        var historyExists = Assert.Single(Invocations(entryPoint, "HistoryTableExistsAsync"));
        var firstValidation = Invocations(entryPoint, "RunReadValidationAsync")
            .OrderBy(invocation => invocation.SpanStart)
            .First();
        Assert.True(historyExists.SpanStart < firstValidation.SpanStart);

        var lockMethod = FindMethod(
            migrator,
            "LockHistoryTableAsync",
            parameterCount: 4);
        var lockSource = lockMethod.ToFullString();
        Assert.Contains("LOCK TABLE", lockSource, StringComparison.Ordinal);
        Assert.Contains(
            "__EFMigrationsHistory_PostgreSql",
            lockSource,
            StringComparison.Ordinal);
        Assert.Contains("IN SHARE MODE", lockSource, StringComparison.Ordinal);
        AssertEveryExecutedCommandIsBound(lockMethod);
    }

    [Fact]
    public void AdvisoryAcquireFailureQuarantinesThePossiblyLockedSession()
    {
        var migrator = ParseClass(
            "backend/Database/PostgreSqlNativeMigrator.cs",
            "PostgreSqlNativeMigrator");
        var ownedAcquire = FindMethod(
            migrator,
            "AcquireAdvisoryLockOwnedAsync",
            parameterCount: 5);
        Assert.Single(Invocations(ownedAcquire, "AcquireAdvisoryLockAsync"));
        var catchClause = Assert.Single(
            ownedAcquire.DescendantNodes().OfType<CatchClauseSyntax>());
        Assert.Contains(
            Invocations(catchClause, "QuarantineConnectionAsync"),
            _ => true);
        Assert.Contains(
            catchClause.DescendantNodes().OfType<ThrowStatementSyntax>(),
            _ => true);

        var entryPoint = FindMethod(
            migrator,
            "MigrateOpenConnectionAsync",
            parameterCount: 2);
        Assert.Single(Invocations(entryPoint, "AcquireAdvisoryLockOwnedAsync"));
        Assert.Empty(Invocations(entryPoint, "AcquireAdvisoryLockAsync"));
    }

    [Fact]
    public void NativeCommandHelpersPreservePrimaryFailuresAndSeparateCleanupEvidence()
    {
        var path = SqliteContractTestSupport.AbsolutePath(
            "backend/Database/PostgreSqlNativeMigrator.cs");
        var source = File.ReadAllText(path);
        var migrator = ParseClass(
            "backend/Database/PostgreSqlNativeMigrator.cs",
            "PostgreSqlNativeMigrator");
        var commandOwners = migrator.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(method => Invocations(method, "CreateCommand").Any())
            .ToArray();

        Assert.NotEmpty(commandOwners);
        Assert.All(commandOwners, method =>
        {
            Assert.Contains(
                Invocations(method, "DisposeReaderThenCommandAsync"),
                _ => true);
            Assert.DoesNotContain(
                method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(),
                declaration => declaration.AwaitKeyword.RawKind != 0
                               && declaration.UsingKeyword.RawKind != 0);
        });
        Assert.Contains("PostgreSqlTransactionRollbackFailure", source, StringComparison.Ordinal);
        Assert.Contains("PostgreSqlTransactionDisposeFailure", source, StringComparison.Ordinal);
        Assert.Contains("PostgreSqlAdvisoryAcquireCleanupFailure", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationalMigrationDirectEfGuardRequiresExactBaselineProductVersion()
    {
        var migration = ParseClass(
            "backend/Database/PostgreSqlMigrations/20260712000100_PostgreSqlOperationalTriggers.cs",
            "PostgreSqlOperationalTriggers");
        var up = FindMethod(migration, "Up", parameterCount: 1);
        var sql = Assert.Single(
            StringExpressions(up),
            value => value.Contains("__EFMigrationsHistory_PostgreSql", StringComparison.Ordinal));

        Assert.Contains(BaselineMigrationId, sql, StringComparison.Ordinal);
        Assert.Contains(ProductVersion, sql, StringComparison.Ordinal);
        Assert.Contains("\"MigrationId\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"ProductVersion\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"MigrationId\" = %L", sql, StringComparison.Ordinal);
        Assert.Contains("\"ProductVersion\" = %L", sql, StringComparison.Ordinal);

        var guardStart = sql.IndexOf("'SELECT count(*)", StringComparison.Ordinal);
        var guardEnd = sql.IndexOf("FROM %I.%I'", guardStart, StringComparison.Ordinal);
        Assert.True(guardStart >= 0 && guardEnd > guardStart);
        var guard = sql[guardStart..(guardEnd + "FROM %I.%I'".Length)];
        var compactGuard = string.Concat(guard.Where(character => !char.IsWhiteSpace(character)));
        Assert.Contains(
            "count(*)=1ANDbool_and(\"MigrationId\"=%LAND\"ProductVersion\"=%L)",
            compactGuard,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Regex.IsMatch(
            guard,
            @"\bOR\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    [Fact]
    public void EmbeddedPhysicalCatalogHistoryLinesMatchTheReviewedHead()
    {
        Assert.Empty(HistoryLines(PostgreSqlCatalogState.EmptySchema));
        Assert.Empty(HistoryLines(PostgreSqlCatalogState.EmptyHistory));
        Assert.Equal(
            [$"history-row|{BaselineMigrationId}|{ProductVersion}"],
            HistoryLines(PostgreSqlCatalogState.Baseline));
        Assert.Equal(
        [
            $"history-row|{BaselineMigrationId}|{ProductVersion}",
            $"history-row|{OperationalMigrationId}|{ProductVersion}"
        ],
            HistoryLines(PostgreSqlCatalogState.Head));
    }

    [Fact]
    public void PhysicalCatalogExposesTheTransactionBoundValidationOverload()
    {
        AssertMethod(
            typeof(PostgreSqlPhysicalCatalogContract),
            "ValidateAsync",
            typeof(Task),
            typeof(NpgsqlConnection),
            typeof(NpgsqlTransaction),
            typeof(PostgreSqlCatalogState),
            typeof(int),
            typeof(CancellationToken));
    }

    [Fact]
    public void PhysicalCatalogTransactionPathBindsInitialScopeAndEveryCatalogCommand()
    {
        var catalog = ParseClass(
            "backend/Database/PostgreSqlPhysicalCatalogContract.cs",
            "PostgreSqlPhysicalCatalogContract");
        var transactionValidate = FindMethod(
            catalog,
            "ValidateAsync",
            "NpgsqlConnection",
            "NpgsqlTransaction",
            "PostgreSqlCatalogState",
            "int",
            "CancellationToken");
        var captureCall = Assert.Single(
            transactionValidate.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "CaptureCanonicalAsync");

        AssertArgumentsContain(
            captureCall,
            "connection",
            "transaction",
            "commandTimeoutSeconds",
            "cancellationToken");

        var transactionCapture = FindMethod(
            catalog,
            "CaptureCanonicalAsync",
            "NpgsqlConnection",
            "NpgsqlTransaction",
            "int",
            "CancellationToken");
        var currentSchemaCall = Assert.Single(
            transactionCapture.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ScalarAsync"
                          && invocation.ToFullString().Contains(
                              "current_schema()",
                              StringComparison.Ordinal));
        AssertArgumentsContain(
            currentSchemaCall,
            "connection",
            "transaction",
            "commandTimeoutSeconds",
            "cancellationToken");

        var commandCreators = catalog.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(method => Invocations(method, "CreateCommand").Any())
            .ToArray();
        Assert.NotEmpty(commandCreators);
        Assert.All(commandCreators, method =>
        {
            Assert.Contains(
                method.ParameterList.Parameters,
                parameter => BaseTypeName(parameter.Type) == "NpgsqlTransaction");
            Assert.Contains(
                method.ParameterList.Parameters,
                parameter => parameter.Identifier.ValueText == "commandTimeoutSeconds"
                             && BaseTypeName(parameter.Type) == "int");
            AssertEveryExecutedCommandIsBound(method);
        });

        var catalogQueries = Invocations(catalog, "AddAsync").ToArray();
        Assert.True(catalogQueries.Length > 10);
        Assert.All(
            catalogQueries,
            invocation => AssertArgumentsContain(
                invocation,
                "transaction",
                "commandTimeoutSeconds"));
    }

    [Fact]
    public void PhysicalCatalogPreservesPrimaryFailuresAcrossReaderAndCommandCleanup()
    {
        var catalog = ParseClass(
            "backend/Database/PostgreSqlPhysicalCatalogContract.cs",
            "PostgreSqlPhysicalCatalogContract");

        foreach (var method in new[]
                 {
                     FindMethod(catalog, "AddAsync", parameterCount: 8),
                     FindMethod(catalog, "ScalarAsync", parameterCount: 5)
                 })
        {
            Assert.Contains(
                method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                invocation => InvocationName(invocation)
                              == "DisposeReaderThenCommandAsync");
            Assert.DoesNotContain(
                method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>(),
                declaration => declaration.AwaitKeyword.RawKind != 0
                               && declaration.UsingKeyword.RawKind != 0);
        }
    }

    private static PostgreSqlMigrationHistoryEntry[] ExactHead() =>
    [
        new(BaselineMigrationId, ProductVersion),
        new(OperationalMigrationId, ProductVersion)
    ];

    private static string[] HistoryLines(PostgreSqlCatalogState state)
    {
        return PostgreSqlPhysicalCatalogContract.ReadExpectedInventory(state)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("history-row|", StringComparison.Ordinal))
            .ToArray();
    }

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

    private static ClassDeclarationSyntax ParseClass(
        string repositoryRelativePath,
        string className)
    {
        var path = SqliteContractTestSupport.AbsolutePath(repositoryRelativePath);
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);
        Assert.DoesNotContain(tree.GetDiagnostics(), diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        return tree.GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == className);
    }

    private static MethodDeclarationSyntax FindMethod(
        ClassDeclarationSyntax type,
        string name,
        int parameterCount)
    {
        return type.Members.OfType<MethodDeclarationSyntax>().Single(method =>
            method.Identifier.ValueText == name
            && method.ParameterList.Parameters.Count == parameterCount);
    }

    private static MethodDeclarationSyntax FindMethod(
        ClassDeclarationSyntax type,
        string name,
        params string[] parameterTypes)
    {
        return type.Members.OfType<MethodDeclarationSyntax>().Single(method =>
            method.Identifier.ValueText == name
            && method.ParameterList.Parameters
                .Select(parameter => BaseTypeName(parameter.Type))
                .SequenceEqual(parameterTypes, StringComparer.Ordinal));
    }

    private static string BaseTypeName(TypeSyntax? type)
    {
        return type switch
        {
            NullableTypeSyntax nullable => BaseTypeName(nullable.ElementType),
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
            _ => type?.ToString().TrimEnd('?') ?? string.Empty
        };
    }

    private static IEnumerable<string> StringExpressions(SyntaxNode node)
    {
        return node.DescendantNodes()
            .Where(candidate => candidate is LiteralExpressionSyntax
                                or InterpolatedStringExpressionSyntax)
            .Select(candidate => candidate.ToString().Trim('"'));
    }

    private static IEnumerable<InvocationExpressionSyntax> Invocations(
        SyntaxNode node,
        string name)
    {
        return node.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) == name);
    }

    private static string InvocationName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => string.Empty
        };
    }

    private static AssignmentExpressionSyntax FindAssignment(
        SyntaxNode node,
        string receiver,
        string propertyName,
        string expectedRight)
    {
        return node.DescendantNodes().OfType<AssignmentExpressionSyntax>().Single(assignment =>
            assignment.Left is MemberAccessExpressionSyntax member
            && member.Expression.ToString() == receiver
            && member.Name.Identifier.ValueText == propertyName
            && assignment.Right.ToString() == expectedRight);
    }

    private static void AssertEveryExecutedCommandIsBound(MethodDeclarationSyntax method)
    {
        var executions = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation).StartsWith("Execute", StringComparison.Ordinal)
                                 && invocation.Expression is MemberAccessExpressionSyntax)
            .ToArray();
        Assert.NotEmpty(executions);

        Assert.All(executions, execution =>
        {
            var receiver = ((MemberAccessExpressionSyntax)execution.Expression)
                .Expression.ToString();
            var transactionAssignment = FindAssignment(
                method,
                receiver,
                "Transaction",
                "transaction");
            var timeoutAssignment = FindAssignment(
                method,
                receiver,
                "CommandTimeout",
                "commandTimeoutSeconds");
            Assert.True(transactionAssignment.SpanStart < execution.SpanStart);
            Assert.True(timeoutAssignment.SpanStart < execution.SpanStart);
        });
    }

    private static int ResolveReaderOrdinal(
        SyntaxNode readerLoop,
        ExpressionSyntax expression)
    {
        var read = Assert.Single(Invocations(readerLoop, "ReadAsync"));
        var readerName = Assert.IsType<MemberAccessExpressionSyntax>(read.Expression)
            .Expression.ToString();
        var resolved = expression;
        if (expression is IdentifierNameSyntax identifier)
        {
            resolved = readerLoop.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .Single(variable => variable.Identifier.ValueText
                                    == identifier.Identifier.ValueText)
                .Initializer!.Value;
        }

        var invocation = Assert.IsType<InvocationExpressionSyntax>(resolved);
        var member = Assert.IsType<MemberAccessExpressionSyntax>(invocation.Expression);
        Assert.Equal(readerName, member.Expression.ToString());
        Assert.Contains(
            member.Name.Identifier.ValueText,
            new[] { "GetString", "GetFieldValue" });
        return int.Parse(
            Assert.Single(invocation.ArgumentList.Arguments).Expression.ToString(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertAsyncValidatorUsesCapture(
        ClassDeclarationSyntax contract,
        string asyncMethodName,
        string pureMethodName)
    {
        var method = FindMethod(contract, asyncMethodName, parameterCount: 4);
        var capture = Assert.Single(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "CaptureAsync");
        AssertArgumentsContain(
            capture,
            "connection",
            "transaction",
            "commandTimeoutSeconds",
            "cancellationToken");
        Assert.Contains(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == pureMethodName);
        Assert.DoesNotContain(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) is "ExecuteReaderAsync" or "ExecuteScalarAsync");
    }

    private static void AssertArgumentsContain(
        InvocationExpressionSyntax invocation,
        params string[] expressions)
    {
        var actual = invocation.ArgumentList.Arguments
            .Select(argument => argument.Expression.ToString())
            .ToArray();
        Assert.All(expressions, expression => Assert.Contains(expression, actual));
    }

    private static void AssertUsesProvablyPositiveTimeout(
        InvocationExpressionSyntax invocation)
    {
        var arguments = invocation.ArgumentList.Arguments;
        var timeoutArgument = arguments.SingleOrDefault(argument =>
                                  argument.NameColon?.Name.Identifier.ValueText
                                  == "commandTimeoutSeconds")
                              ?? arguments[2];
        AssertProvablyPositiveTimeout(timeoutArgument.Expression);
    }

    private static void AssertProvablyPositiveTimeout(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal
            && int.TryParse(literal.Token.ValueText, out var value))
        {
            Assert.True(value > 0);
            return;
        }

        var fieldName = expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            _ => string.Empty
        };
        var field = typeof(PostgreSqlNativeMigrator).GetField(
            fieldName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Equal(typeof(int), field.FieldType);
        Assert.True((int)field.GetValue(null)! > 0);
    }
}
