using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

public sealed class TransferV3PostgreSqlAdmissionTests
{
    private const string AdmissionSourcePath =
        "backend/Database/Transfer/Phase4/TransferV3PostgreSqlAdmissionValidator.cs";
    private const string SessionSourcePath =
        "backend/Database/Transfer/Phase4/TransferV3PostgreSqlSession.cs";
    private const string ServerContractSourcePath =
        "backend/Database/Transfer/Phase4/TransferV3PostgreSqlServerContract.cs";
    private const string EnvironmentContractSourcePath =
        "backend/Database/PostgreSqlEnvironmentContract.cs";
    private const string ImportStateStoreSourcePath =
        "backend/Database/Transfer/TransferV3ImportStateStore.cs";
    private const string FreshCanonicalJson =
        "{\"formatVersion\":3,\"state\":\"fresh\"}";
    private const string ImportingCanonicalPrefix =
        "{\"formatVersion\":3,\"state\":\"importing\",\"manifestSha256\":\"";
    private const string CanonicalDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void ValidatorHasTheExactReviewedStaticSignature()
    {
        var type = typeof(TransferV3PostgreSqlAdmissionValidator);

        Assert.True(type.IsAbstract);
        Assert.True(type.IsSealed);
        Assert.False(type.IsPublic);

        var method = type.GetMethod(
            "ValidateFreshAndMarkImportingAsync",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(TransferV3PostgreSqlSession),
                typeof(NpgsqlTransaction),
                typeof(TransferV3Phase4Digest),
                typeof(TransferV3Phase4ManagedBudget),
                typeof(int),
                typeof(CancellationToken),
            ],
            modifiers: null);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
        Assert.Single(
            type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            candidate => candidate.Name == "ValidateFreshAndMarkImportingAsync");
    }

    [Fact]
    public void ArgumentsAndDigestOwnershipAreProvedBeforeBorrowingOrProviderWork()
    {
        var method = EntryPoint();
        Assert.Equal(
            [
                ("TransferV3PostgreSqlSession", "session"),
                ("NpgsqlTransaction", "transaction"),
                ("TransferV3Phase4Digest", "manifestDigest"),
                ("TransferV3Phase4ManagedBudget", "managedBudget"),
                ("int", "commandTimeoutSeconds"),
                ("CancellationToken", "cancellationToken"),
            ],
            method.ParameterList.Parameters
                .Select(parameter => (
                    parameter.Type!.ToString(),
                    parameter.Identifier.ValueText)));
        Assert.Equal(
            ["internal", "static", "async"],
            method.Modifiers.Select(modifier => modifier.ValueText));
        Assert.Equal("Task", method.ReturnType.ToString());

        var timeout = Assert.Single(Invocations(method, "ThrowIfLessThan"));
        AssertInvocationArguments(timeout, "commandTimeoutSeconds", "1");
        var nullChecks = Invocations(method, "ThrowIfNull")
            .OrderBy(invocation => invocation.SpanStart)
            .ToArray();
        Assert.Equal(4, nullChecks.Length);
        Assert.Equal(
            ["session", "transaction", "manifestDigest", "managedBudget"],
            nullChecks.Select(OnlyArgument));

        var digestOwnership = Assert.Single(Invocations(method, "ValidateOwner"));
        Assert.Equal("manifestDigest.ValidateOwner", digestOwnership.Expression.ToString());
        AssertInvocationArguments(digestOwnership, "managedBudget");

        var borrow = Assert.Single(Invocations(method, "BorrowConnection"));
        AssertInvocationArguments(borrow);
        Assert.Equal("session.BorrowConnection", borrow.Expression.ToString());
        Assert.Equal("connection", AssignedLocal(borrow));
        var advisory = AssertSingleInvocation(
            method,
            "TransferV3PostgreSqlAdmissionLockSet.TryAcquireAdvisoryAsync");

        Assert.True(timeout.SpanStart < nullChecks[0].SpanStart);
        Assert.True(nullChecks[^1].SpanStart < digestOwnership.SpanStart);
        Assert.True(digestOwnership.SpanStart < borrow.SpanStart);
        Assert.True(borrow.SpanStart < advisory.SpanStart);
    }

    [Fact]
    public void DigestOwnerValidationAcceptsOnlyTheLiveCreatingBudget()
    {
        var owner = new TransferV3Phase4ManagedBudget();
        var other = new TransferV3Phase4ManagedBudget();
        var digest = TransferV3Phase4Digest.Create(owner, new byte[TransferV3Phase4Digest.SizeBytes]);

        digest.ValidateOwner(owner);
        AssertPhase4Code("phase4-argument", () => digest.ValidateOwner(other));

        digest.Dispose();
        AssertPhase4Code("phase4-argument", () => digest.ValidateOwner(owner));
    }

    [Fact]
    public void CanonicalSpanCodecWritesExactFreshAndImportingBytesAndRejectsMutations()
    {
        var expectedFresh = Encoding.UTF8.GetBytes(FreshCanonicalJson);
        var expectedImporting = Encoding.UTF8.GetBytes(
            $"{ImportingCanonicalPrefix}{CanonicalDigest}\"}}");
        Assert.Equal(TransferV3ImportStateCodec.FreshCanonicalUtf8Length, expectedFresh.Length);
        Assert.Equal(
            TransferV3ImportStateCodec.ImportingCanonicalUtf8Length,
            expectedImporting.Length);

        var fresh = new byte[TransferV3ImportStateCodec.FreshCanonicalUtf8Length];
        TransferV3ImportStateCodec.WriteFreshCanonical(fresh);
        Assert.Equal(expectedFresh, fresh);

        var importing = new byte[TransferV3ImportStateCodec.ImportingCanonicalUtf8Length];
        var manifestSha256Utf8 =
            TransferV3ImportStateCodec.InitializeImportingCanonical(importing);
        Assert.Equal(TransferV3Phase4Digest.SizeBytes * 2, manifestSha256Utf8.Length);
        var importingPrefixLength = Encoding.UTF8.GetByteCount(ImportingCanonicalPrefix);
        manifestSha256Utf8[0] = (byte)'f';
        Assert.Equal((byte)'f', importing[importingPrefixLength]);
        Encoding.ASCII.GetBytes(CanonicalDigest).AsSpan().CopyTo(manifestSha256Utf8);

        Assert.Equal(expectedImporting, importing);
        Assert.True(
            TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
                fresh,
                importing));

        Assert.Throws<ArgumentException>(() => WriteFreshCanonical(new byte[
            TransferV3ImportStateCodec.FreshCanonicalUtf8Length - 1]));
        Assert.Throws<ArgumentException>(() => WriteFreshCanonical(new byte[
            TransferV3ImportStateCodec.FreshCanonicalUtf8Length + 1]));
        Assert.Throws<ArgumentException>(() => InitializeImportingCanonical(new byte[
            TransferV3ImportStateCodec.ImportingCanonicalUtf8Length - 1]));
        Assert.Throws<ArgumentException>(() => InitializeImportingCanonical(new byte[
            TransferV3ImportStateCodec.ImportingCanonicalUtf8Length + 1]));

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
        uppercaseDigest[importingPrefixLength + 10] = (byte)'A';
        Assert.False(TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
            fresh,
            uppercaseDigest));

        var invalidDigest = (byte[])importing.Clone();
        invalidDigest[importingPrefixLength] = (byte)'g';
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
    public void RawCanonicalStoreOverloadValidatesThenDelegatesWithoutOwningBuffersOrLifecycle()
    {
        const string methodName =
            "TryTransitionCanonicalFreshToImportingInPostgreSqlTransactionAsync";
        var reflected = typeof(TransferV3ImportStateStore).GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(NpgsqlConnection),
                typeof(NpgsqlTransaction),
                typeof(byte[]),
                typeof(byte[]),
                typeof(int),
                typeof(CancellationToken),
            ],
            modifiers: null);
        Assert.NotNull(reflected);
        Assert.Equal(typeof(Task<int>), reflected.ReturnType);
        Assert.Single(
            typeof(TransferV3ImportStateStore)
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            candidate => candidate.Name == methodName);

        var root = ParseSource(ImportStateStoreSourcePath);
        var store = Assert.Single(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == "TransferV3ImportStateStore");
        var method = Assert.Single(
            store.Members.OfType<MethodDeclarationSyntax>(),
            candidate => candidate.Identifier.ValueText == methodName);
        Assert.Equal(
            [
                ("NpgsqlConnection", "connection"),
                ("NpgsqlTransaction", "transaction"),
                ("byte[]", "expectedCanonicalUtf8"),
                ("byte[]", "nextCanonicalUtf8"),
                ("int", "commandTimeoutSeconds"),
                ("CancellationToken", "cancellationToken"),
            ],
            method.ParameterList.Parameters.Select(parameter => (
                parameter.Type!.ToString(),
                parameter.Identifier.ValueText)));
        Assert.Equal(
            ["internal", "static", "async"],
            method.Modifiers.Select(modifier => modifier.ValueText));
        Assert.Equal("Task<int>", method.ReturnType.ToString());

        var timeout = Assert.Single(Invocations(method, "ThrowIfLessThan"));
        AssertInvocationArguments(timeout, "commandTimeoutSeconds", "1");
        var nullChecks = Invocations(method, "ThrowIfNull")
            .OrderBy(invocation => invocation.SpanStart)
            .ToArray();
        Assert.Equal(
            ["connection", "transaction", "expectedCanonicalUtf8", "nextCanonicalUtf8"],
            nullChecks.Select(OnlyArgument));

        var canonical = AssertSingleInvocation(
            method,
            "TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition");
        AssertInvocationArguments(canonical, "expectedCanonicalUtf8", "nextCanonicalUtf8");
        var canonicalNot = Assert.IsType<PrefixUnaryExpressionSyntax>(canonical.Parent);
        Assert.True(canonicalNot.IsKind(SyntaxKind.LogicalNotExpression));
        var canonicalGuard = Assert.IsType<IfStatementSyntax>(canonicalNot.Parent);
        AssertDirectZeroReturn(canonicalGuard.Statement);

        var transactionValidation = AssertSingleInvocation(
            method,
            "ValidatePostgreSqlTransactionContext");
        AssertInvocationArguments(transactionValidation, "connection", "transaction");
        var cancellation = AssertSingleInvocation(
            method,
            "cancellationToken.ThrowIfCancellationRequested");
        AssertInvocationArguments(cancellation);
        var execute = AssertSingleInvocation(method, "ExecutePostgreSqlCasCommandAsync");
        AssertInvocationArguments(
            execute,
            "connection",
            "transaction",
            "commandTimeoutSeconds",
            "expectedCanonicalUtf8",
            "nextCanonicalUtf8",
            "cancellationToken");
        AssertAwaited(execute, method);

        Assert.True(timeout.SpanStart < nullChecks[0].SpanStart);
        Assert.True(nullChecks[^1].SpanStart < canonical.SpanStart);
        Assert.True(canonicalGuard.Span.End < transactionValidation.SpanStart);
        Assert.True(transactionValidation.SpanStart < cancellation.SpanStart);
        Assert.True(cancellation.SpanStart < execute.SpanStart);

        Assert.Empty(method.DescendantNodes().OfType<ArrayCreationExpressionSyntax>());
        Assert.Empty(method.DescendantNodes().OfType<ImplicitArrayCreationExpressionSyntax>());
        Assert.Empty(method.DescendantNodes().OfType<StackAllocArrayCreationExpressionSyntax>());
        Assert.Empty(method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>());
        var source = method.ToFullString();
        Assert.DoesNotContain("Serialize", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ZeroMemory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransferV3ImportState.Fresh", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransferV3ImportState.Importing", source, StringComparison.Ordinal);
        var forbiddenLifecycle = new HashSet<string>(StringComparer.Ordinal)
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
            "DisposeAsync",
            "CreateCommand",
        };
        Assert.DoesNotContain(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => forbiddenLifecycle.Contains(InvocationName(invocation)));
        Assert.Single(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ExecutePostgreSqlCasCommandAsync");
    }

    [Fact]
    public void AdmissionUsesOneBorrowedContextForTheExactLockedPreflightOrder()
    {
        var method = EntryPoint();
        var borrow = Assert.Single(Invocations(method, "BorrowConnection"));
        var connection = AssignedLocal(borrow);
        Assert.Equal("connection", connection);

        var advisory = AssertSingleInvocation(
            method,
            "TransferV3PostgreSqlAdmissionLockSet.TryAcquireAdvisoryAsync");
        AssertInvocationArguments(
            advisory,
            connection,
            "transaction",
            "commandTimeoutSeconds",
            "cancellationToken");
        AssertAwaited(advisory, method);
        var advisoryGuard = AssertFailureGuard(advisory, method);

        var relations = AssertSingleInvocation(
            method,
            "TransferV3PostgreSqlAdmissionLockSet.AcquireRelationsAsync");
        AssertInvocationArguments(
            relations,
            connection,
            "transaction",
            "commandTimeoutSeconds",
            "cancellationToken");
        AssertAwaited(relations, method);

        var server = AssertSingleInvocation(
            method,
            "TransferV3PostgreSqlServerContract.ValidateAndCaptureAsync");
        AssertInvocationArguments(
            server,
            connection,
            "transaction",
            "session.TimeZoneId",
            "commandTimeoutSeconds",
            "cancellationToken");
        AssertAwaited(server, method);
        var capturedIdentity = AssignedLocal(server);

        var identityEquality = Assert.Single(
            Invocations(method, "Equals"),
            invocation => invocation.Expression.ToString() == "Equals"
                          && invocation.ArgumentList.Arguments.Count == 2);
        AssertInvocationArguments(identityEquality, capturedIdentity, "session.Identity");
        var identityGuard = AssertNegatedFailureGuard(identityEquality);

        var environment = AssertSingleInvocation(
            method,
            "PostgreSqlEnvironmentContract.ValidateAsync");
        AssertInvocationArguments(
            environment,
            connection,
            "transaction",
            "commandTimeoutSeconds",
            "cancellationToken");
        AssertAwaited(environment, method);
        var environmentSchema = AssignedLocal(environment);

        var schemaEquality = Assert.Single(
            Invocations(method, "Equals"),
            invocation => invocation.Expression.ToString() == "string.Equals");
        AssertInvocationArguments(
            schemaEquality,
            environmentSchema,
            "session.Identity.SchemaName",
            "StringComparison.Ordinal");
        var schemaGuard = AssertNegatedFailureGuard(schemaEquality);

        var catalog = AssertSingleInvocation(
            method,
            "PostgreSqlPhysicalCatalogContract.ValidateAsync");
        AssertInvocationArguments(
            catalog,
            connection,
            "transaction",
            "PostgreSqlCatalogState.Head",
            "commandTimeoutSeconds",
            "cancellationToken");
        AssertAwaited(catalog, method);

        var history = AssertSingleInvocation(
            method,
            "PostgreSqlNativeMigrationContract.ValidateHeadAsync");
        AssertInvocationArguments(
            history,
            connection,
            "transaction",
            "commandTimeoutSeconds",
            "cancellationToken");
        AssertAwaited(history, method);

        var bootstrap = AssertSingleInvocation(
            method,
            "PostgreSqlFreshBootstrapContract.ValidateAsync");
        AssertInvocationArguments(
            bootstrap,
            connection,
            "transaction",
            "commandTimeoutSeconds",
            "cancellationToken");
        AssertAwaited(bootstrap, method);

        Assert.True(advisory.SpanStart < advisoryGuard.Span.End);
        Assert.True(advisoryGuard.Span.End < relations.SpanStart);
        Assert.True(relations.SpanStart < server.SpanStart);
        Assert.True(server.SpanStart < identityEquality.SpanStart);
        Assert.True(identityGuard.Span.End < environment.SpanStart);
        Assert.True(environment.SpanStart < schemaEquality.SpanStart);
        Assert.True(schemaGuard.Span.End < catalog.SpanStart);
        Assert.True(catalog.SpanStart < history.SpanStart);
        Assert.True(history.SpanStart < bootstrap.SpanStart);
    }

    [Fact]
    public void CanonicalFreshToImportingCasIsFullyChargedAndOccursOnlyAfterEveryPreflight()
    {
        var method = EntryPoint();
        var bootstrap = AssertSingleInvocation(
            method,
            "PostgreSqlFreshBootstrapContract.ValidateAsync");

        var reservations = Invocations(method, "Reserve")
            .Where(invocation => invocation.Expression.ToString() == "managedBudget.Reserve")
            .OrderBy(invocation => invocation.SpanStart)
            .ToArray();
        Assert.Collection(
            reservations,
            reservation => AssertInvocationArguments(
                reservation,
                "TransferV3ImportStateCodec.FreshCanonicalUtf8Length",
                "TransferV3Phase4MemoryKind.Copy"),
            reservation => AssertInvocationArguments(
                reservation,
                "TransferV3ImportStateCodec.ImportingCanonicalUtf8Length",
                "TransferV3Phase4MemoryKind.Copy"));
        var expectedLease = AssignedStorage(reservations[0]);
        var nextLease = AssignedStorage(reservations[1]);

        var expectedAllocation = AssertArrayAllocation(
            method,
            "expectedCanonicalUtf8",
            "TransferV3ImportStateCodec.FreshCanonicalUtf8Length");
        var nextAllocation = AssertArrayAllocation(
            method,
            "nextCanonicalUtf8",
            "TransferV3ImportStateCodec.ImportingCanonicalUtf8Length");
        Assert.Equal(
            2,
            method.DescendantNodes().OfType<ArrayCreationExpressionSyntax>().Count());

        var expectedMark = AssertSingleInvocation(
            method,
            $"{expectedLease}.MarkManagedElementStorageAllocated");
        AssertInvocationArguments(expectedMark, "expectedCanonicalUtf8.Length");
        var nextMark = AssertSingleInvocation(
            method,
            $"{nextLease}.MarkManagedElementStorageAllocated");
        AssertInvocationArguments(nextMark, "nextCanonicalUtf8.Length");

        var writeFresh = AssertSingleInvocation(
            method,
            "TransferV3ImportStateCodec.WriteFreshCanonical");
        AssertInvocationArguments(writeFresh, "expectedCanonicalUtf8");
        var initializeImporting = AssertSingleInvocation(
            method,
            "TransferV3ImportStateCodec.InitializeImportingCanonical");
        AssertInvocationArguments(initializeImporting, "nextCanonicalUtf8");
        Assert.Equal("manifestSha256Utf8", AssignedLocal(initializeImporting));

        var digestCopy = Assert.Single(Invocations(method, "CopyLowerHexTo"));
        Assert.Equal("manifestDigest.CopyLowerHexTo", digestCopy.Expression.ToString());
        AssertInvocationArguments(digestCopy, "manifestSha256Utf8");

        var legalTransition = AssertSingleInvocation(
            method,
            "TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition");
        AssertInvocationArguments(
            legalTransition,
            "expectedCanonicalUtf8",
            "nextCanonicalUtf8");
        var legalTransitionGuard = AssertFailureGuard(legalTransition, method);

        var cas = AssertSingleInvocation(
            method,
            "TransferV3ImportStateStore.TryTransitionCanonicalFreshToImportingInPostgreSqlTransactionAsync");
        AssertInvocationArguments(
            cas,
            "connection",
            "transaction",
            "expectedCanonicalUtf8",
            "nextCanonicalUtf8",
            "commandTimeoutSeconds",
            "cancellationToken");
        AssertAwaited(cas, method);
        var changedRows = AssignedLocal(cas);
        var changedRowsGuard = Assert.Single(
            method.DescendantNodes().OfType<IfStatementSyntax>(),
            statement => statement.Condition.ToString() == $"{changedRows} != 1");
        AssertDirectThrow(changedRowsGuard.Statement);

        Assert.True(bootstrap.SpanStart < reservations[0].SpanStart);
        Assert.True(reservations[0].SpanStart < reservations[1].SpanStart);
        Assert.True(reservations[1].Span.End < expectedAllocation.SpanStart);
        Assert.True(reservations[1].Span.End < nextAllocation.SpanStart);
        Assert.True(expectedAllocation.SpanStart < expectedMark.SpanStart);
        Assert.True(nextAllocation.SpanStart < nextMark.SpanStart);
        Assert.True(expectedMark.SpanStart < writeFresh.SpanStart);
        Assert.True(nextMark.SpanStart < writeFresh.SpanStart);
        Assert.True(writeFresh.SpanStart < initializeImporting.SpanStart);
        Assert.True(initializeImporting.SpanStart < digestCopy.SpanStart);
        Assert.True(digestCopy.SpanStart < legalTransition.SpanStart);
        Assert.True(legalTransitionGuard.Span.End < cas.SpanStart);
        Assert.True(cas.SpanStart < changedRowsGuard.SpanStart);

        var digestMemberReferences = method.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(identifier => identifier.Identifier.ValueText == "manifestDigest")
            .Where(identifier => identifier.Parent is MemberAccessExpressionSyntax)
            .ToArray();
        Assert.Equal(2, digestMemberReferences.Length);
        Assert.Contains(
            digestMemberReferences,
            identifier => identifier.Parent is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "ValidateOwner",
            });
        Assert.Contains(
            digestMemberReferences,
            identifier => identifier.Parent is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "CopyLowerHexTo",
            });

        var source = method.ToFullString();
        Assert.DoesNotContain("Encoding.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetString", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransferV3ImportState.Fresh", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransferV3ImportState.Importing", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TryTransitionInPostgreSqlTransactionAsync",
            source,
            StringComparison.Ordinal);
        Assert.Single(
            method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation)
                          == "TryTransitionCanonicalFreshToImportingInPostgreSqlTransactionAsync");

        AssertCanonicalBuffersAreZeroedBeforeLeasesRelease(
            method,
            expectedLease,
            nextLease);
    }

    [Fact]
    public void EveryOperationalFailureIsSanitizedAtThePostgreSqlCommandBoundary()
    {
        var method = EntryPoint();
        var operationalTry = Assert.Single(
            method.DescendantNodes().OfType<TryStatementSyntax>(),
            statement => statement.Catches.Count == 1
                         && statement.Finally is not null);
        var catchClause = Assert.Single(operationalTry.Catches);
        Assert.NotNull(catchClause.Declaration);
        Assert.Equal("Exception", catchClause.Declaration!.Type.ToString());
        Assert.Equal("raw", catchClause.Declaration.Identifier.ValueText);
        Assert.Null(catchClause.Filter);

        var throwStatement = Assert.Single(
            catchClause.Block.Statements.OfType<ThrowStatementSyntax>());
        var sanitize = Assert.IsType<InvocationExpressionSyntax>(throwStatement.Expression);
        Assert.Equal(
            "TransferV3Phase4FailureMapper.Sanitize",
            sanitize.Expression.ToString());
        AssertInvocationArguments(
            sanitize,
            "raw",
            "TransferV3Phase4Boundary.PostgreSqlCommand",
            "cancellationToken");

        var advisory = AssertSingleInvocation(
            method,
            "TransferV3PostgreSqlAdmissionLockSet.TryAcquireAdvisoryAsync");
        var cas = AssertSingleInvocation(
            method,
            "TransferV3ImportStateStore.TryTransitionCanonicalFreshToImportingInPostgreSqlTransactionAsync");
        Assert.True(operationalTry.Block.Span.Contains(advisory.Span));
        Assert.True(operationalTry.Block.Span.Contains(cas.Span));
    }

    [Fact]
    public void SessionExposesOnlyTheDescriptorFrozenTimeZoneId()
    {
        var root = ParseSource(SessionSourcePath);
        var session = Assert.Single(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText == "TransferV3PostgreSqlSession");
        var property = Assert.Single(
            session.Members.OfType<PropertyDeclarationSyntax>(),
            candidate => candidate.Identifier.ValueText == "TimeZoneId");

        Assert.Equal("string", property.Type.ToString());
        Assert.Equal(["internal"], property.Modifiers.Select(modifier => modifier.ValueText));
        Assert.Null(property.AccessorList);
        Assert.NotNull(property.ExpressionBody);
        Assert.Equal("_descriptor.TimeZoneId", property.ExpressionBody!.Expression.ToString());
    }

    [Fact]
    public void SharedTransactionValidatorsProveReadinessAndForwardEveryArgumentExactly()
    {
        AssertSharedTransactionWrapper(
            ServerContractSourcePath,
            "TransferV3PostgreSqlServerContract",
            "ValidateAndCaptureAsync",
            [
                "connection",
                "transaction",
                "expectedTimeZoneId",
                "commandTimeoutSeconds",
                "cancellationToken",
            ]);
        AssertSharedTransactionWrapper(
            EnvironmentContractSourcePath,
            "PostgreSqlEnvironmentContract",
            "ValidateAsync",
            [
                "connection",
                "transaction",
                "RequiredServerVersion",
                "RequiredServerVersionNumber",
                "commandTimeoutSeconds",
                "cancellationToken",
            ]);
    }

    [Fact]
    public void ValidatorOwnsNoSqlTransactionConnectionSessionOrDigestLifecycle()
    {
        var type = ParseValidator();
        var reachable = ReachableSameClassMethods(type, EntryPoint())
            .SelectMany(method => method.DescendantNodesAndSelf())
            .ToArray();
        var source = string.Join(
            '\n',
            ReachableSameClassMethods(type, EntryPoint())
                .Select(method => method.ToFullString()));

        Assert.DoesNotContain("ConfigItems", source, StringComparison.Ordinal);
        Assert.DoesNotContain("database.import-state", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            reachable.OfType<LiteralExpressionSyntax>(),
            literal => literal.IsKind(SyntaxKind.StringLiteralExpression)
                       && System.Text.RegularExpressions.Regex.IsMatch(
                           literal.Token.ValueText,
                           "(?is)^\\s*(SELECT|INSERT|UPDATE|DELETE|MERGE|COPY|LOCK|SET|RESET|DISCARD|ALTER|CREATE|DROP|TRUNCATE)\\b"));
        Assert.DoesNotContain("PostgreSqlCasSql", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            @"(?is)\b(?:session|connection|transaction|manifestDigest)\s*\??\.\s*Dispose(?:Async)?\s*\(",
            source);
        Assert.DoesNotContain(
            reachable.OfType<ObjectCreationExpressionSyntax>(),
            creation => creation.Type.ToString() is "NpgsqlConnection" or "NpgsqlCommand");

        var forbiddenCalls = new HashSet<string>(StringComparer.Ordinal)
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
            "CloseWithinAsync",
            "Quarantine",
            "DisposeAsync",
            "CreateCommand",
            "ExecuteNonQuery",
            "ExecuteNonQueryAsync",
            "ExecuteReader",
            "ExecuteReaderAsync",
            "ExecuteScalar",
            "ExecuteScalarAsync",
            "TryTransitionAsync",
        };
        Assert.DoesNotContain(
            reachable.OfType<InvocationExpressionSyntax>(),
            invocation => forbiddenCalls.Contains(InvocationName(invocation)));

        Assert.Single(
            reachable.OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation)
                          == "TryTransitionCanonicalFreshToImportingInPostgreSqlTransactionAsync");
    }

    private static ArrayCreationExpressionSyntax AssertArrayAllocation(
        MethodDeclarationSyntax method,
        string localName,
        string expectedLength)
    {
        var allocation = Assert.Single(
            method.DescendantNodes().OfType<ArrayCreationExpressionSyntax>(),
            creation => creation.Type.ElementType.ToString() == "byte"
                        && Assert.Single(creation.Type.RankSpecifiers)
                            .Sizes.Single().ToString() == expectedLength);
        var assignedName = allocation.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.Left.ToString(),
            EqualsValueClauseSyntax
            {
                Parent: VariableDeclaratorSyntax declarator,
            } => declarator.Identifier.ValueText,
            _ => null,
        };
        Assert.Equal(localName, assignedName);
        return allocation;
    }

    private static void AssertCanonicalBuffersAreZeroedBeforeLeasesRelease(
        MethodDeclarationSyntax method,
        string expectedLease,
        string nextLease)
    {
        var operationalTry = Assert.Single(
            method.DescendantNodes().OfType<TryStatementSyntax>(),
            statement => statement.Catches.Count == 1
                         && statement.Finally is not null);
        var finallyClause = operationalTry.Finally!;
        var expectedClear = AssertExactNonNullZeroAndClearGuard(
            finallyClause,
            "expectedCanonicalUtf8");
        var nextClear = AssertExactNonNullZeroAndClearGuard(
            finallyClause,
            "nextCanonicalUtf8");
        Assert.Equal(
            2,
            finallyClause.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Count(invocation => InvocationName(invocation) == "ZeroMemory"));

        var expectedRelease = LeaseDisposeStart(finallyClause, expectedLease);
        var nextRelease = LeaseDisposeStart(finallyClause, nextLease);
        var firstRelease = Math.Min(expectedRelease, nextRelease);
        Assert.True(expectedClear.Span.End < firstRelease);
        Assert.True(nextClear.Span.End < firstRelease);
        Assert.True(nextRelease < expectedRelease);
    }

    private static AssignmentExpressionSyntax AssertExactNonNullZeroAndClearGuard(
        FinallyClauseSyntax finallyClause,
        string buffer)
    {
        var guard = Assert.Single(
            finallyClause.Block.Statements.OfType<IfStatementSyntax>(),
            statement => statement.Condition.ToString() == $"{buffer} is not null");
        var block = Assert.IsType<BlockSyntax>(guard.Statement);
        Assert.Equal(2, block.Statements.Count);
        var zeroStatement = Assert.IsType<ExpressionStatementSyntax>(block.Statements[0]);
        var zero = Assert.IsType<InvocationExpressionSyntax>(zeroStatement.Expression);
        Assert.Equal("CryptographicOperations.ZeroMemory", zero.Expression.ToString());
        AssertInvocationArguments(zero, buffer);
        var clearStatement = Assert.IsType<ExpressionStatementSyntax>(block.Statements[1]);
        var clear = Assert.IsType<AssignmentExpressionSyntax>(clearStatement.Expression);
        Assert.True(clear.IsKind(SyntaxKind.SimpleAssignmentExpression));
        Assert.Equal(buffer, clear.Left.ToString());
        Assert.True(clear.Right.IsKind(SyntaxKind.NullLiteralExpression));
        Assert.True(zero.SpanStart < clear.SpanStart);
        return clear;
    }

    private static int LeaseDisposeStart(FinallyClauseSyntax finallyClause, string lease)
    {
        var conditional = Assert.Single(
            finallyClause.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>(),
            access => access.Expression.ToString() == lease
                      && access.WhenNotNull.DescendantNodesAndSelf()
                          .OfType<InvocationExpressionSyntax>()
                          .Any(invocation => InvocationName(invocation) == "Dispose"));
        return conditional.SpanStart;
    }

    private static void AssertSharedTransactionWrapper(
        string sourcePath,
        string typeName,
        string wrapperName,
        string[] exactCoreArguments)
    {
        var root = ParseSource(sourcePath);
        var type = Assert.Single(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            candidate => candidate.Identifier.ValueText == typeName);
        var wrapper = Assert.Single(
            type.Members.OfType<MethodDeclarationSyntax>(),
            candidate => candidate.Identifier.ValueText == wrapperName
                         && candidate.ParameterList.Parameters.Count >= 2
                         && candidate.ParameterList.Parameters[1].Type?.ToString()
                         == "NpgsqlTransaction");
        var transactionValidation = Assert.Single(
            wrapper.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "ValidateTransactionContext");
        AssertInvocationArguments(transactionValidation, "connection", "transaction");
        var coreDelegation = Assert.Single(
            wrapper.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) is
                "ValidateAndCaptureCoreAsync" or "ValidateCoreAsync");
        AssertInvocationArguments(coreDelegation, exactCoreArguments);
        Assert.True(transactionValidation.SpanStart < coreDelegation.SpanStart);

        var helper = Assert.Single(
            type.Members.OfType<MethodDeclarationSyntax>(),
            candidate => candidate.Identifier.ValueText == "ValidateTransactionContext");
        var openGuard = Assert.Single(
            helper.DescendantNodes().OfType<IfStatementSyntax>(),
            statement => statement.Condition.ToString()
                         == "connection.State != ConnectionState.Open");
        AssertDirectThrow(openGuard.Statement);

        var ownership = Assert.Single(
            helper.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => invocation.Expression.ToString() == "ReferenceEquals");
        AssertInvocationArguments(ownership, "transactionConnection", "connection");
        var ownershipGuard = AssertNegatedFailureGuard(ownership);
        var readiness = Assert.Single(
            helper.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            member => member.ToString() == "transaction.IsolationLevel");

        Assert.True(openGuard.Span.End < ownership.SpanStart);
        Assert.True(ownershipGuard.Span.End < readiness.SpanStart);
    }

    private static IfStatementSyntax AssertFailureGuard(
        InvocationExpressionSyntax invocation,
        MethodDeclarationSyntax method)
    {
        var directGuard = invocation.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        if (directGuard is not null)
        {
            AssertDirectThrow(directGuard.Statement);
            return directGuard;
        }

        var localName = AssignedLocal(invocation);
        var guard = Assert.Single(
            method.DescendantNodes().OfType<IfStatementSyntax>(),
            statement => statement.Condition.DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Any(identifier => identifier.Identifier.ValueText == localName));
        var logicalNot = Assert.IsType<PrefixUnaryExpressionSyntax>(guard.Condition);
        Assert.True(logicalNot.IsKind(SyntaxKind.LogicalNotExpression));
        Assert.Equal(localName, logicalNot.Operand.ToString());
        AssertDirectThrow(guard.Statement);
        return guard;
    }

    private static IfStatementSyntax AssertNegatedFailureGuard(
        InvocationExpressionSyntax equality)
    {
        var logicalNot = Assert.IsType<PrefixUnaryExpressionSyntax>(equality.Parent);
        Assert.True(logicalNot.IsKind(SyntaxKind.LogicalNotExpression));
        var guard = Assert.IsType<IfStatementSyntax>(logicalNot.Parent);
        AssertDirectThrow(guard.Statement);
        return guard;
    }

    private static void AssertDirectThrow(StatementSyntax statement)
    {
        if (statement is BlockSyntax block)
            Assert.Single(block.Statements.OfType<ThrowStatementSyntax>());
        else
            Assert.IsType<ThrowStatementSyntax>(statement);
    }

    private static void AssertDirectZeroReturn(StatementSyntax statement)
    {
        var returnStatement = statement is BlockSyntax block
            ? Assert.Single(block.Statements.OfType<ReturnStatementSyntax>())
            : Assert.IsType<ReturnStatementSyntax>(statement);
        Assert.NotNull(returnStatement.Expression);
        Assert.Equal("0", returnStatement.Expression!.ToString());
    }

    private static void AssertPhase4Code(string expected, Action action)
    {
        var failure = Assert.IsType<TransferV3Phase4Exception>(Record.Exception(action));
        Assert.Equal(expected, failure.Code);
        Assert.Equal("Transfer-v3 Phase 4 failed.", failure.Message);
    }

    private static void WriteFreshCanonical(byte[] destination) =>
        TransferV3ImportStateCodec.WriteFreshCanonical(destination);

    private static void InitializeImportingCanonical(byte[] destination)
    {
        _ = TransferV3ImportStateCodec.InitializeImportingCanonical(destination);
    }

    private static void AssertAwaited(
        InvocationExpressionSyntax invocation,
        MethodDeclarationSyntax owner)
    {
        var awaited = Assert.Single(
            invocation.Ancestors().OfType<AwaitExpressionSyntax>(),
            expression => owner.Span.Contains(expression.Span));
        Assert.True(owner.Span.Contains(awaited.Span));
    }

    private static string AssignedLocal(InvocationExpressionSyntax invocation)
    {
        var declarator = Assert.Single(
            invocation.Ancestors().OfType<VariableDeclaratorSyntax>());
        Assert.NotNull(declarator.Initializer);
        Assert.True(declarator.Initializer.Value.Span.Contains(invocation.Span));
        return declarator.Identifier.ValueText;
    }

    private static string AssignedStorage(InvocationExpressionSyntax invocation)
    {
        var assignment = invocation.Ancestors().OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(candidate => candidate.Right.Span.Contains(invocation.Span));
        if (assignment is not null)
            return assignment.Left.ToString();

        return AssignedLocal(invocation);
    }

    private static void AssertInvocationArguments(
        InvocationExpressionSyntax invocation,
        params string[] expected)
    {
        Assert.Equal(
            expected,
            invocation.ArgumentList.Arguments
                .Select(argument => argument.Expression.ToString()));
    }

    private static string OnlyArgument(InvocationExpressionSyntax invocation) =>
        Assert.Single(invocation.ArgumentList.Arguments).Expression.ToString();

    private static InvocationExpressionSyntax AssertSingleInvocation(
        SyntaxNode node,
        string expression) => Assert.Single(
        node.DescendantNodes().OfType<InvocationExpressionSyntax>(),
        invocation => invocation.Expression.ToString() == expression);

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
            MemberBindingExpressionSyntax memberBinding =>
                memberBinding.Name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => string.Empty,
        };

    private static IReadOnlyList<MethodDeclarationSyntax> ReachableSameClassMethods(
        ClassDeclarationSyntax type,
        MethodDeclarationSyntax entryPoint)
    {
        var methods = type.Members.OfType<MethodDeclarationSyntax>().ToArray();
        var methodsByName = methods
            .GroupBy(method => method.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var reachable = new HashSet<MethodDeclarationSyntax>();
        var pending = new Queue<MethodDeclarationSyntax>();
        pending.Enqueue(entryPoint);

        while (pending.TryDequeue(out var method))
        {
            if (!reachable.Add(method))
                continue;

            foreach (var invocation in method.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                var candidateName = invocation.Expression switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax receiver,
                    } member when receiver.Identifier.ValueText is
                        "this" or "TransferV3PostgreSqlAdmissionValidator" =>
                        member.Name.Identifier.ValueText,
                    _ => null,
                };
                if (candidateName is null
                    || !methodsByName.TryGetValue(candidateName, out var candidates))
                {
                    continue;
                }

                foreach (var candidate in candidates)
                    pending.Enqueue(candidate);
            }
        }

        return reachable.ToArray();
    }

    private static MethodDeclarationSyntax EntryPoint()
    {
        var type = ParseValidator();
        return Assert.Single(
            type.Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText
                          == "ValidateFreshAndMarkImportingAsync"
                      && method.ParameterList.Parameters.Count == 6);
    }

    private static ClassDeclarationSyntax ParseValidator()
    {
        var root = ParseSource(AdmissionSourcePath);
        return Assert.Single(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            type => type.Identifier.ValueText
                    == "TransferV3PostgreSqlAdmissionValidator");
    }

    private static SyntaxNode ParseSource(string sourcePath)
    {
        var path = SqliteContractTestSupport.AbsolutePath(sourcePath);
        var source = File.ReadAllText(path);
        var tree = CSharpSyntaxTree.ParseText(source, path: sourcePath);
        var errors = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
        return tree.GetRoot();
    }
}
