using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Npgsql;
using NpgsqlTypes;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

public sealed class TransferV3ImportStateStorePostgreSqlContractTests
{
    private const string DigestA =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void TransactionBoundApiHasTheExactReviewedSignatures()
    {
        AssertMethod(
            "TryTransitionInPostgreSqlTransactionAsync",
            typeof(Task<int>),
            typeof(NpgsqlConnection),
            typeof(NpgsqlTransaction),
            typeof(TransferV3ImportState),
            typeof(TransferV3ImportState),
            typeof(int),
            typeof(CancellationToken));
        AssertMethod(
            "TryTransitionCanonicalFreshToImportingInPostgreSqlTransactionAsync",
            typeof(Task<int>),
            typeof(NpgsqlConnection),
            typeof(NpgsqlTransaction),
            typeof(byte[]),
            typeof(byte[]),
            typeof(int),
            typeof(CancellationToken));
        AssertMethod(
            "ReadForShareInPostgreSqlTransactionAsync",
            typeof(Task<TransferV3ImportState>),
            typeof(NpgsqlConnection),
            typeof(NpgsqlTransaction),
            typeof(int),
            typeof(CancellationToken));

        Assert.Equal(
            "The transfer-v3 PostgreSQL import-state operation requires an open connection and an active transaction owned by that connection.",
            Constant("PostgreSqlTransactionFailureMessage"));
        Assert.Equal(
            "The transfer-v3 PostgreSQL import-state row is not exactly one canonical text value.",
            Constant("PostgreSqlReadFailureMessage"));
    }

    [Fact]
    public async Task ArgumentValidationPrecedesEveryProviderAccess()
    {
        var expected = TransferV3ImportState.Fresh();
        var next = TransferV3ImportState.Importing(DigestA);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            TransferV3ImportStateStore.TryTransitionInPostgreSqlTransactionAsync(
                null!,
                null!,
                expected,
                next,
                0,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            TransferV3ImportStateStore.TryTransitionInPostgreSqlTransactionAsync(
                null!,
                null!,
                expected,
                next,
                1,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            TransferV3ImportStateStore.ReadForShareInPostgreSqlTransactionAsync(
                null!,
                null!,
                0,
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            TransferV3ImportStateStore.ReadForShareInPostgreSqlTransactionAsync(
                null!,
                null!,
                1,
                CancellationToken.None));
    }

    [Fact]
    public void CasControlFlowReusesTheLegalGraphAndShortCircuitsBeforeProviderWork()
    {
        var store = ParseStore();
        var method = FindMethod(
            store,
            "TryTransitionInPostgreSqlTransactionAsync",
            parameterCount: 6);
        var timeout = Assert.Single(Invocations(method, "ThrowIfLessThan"));
        var nullChecks = Invocations(method, "ThrowIfNull").OrderBy(call => call.SpanStart).ToArray();
        Assert.Equal(4, nullChecks.Length);
        AssertInvocationArguments(timeout, "commandTimeoutSeconds", "1");
        Assert.Equal(
            new[] { "connection", "transaction", "expected", "next" },
            nullChecks.Select(OnlyArgument).ToArray());
        var legalGuard = Assert.Single(
            method.Body!.Statements.OfType<IfStatementSyntax>(),
            statement => statement.Condition is PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LogicalNotExpression,
                Operand: InvocationExpressionSyntax invocation
            }
            && InvocationName(invocation) == "IsLegalTransition");
        var illegalReturn = Assert.IsType<ReturnStatementSyntax>(legalGuard.Statement);
        Assert.Equal("0", illegalReturn.Expression?.ToString());
        var transactionValidation = Assert.Single(
            Invocations(method, "ValidatePostgreSqlTransactionContext"));
        var cancellation = Assert.Single(Invocations(method, "ThrowIfCancellationRequested"));
        var serialization = Invocations(method, "Serialize").OrderBy(call => call.SpanStart).ToArray();
        Assert.Equal(2, serialization.Length);
        var execute = Assert.Single(Invocations(method, "ExecutePostgreSqlCasCommandAsync"));
        AssertInvocationArguments(transactionValidation, "connection", "transaction");
        AssertInvocationArguments(cancellation);
        Assert.Equal("cancellationToken.ThrowIfCancellationRequested", cancellation.Expression.ToString());

        Assert.True(timeout.SpanStart < nullChecks[0].SpanStart);
        Assert.True(nullChecks[^1].SpanStart < legalGuard.SpanStart);
        Assert.True(legalGuard.SpanStart < transactionValidation.SpanStart);
        Assert.True(transactionValidation.SpanStart < cancellation.SpanStart);
        Assert.True(cancellation.SpanStart < serialization[0].SpanStart);
        Assert.True(serialization[^1].SpanStart < execute.SpanStart);
        Assert.DoesNotContain("ExecutePostgreSqlCasAsync", method.ToFullString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RawCanonicalFreshToImportingCasValidatesThenDelegatesBorrowedBuffers()
    {
        var method = FindMethod(
            ParseStore(),
            "TryTransitionCanonicalFreshToImportingInPostgreSqlTransactionAsync",
            parameterCount: 6);
        var timeout = Assert.Single(Invocations(method, "ThrowIfLessThan"));
        var nullChecks = Invocations(method, "ThrowIfNull")
            .OrderBy(call => call.SpanStart)
            .ToArray();
        Assert.Equal(4, nullChecks.Length);
        AssertInvocationArguments(timeout, "commandTimeoutSeconds", "1");
        Assert.Equal(
            new[]
            {
                "connection",
                "transaction",
                "expectedCanonicalUtf8",
                "nextCanonicalUtf8",
            },
            nullChecks.Select(OnlyArgument).ToArray());

        var canonical = Assert.Single(
            Invocations(method, "IsCanonicalFreshToImportingTransition"));
        Assert.Equal(
            "TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition",
            canonical.Expression.ToString());
        AssertInvocationArguments(
            canonical,
            "expectedCanonicalUtf8",
            "nextCanonicalUtf8");
        var canonicalNot = Assert.IsType<PrefixUnaryExpressionSyntax>(canonical.Parent);
        Assert.True(canonicalNot.IsKind(SyntaxKind.LogicalNotExpression));
        var canonicalGuard = Assert.IsType<IfStatementSyntax>(canonicalNot.Parent);
        var illegalReturn = Assert.IsType<ReturnStatementSyntax>(canonicalGuard.Statement);
        Assert.Equal("0", illegalReturn.Expression?.ToString());

        var transactionValidation = Assert.Single(
            Invocations(method, "ValidatePostgreSqlTransactionContext"));
        var cancellation = Assert.Single(Invocations(method, "ThrowIfCancellationRequested"));
        var execute = Assert.Single(Invocations(method, "ExecutePostgreSqlCasCommandAsync"));
        AssertInvocationArguments(transactionValidation, "connection", "transaction");
        AssertInvocationArguments(cancellation);
        Assert.Equal(
            "cancellationToken.ThrowIfCancellationRequested",
            cancellation.Expression.ToString());
        AssertInvocationArguments(
            execute,
            "connection",
            "transaction",
            "commandTimeoutSeconds",
            "expectedCanonicalUtf8",
            "nextCanonicalUtf8",
            "cancellationToken");

        Assert.True(timeout.SpanStart < nullChecks[0].SpanStart);
        Assert.True(nullChecks[^1].SpanStart < canonical.SpanStart);
        Assert.True(canonicalGuard.Span.End < transactionValidation.SpanStart);
        Assert.True(transactionValidation.SpanStart < cancellation.SpanStart);
        Assert.True(cancellation.SpanStart < execute.SpanStart);

        Assert.Empty(method.DescendantNodes().OfType<ArrayCreationExpressionSyntax>());
        Assert.Empty(method.DescendantNodes().OfType<ImplicitArrayCreationExpressionSyntax>());
        Assert.Empty(method.DescendantNodes().OfType<StackAllocArrayCreationExpressionSyntax>());
        var source = method.ToFullString();
        Assert.DoesNotContain("Serialize", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ZeroMemory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Open", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginTransaction", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Commit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Rollback", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispose", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BorrowedTransactionValidationProvesOwnershipBeforeProviderReadiness()
    {
        var validation = FindMethod(
            ParseStore(),
            "ValidatePostgreSqlTransactionContext",
            parameterCount: 2);
        AssertMethodSyntax(
            validation,
            """
            private static void ValidatePostgreSqlTransactionContext(
                NpgsqlConnection connection,
                NpgsqlTransaction transaction)
            {
                if (connection.State != ConnectionState.Open)
                    throw PostgreSqlTransactionFailure();

                NpgsqlConnection? transactionConnection;
                try
                {
                    transactionConnection = transaction.Connection;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or ObjectDisposedException)
                {
                    throw PostgreSqlTransactionFailure();
                }

                if (!ReferenceEquals(transactionConnection, connection))
                    throw PostgreSqlTransactionFailure();

                try
                {
                    // Npgsql 10.0.3 keeps Connection after completion; this getter runs CheckReady.
                    _ = transaction.IsolationLevel;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or ObjectDisposedException)
                {
                    throw PostgreSqlTransactionFailure();
                }
            }
            """);
        var openState = Assert.Single(
            validation.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            member => member.Expression.ToString() == "connection"
                      && member.Name.Identifier.ValueText == "State");
        var transactionConnection = Assert.Single(
            validation.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            member => member.Expression.ToString() == "transaction"
                      && member.Name.Identifier.ValueText == "Connection");
        var ownershipGuard = Assert.Single(
            validation.Body!.Statements.OfType<IfStatementSyntax>(),
            statement => statement.Condition is PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.LogicalNotExpression,
                Operand: InvocationExpressionSyntax invocation
            }
            && InvocationName(invocation) == "ReferenceEquals");
        var logicalNot = Assert.IsType<PrefixUnaryExpressionSyntax>(ownershipGuard.Condition);
        var ownership = Assert.IsType<InvocationExpressionSyntax>(logicalNot.Operand);
        Assert.Collection(
            ownership.ArgumentList.Arguments,
            argument => Assert.Equal("transactionConnection", argument.Expression.ToString()),
            argument => Assert.Equal("connection", argument.Expression.ToString()));
        var ownershipFailure = Assert.IsType<ThrowStatementSyntax>(ownershipGuard.Statement);
        Assert.Equal(
            "PostgreSqlTransactionFailure",
            InvocationName(Assert.IsType<InvocationExpressionSyntax>(ownershipFailure.Expression)));
        var readiness = Assert.Single(
            validation.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            member => member.Expression.ToString() == "transaction"
                      && member.Name.Identifier.ValueText == "IsolationLevel");

        Assert.True(openState.SpanStart < transactionConnection.SpanStart);
        Assert.True(transactionConnection.SpanStart < ownershipGuard.SpanStart);
        Assert.True(ownershipGuard.SpanStart < readiness.SpanStart);
    }

    [Fact]
    public void LockingReadValidatesContextAndCancellationBeforeCommandCreation()
    {
        var read = FindMethod(
            ParseStore(),
            "ReadForShareInPostgreSqlTransactionAsync",
            parameterCount: 4);
        var timeout = Assert.Single(Invocations(read, "ThrowIfLessThan"));
        var nullChecks = Invocations(read, "ThrowIfNull")
            .OrderBy(call => call.SpanStart)
            .ToArray();
        var transactionValidation = Assert.Single(
            Invocations(read, "ValidatePostgreSqlTransactionContext"));
        var cancellation = Assert.Single(Invocations(read, "ThrowIfCancellationRequested"));
        var createCommand = Assert.Single(Invocations(read, "CreateCommand"));

        Assert.Equal(2, nullChecks.Length);
        AssertInvocationArguments(timeout, "commandTimeoutSeconds", "1");
        Assert.Equal(
            new[] { "connection", "transaction" },
            nullChecks.Select(OnlyArgument).ToArray());
        AssertInvocationArguments(transactionValidation, "connection", "transaction");
        AssertInvocationArguments(cancellation);
        Assert.Equal("cancellationToken.ThrowIfCancellationRequested", cancellation.Expression.ToString());
        AssertInvocationArguments(createCommand);
        Assert.Equal("connection.CreateCommand", createCommand.Expression.ToString());
        Assert.True(timeout.SpanStart < nullChecks[0].SpanStart);
        Assert.True(nullChecks[^1].SpanStart < transactionValidation.SpanStart);
        Assert.True(transactionValidation.SpanStart < cancellation.SpanStart);
        Assert.True(cancellation.SpanStart < createCommand.SpanStart);
    }

    [Fact]
    public void CasSqlUsesNativeTextAndCaseBoundedExactBytePredicates()
    {
        var sql = TransferV3ImportStateStore.PostgreSqlCasSql;

        Assert.Equal(
            """
            UPDATE "ConfigItems"
            SET "ConfigValue" = convert_from(@next, 'UTF8')
            WHERE pg_typeof("ConfigName") = 'text'::regtype
              AND "ConfigName" = @keyText
              AND CASE
                    WHEN octet_length("ConfigName") = @keyLength
                    THEN convert_to("ConfigName", 'UTF8') = @key
                    ELSE false
                  END
              AND pg_typeof("ConfigValue") = 'text'::regtype
              AND CASE
                    WHEN octet_length("ConfigValue") = @expectedLength
                    THEN convert_to("ConfigValue", 'UTF8') = @expected
                    ELSE false
                  END
            """,
            sql);
        Assert.DoesNotContain(DigestA, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void LockingReadSqlIsBoundedExactAndTakesTwoRowShareLocks()
    {
        var sql = Constant("PostgreSqlReadForShareSql");

        Assert.Equal(
            """
            SELECT
              pg_typeof(config."ConfigValue") = 'text'::regtype,
              CASE
                WHEN pg_typeof(config."ConfigValue") = 'text'::regtype THEN
                  CASE
                    WHEN octet_length(config."ConfigValue"::text) <= @maxCanonicalUtf8Bytes THEN
                      CASE
                        WHEN octet_length(convert_to(config."ConfigValue"::text, 'UTF8')) <= @maxCanonicalUtf8Bytes
                        THEN octet_length(convert_to(config."ConfigValue"::text, 'UTF8'))
                        ELSE @maxCanonicalUtf8Bytes + 1
                      END
                    ELSE @maxCanonicalUtf8Bytes + 1
                  END
                ELSE @maxCanonicalUtf8Bytes + 1
              END,
              CASE
                WHEN pg_typeof(config."ConfigValue") = 'text'::regtype THEN
                  CASE
                    WHEN octet_length(config."ConfigValue"::text) <= @maxCanonicalUtf8Bytes THEN
                      CASE
                        WHEN octet_length(convert_to(config."ConfigValue"::text, 'UTF8')) <= @maxCanonicalUtf8Bytes
                        THEN convert_to(config."ConfigValue"::text, 'UTF8')
                        ELSE ''::bytea
                      END
                    ELSE ''::bytea
                  END
                ELSE ''::bytea
              END
            FROM "ConfigItems" AS config
            WHERE CASE
              WHEN pg_typeof(config."ConfigName") = 'text'::regtype THEN
                CASE
                  WHEN config."ConfigName"::text = @keyText THEN
                    CASE
                      WHEN octet_length(config."ConfigName"::text) = @keyLength
                      THEN convert_to(config."ConfigName"::text, 'UTF8') = @key
                      ELSE false
                    END
                  ELSE false
                END
              ELSE false
            END
            LIMIT 2
            FOR SHARE OF config
            """,
            sql);
        Assert.DoesNotContain(DigestA, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandsBindExactTypesTransactionTimeoutAndPrimaryPreservingCleanup()
    {
        var store = ParseStore();
        var cas = FindMethod(store, "ExecutePostgreSqlCasCommandAsync", parameterCount: 6);
        var read = FindMethod(
            store,
            "ReadForShareInPostgreSqlTransactionAsync",
            parameterCount: 4);
        AssertCommandBinding(cas, "PostgreSqlCasSql", cleanupReader: "null");
        AssertCommandBinding(read, "PostgreSqlReadForShareSql", cleanupReader: "reader");

        AssertNpgsqlParameters(
            cas,
            ("@keyText", "NpgsqlDbType.Text", "TransferV3ReservedConfigPolicy.ImportStateKey"),
            ("@key", "NpgsqlDbType.Bytea", "ImportStateKeyUtf8"),
            ("@keyLength", "NpgsqlDbType.Integer", "ImportStateKeyUtf8.Length"),
            ("@expected", "NpgsqlDbType.Bytea", "expectedUtf8"),
            ("@expectedLength", "NpgsqlDbType.Integer", "expectedUtf8.Length"),
            ("@next", "NpgsqlDbType.Bytea", "nextUtf8"));
        AssertNpgsqlParameters(
            read,
            ("@keyText", "NpgsqlDbType.Text", "TransferV3ReservedConfigPolicy.ImportStateKey"),
            ("@key", "NpgsqlDbType.Bytea", "ImportStateKeyUtf8"),
            ("@keyLength", "NpgsqlDbType.Integer", "ImportStateKeyUtf8.Length"),
            (
                "@maxCanonicalUtf8Bytes",
                "NpgsqlDbType.Integer",
                "TransferV3ImportStateCodec.MaxCanonicalUtf8Bytes"));

        var readSource = read.ToFullString();
        Assert.DoesNotContain("CommandBehavior.SingleRow", readSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandBehavior.SingleResult", readSource, StringComparison.Ordinal);
        Assert.DoesNotContain("await using", cas.ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("await using", read.ToFullString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LockingReadChecksTheSecondRowBeforeCanonicalDecodeAndZerosBytes()
    {
        var read = FindMethod(
            ParseStore(),
            "ReadForShareInPostgreSqlTransactionAsync",
            parameterCount: 4);
        var rowReads = Invocations(read, "ReadAsync").OrderBy(call => call.SpanStart).ToArray();
        Assert.Equal(2, rowReads.Length);
        var parse = Assert.Single(Invocations(read, "ParseCanonical"));
        AssertInvocationArguments(parse, "canonicalUtf8");
        var zeroingTry = AssertFinallyZeros(read, "canonicalUtf8");
        var cleanup = Assert.Single(
            Invocations(zeroingTry.Block, "DisposeReaderThenCommandAsync"));
        var exactFieldCount = Assert.Single(
            read.DescendantNodes().OfType<BinaryExpressionSyntax>(),
            expression => expression.IsKind(SyntaxKind.EqualsExpression)
                          && expression.Left.ToString() == "reader.FieldCount"
                          && expression.Right.ToString() == "3");
        var source = read.ToFullString();

        Assert.True(rowReads[1].SpanStart < parse.SpanStart);
        Assert.True(parse.SpanStart < cleanup.SpanStart);
        Assert.True(cleanup.SpanStart < zeroingTry.Finally!.SpanStart);
        Assert.True(exactFieldCount.SpanStart < rowReads[1].SpanStart);
        Assert.Contains("canonicalLength < 1", source, StringComparison.Ordinal);
        Assert.Contains(
            "canonicalLength > TransferV3ImportStateCodec.MaxCanonicalUtf8Bytes",
            source,
            StringComparison.Ordinal);
        Assert.Contains("canonicalUtf8.Length != canonicalLength", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CasSerializedBuffersAreZeroedAfterPrimaryPreservingCommandCleanup()
    {
        var store = ParseStore();
        var transactionCas = FindMethod(
            store,
            "TryTransitionInPostgreSqlTransactionAsync",
            parameterCount: 6);
        var transactionZeroingTry = AssertFinallyZeros(
            transactionCas,
            "expectedUtf8",
            "nextUtf8");
        var transactionExecute = Assert.Single(
            Invocations(transactionZeroingTry.Block, "ExecutePostgreSqlCasCommandAsync"));
        AssertAwaitedWithin(transactionExecute, transactionZeroingTry.Block);
        Assert.True(transactionExecute.SpanStart < transactionZeroingTry.Finally!.SpanStart);

        var contextOwnedCas = FindMethod(store, "TryTransitionAsync", parameterCount: 3);
        var contextZeroingTry = AssertFinallyZeros(
            contextOwnedCas,
            "expectedUtf8",
            "nextUtf8");
        var contextExecutions = contextZeroingTry.Block.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => InvocationName(invocation) is
                "ExecuteSqliteCasAsync" or "ExecutePostgreSqlCasAsync")
            .OrderBy(call => call.SpanStart)
            .ToArray();
        Assert.Equal(2, contextExecutions.Length);
        Assert.All(
            contextExecutions,
            invocation => AssertAwaitedWithin(invocation, contextZeroingTry.Block));
        Assert.True(contextExecutions[^1].SpanStart < contextZeroingTry.Finally!.SpanStart);

        var command = FindMethod(store, "ExecutePostgreSqlCasCommandAsync", parameterCount: 6);
        var providerExecute = Assert.Single(Invocations(command, "ExecuteNonQueryAsync"));
        var cleanup = Assert.Single(Invocations(command, "DisposeReaderThenCommandAsync"));
        AssertAwaitedWithin(providerExecute, command.Body!);
        AssertAwaitedWithin(cleanup, command.Body!);
        Assert.True(providerExecute.SpanStart < cleanup.SpanStart);
    }

    [Fact]
    public void TransactionBoundPathsNeverOwnCallerConnectionOrTransactionLifecycle()
    {
        var store = ParseStore();
        var source = string.Join(
            '\n',
            FindMethod(store, "TryTransitionInPostgreSqlTransactionAsync", 6).ToFullString(),
            FindMethod(store, "ExecutePostgreSqlCasCommandAsync", 6).ToFullString(),
            FindMethod(store, "ReadForShareInPostgreSqlTransactionAsync", 4).ToFullString(),
            FindMethod(store, "ValidatePostgreSqlTransactionContext", 2).ToFullString());

        Assert.DoesNotContain("new NpgsqlConnection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginTransaction", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Commit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Rollback", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(
                "(?:connection|transaction)\\s*\\.\\s*(?:Close|Dispose|DisposeAsync)\\s*\\(",
                RegexOptions.CultureInvariant),
            source);
        Assert.DoesNotContain("Environment.", source, StringComparison.Ordinal);
    }

    private static void AssertCommandBinding(
        MethodDeclarationSyntax method,
        string expectedSqlOwner,
        string cleanupReader)
    {
        AssertSingleAssignment(method, "command.Transaction", "transaction");
        AssertSingleAssignment(method, "command.CommandTimeout", "commandTimeoutSeconds");
        AssertSingleAssignment(method, "command.CommandText", expectedSqlOwner);
        var createCommand = Assert.Single(Invocations(method, "CreateCommand"));
        AssertInvocationArguments(createCommand);
        Assert.Equal("connection.CreateCommand", createCommand.Expression.ToString());
        var cleanup = Assert.Single(Invocations(method, "DisposeReaderThenCommandAsync"));
        AssertInvocationArguments(cleanup, cleanupReader, "command", "primaryFailure");
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
            Assert.Equal("AddNpgsqlParameter", calls[index].Expression.ToString());
            AssertInvocationArguments(
                calls[index],
                "command",
                $"\"{expected[index].Name}\"",
                expected[index].Type,
                expected[index].Value);
        }

        Assert.DoesNotContain(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString().Contains(
                ".Parameters.",
                StringComparison.Ordinal));
    }

    private static TryStatementSyntax AssertFinallyZeros(
        MethodDeclarationSyntax method,
        params string[] expectedBuffers)
    {
        var zeroingTry = Assert.Single(
            method.DescendantNodes().OfType<TryStatementSyntax>(),
            statement => statement.Finally is not null
                         && Invocations(statement.Finally, "ZeroMemory").Any());
        var zeroCalls = Invocations(zeroingTry.Finally!, "ZeroMemory")
            .OrderBy(call => call.SpanStart)
            .ToArray();
        Assert.Equal(expectedBuffers.Length, zeroCalls.Length);
        Assert.All(
            zeroCalls,
            call => Assert.Equal("CryptographicOperations.ZeroMemory", call.Expression.ToString()));
        Assert.Equal(expectedBuffers, zeroCalls.Select(OnlyArgument).ToArray());
        for (var index = 0; index < zeroCalls.Length; index++)
        {
            var guard = Assert.Single(
                zeroCalls[index].Ancestors().OfType<IfStatementSyntax>(),
                statement => zeroingTry.Finally!.Span.Contains(statement.Span));
            Assert.Equal($"{expectedBuffers[index]} is not null", guard.Condition.ToString());
            Assert.Null(guard.Else);
            Assert.Equal(
                zeroCalls[index].SpanStart,
                Assert.Single(Invocations(guard.Statement, "ZeroMemory")).SpanStart);
        }

        return zeroingTry;
    }

    private static void AssertAwaitedWithin(
        InvocationExpressionSyntax invocation,
        SyntaxNode owner)
    {
        var awaited = Assert.Single(
            invocation.Ancestors().OfType<AwaitExpressionSyntax>(),
            expression => owner.Span.Contains(expression.Span));
        Assert.True(owner.Span.Contains(awaited.Span));
    }

    private static void AssertMethodSyntax(
        MethodDeclarationSyntax actual,
        string expectedSource)
    {
        var expected = Assert.IsType<MethodDeclarationSyntax>(
            SyntaxFactory.ParseMemberDeclaration(expectedSource));
        Assert.Equal(
            expected.NormalizeWhitespace().ToFullString(),
            actual.NormalizeWhitespace().ToFullString());
    }

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

    private static void AssertMethod(
        string name,
        Type returnType,
        params Type[] parameterTypes)
    {
        var method = typeof(TransferV3ImportStateStore).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            parameterTypes,
            modifiers: null);
        Assert.NotNull(method);
        Assert.Equal(returnType, method.ReturnType);
    }

    private static string Constant(string name)
    {
        var field = typeof(TransferV3ImportStateStore).GetField(
            name,
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.True(field.IsLiteral);
        return Assert.IsType<string>(field.GetRawConstantValue());
    }

    private static ClassDeclarationSyntax ParseStore()
    {
        var path = SqliteContractTestSupport.AbsolutePath(
            "backend/Database/Transfer/TransferV3ImportStateStore.cs");
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
        return root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == nameof(TransferV3ImportStateStore));
    }

    private static MethodDeclarationSyntax FindMethod(
        ClassDeclarationSyntax type,
        string name,
        int parameterCount) =>
        type.Members.OfType<MethodDeclarationSyntax>().Single(method =>
            method.Identifier.ValueText == name
            && method.ParameterList.Parameters.Count == parameterCount);

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

}
