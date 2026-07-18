using System.Reflection;
using Npgsql;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3Phase4FailureTests
{
    private const string FixedFailureMessage = "Transfer-v3 Phase 4 failed.";
    private const string FixedCancellationMessage = "Transfer-v3 Phase 4 was canceled.";

    public static TheoryData<int, string> BoundaryMappings => new()
    {
        { (int)TransferV3Phase4Boundary.Argument, "phase4-argument" },
        { (int)TransferV3Phase4Boundary.Parser, "phase4-parser" },
        { (int)TransferV3Phase4Boundary.Codec, "phase4-codec" },
        { (int)TransferV3Phase4Boundary.PostgreSqlOpen, "phase4-postgresql-open" },
        { (int)TransferV3Phase4Boundary.PostgreSqlCommand, "phase4-postgresql-command" },
        { (int)TransferV3Phase4Boundary.PostgreSqlCopy, "phase4-postgresql-copy" },
        { (int)TransferV3Phase4Boundary.PostgreSqlCommit, "phase4-postgresql-commit" },
        { (int)TransferV3Phase4Boundary.Posix, "phase4-posix" },
        { (int)TransferV3Phase4Boundary.Cleanup, "phase4-cleanup" },
        { (int)TransferV3Phase4Boundary.Unexpected, "phase4-unexpected" },
    };

    public static TheoryData<int, string> SecondaryMappings => new()
    {
        { (int)TransferV3Phase4SecondaryCode.ObserverAbortFailed, "observer-abort-failed" },
        { (int)TransferV3Phase4SecondaryCode.CopyCancelFailed, "copy-cancel-failed" },
        {
            (int)TransferV3Phase4SecondaryCode.TransactionRollbackFailed,
            "transaction-rollback-failed"
        },
        { (int)TransferV3Phase4SecondaryCode.SpoolResidue, "spool-residue" },
        { (int)TransferV3Phase4SecondaryCode.BlobStageResidue, "blob-stage-residue" },
        {
            (int)TransferV3Phase4SecondaryCode.FailedStateCasZeroRows,
            "failed-state-cas-zero-rows"
        },
        {
            (int)TransferV3Phase4SecondaryCode.FailedStateCasUnknown,
            "failed-state-cas-unknown"
        },
        {
            (int)TransferV3Phase4SecondaryCode.ConnectionCloseFailed,
            "connection-close-failed"
        },
        {
            (int)TransferV3Phase4SecondaryCode.DataSourceDisposeFailed,
            "data-source-dispose-failed"
        },
        {
            (int)TransferV3Phase4SecondaryCode.SourceReadCloseFailed,
            "source-read-close-failed"
        },
        {
            (int)TransferV3Phase4SecondaryCode.DeadlineAbandonedProviderTask,
            "deadline-abandoned-provider-task"
        },
        {
            (int)TransferV3Phase4SecondaryCode.CleanupDeadlineExceeded,
            "cleanup-deadline-exceeded"
        },
        {
            (int)TransferV3Phase4SecondaryCode.CommitOutcomeUnknown,
            "commit-outcome-unknown"
        },
    };

    public static TheoryData<int> PostgreSqlBoundaries => new()
    {
        (int)TransferV3Phase4Boundary.PostgreSqlOpen,
        (int)TransferV3Phase4Boundary.PostgreSqlCommand,
        (int)TransferV3Phase4Boundary.PostgreSqlCopy,
        (int)TransferV3Phase4Boundary.PostgreSqlCommit,
    };

    public static TheoryData<int> NonPostgreSqlAndInvalidBoundaries => new()
    {
        (int)TransferV3Phase4Boundary.Argument,
        (int)TransferV3Phase4Boundary.Parser,
        (int)TransferV3Phase4Boundary.Codec,
        (int)TransferV3Phase4Boundary.Posix,
        (int)TransferV3Phase4Boundary.Cleanup,
        (int)TransferV3Phase4Boundary.Unexpected,
        int.MinValue,
    };

    public static TheoryData<string> InvalidSqlStates => new()
    {
        "abcd1",
        "23 05",
        "23-05",
        "23Å05",
        "23\0A5",
        "2350",
        "235050",
    };

    [Theory]
    [MemberData(nameof(BoundaryMappings))]
    public void Sanitize_MapsEveryBoundaryToItsExactFixedFailure(
        int boundaryValue,
        string expectedCode)
    {
        var boundary = (TransferV3Phase4Boundary)boundaryValue;
        var raw = new InvalidOperationException("raw-boundary-message-CANARY");

        var failure = Assert.IsType<TransferV3Phase4Exception>(
            TransferV3Phase4FailureMapper.Sanitize(raw, boundary, CancellationToken.None));

        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(FixedFailureMessage, failure.Message);
        Assert.Null(failure.SqlState);
        Assert.Empty(failure.SecondaryCodes);
        Assert.Null(failure.InnerException);
        Assert.Empty(failure.Data);
        Assert.DoesNotContain(raw.Message, failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Sanitize_InvalidBoundaryMapsToUnexpected(int invalidBoundary)
    {
        var failure = SanitizeFailure(
            new Exception("invalid-boundary-CANARY"),
            (TransferV3Phase4Boundary)invalidBoundary);

        Assert.Equal("phase4-unexpected", failure.Code);
        Assert.Equal(FixedFailureMessage, failure.Message);
        Assert.Null(failure.SqlState);
    }

    [Theory]
    [MemberData(nameof(SecondaryMappings))]
    public void TryAddSecondary_MapsEveryRecognizedValueToItsExactLiteral(
        int codeValue,
        string expectedLiteral)
    {
        var code = (TransferV3Phase4SecondaryCode)codeValue;
        var failure = SanitizeFailure(new Exception("secondary-map-CANARY"));

        Assert.True(failure.TryAddSecondary(code));
        Assert.Equal(new[] { expectedLiteral }, failure.SecondaryCodes);
    }

    [Fact]
    public void TryAddSecondary_UsesFourFixedSlotsWithStableOrderAndNoMutationOnRejection()
    {
        var failure = SanitizeFailure(new Exception("secondary-slots-CANARY"));
        var storageField = typeof(TransferV3Phase4Exception)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(string[]));
        var originalStorage = Assert.IsType<string[]>(storageField.GetValue(failure));
        var originalView = failure.SecondaryCodes;

        Assert.Equal(4, originalStorage.Length);
        Assert.All(originalStorage, Assert.Null);
        Assert.False(failure.TryAddSecondary(TransferV3Phase4SecondaryCode.None));
        Assert.False(failure.TryAddSecondary((TransferV3Phase4SecondaryCode)int.MinValue));
        Assert.Empty(failure.SecondaryCodes);

        Assert.True(failure.TryAddSecondary(TransferV3Phase4SecondaryCode.BlobStageResidue));
        Assert.True(failure.TryAddSecondary(TransferV3Phase4SecondaryCode.ObserverAbortFailed));
        Assert.False(failure.TryAddSecondary(TransferV3Phase4SecondaryCode.BlobStageResidue));
        Assert.True(failure.TryAddSecondary(TransferV3Phase4SecondaryCode.CommitOutcomeUnknown));
        Assert.True(failure.TryAddSecondary(TransferV3Phase4SecondaryCode.CopyCancelFailed));

        var fourEntries = new[]
        {
            "blob-stage-residue",
            "observer-abort-failed",
            "commit-outcome-unknown",
            "copy-cancel-failed",
        };
        Assert.Equal(fourEntries, failure.SecondaryCodes);

        Assert.False(failure.TryAddSecondary(
            TransferV3Phase4SecondaryCode.TransactionRollbackFailed));
        Assert.False(failure.TryAddSecondary(TransferV3Phase4SecondaryCode.None));
        Assert.False(failure.TryAddSecondary((TransferV3Phase4SecondaryCode)int.MaxValue));
        Assert.Equal(fourEntries, failure.SecondaryCodes);
        Assert.Same(originalStorage, storageField.GetValue(failure));
        Assert.Same(originalView, failure.SecondaryCodes);
    }

    [Fact]
    public void CleanupResult_PreservesItsFourFixedCodes()
    {
        var result = new TransferV3Phase4CleanupResult(
            TransferV3Phase4SecondaryCode.ObserverAbortFailed,
            TransferV3Phase4SecondaryCode.None,
            TransferV3Phase4SecondaryCode.SpoolResidue,
            TransferV3Phase4SecondaryCode.CommitOutcomeUnknown);

        Assert.Equal(TransferV3Phase4SecondaryCode.ObserverAbortFailed, result.First);
        Assert.Equal(TransferV3Phase4SecondaryCode.None, result.Second);
        Assert.Equal(TransferV3Phase4SecondaryCode.SpoolResidue, result.Third);
        Assert.Equal(TransferV3Phase4SecondaryCode.CommitOutcomeUnknown, result.Fourth);
    }

    [Fact]
    public void SanitizedFailure_ConstructionIsFactoryGated()
    {
        var constructors = typeof(TransferV3Phase4Exception)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotEmpty(constructors);
        Assert.All(constructors, constructor => Assert.True(constructor.IsPrivate));
    }

    [Fact]
    public void Sanitize_ExistingSanitizedFailureWinsAndIsPreservedByReference()
    {
        var original = SanitizeFailure(
            CreatePostgresException("23505"),
            TransferV3Phase4Boundary.PostgreSqlCopy);
        Assert.True(original.TryAddSecondary(
            TransferV3Phase4SecondaryCode.TransactionRollbackFailed));
        using var requestedCaller = new CancellationTokenSource();
        requestedCaller.Cancel();

        var returned = TransferV3Phase4FailureMapper.Sanitize(
            original,
            (TransferV3Phase4Boundary)int.MinValue,
            requestedCaller.Token);

        Assert.Same(original, returned);
        Assert.Equal("phase4-postgresql-copy", original.Code);
        Assert.Equal(FixedFailureMessage, original.Message);
        Assert.Equal("23505", original.SqlState);
        Assert.Equal(new[] { "transaction-rollback-failed" }, original.SecondaryCodes);
    }

    [Theory]
    [MemberData(nameof(PostgreSqlBoundaries))]
    public void Sanitize_ValidSqlStateRoundTripsAtEveryPostgreSqlBoundary(
        int boundaryValue)
    {
        var boundary = (TransferV3Phase4Boundary)boundaryValue;
        var failure = SanitizeFailure(CreatePostgresException("23505"), boundary);

        Assert.Equal("23505", failure.SqlState);
    }

    [Theory]
    [MemberData(nameof(NonPostgreSqlAndInvalidBoundaries))]
    public void Sanitize_PostgresSqlStateIsNullOutsidePostgreSqlBoundaries(
        int boundaryValue)
    {
        var boundary = (TransferV3Phase4Boundary)boundaryValue;
        var failure = SanitizeFailure(CreatePostgresException("23505"), boundary);

        Assert.Null(failure.SqlState);
    }

    [Theory]
    [MemberData(nameof(InvalidSqlStates))]
    public void Sanitize_RejectsEveryMalformedSqlState(string sqlState)
    {
        var raw = CreatePostgresException(sqlState);
        raw.Data["sqlstate-data-CANARY"] = "23505";

        var failure = SanitizeFailure(
            raw,
            TransferV3Phase4Boundary.PostgreSqlCommand);

        Assert.Null(failure.SqlState);
    }

    [Fact]
    public void Sanitize_DoesNotReadSqlStateFromGenericNpgsqlInnerExceptionOrData()
    {
        var raw = new NpgsqlException(
            "generic-npgsql-CANARY",
            CreatePostgresException("23505"));
        raw.Data["SqlState"] = "23505";

        var failure = SanitizeFailure(
            raw,
            TransferV3Phase4Boundary.PostgreSqlOpen);

        Assert.Null(failure.SqlState);
        Assert.Equal("phase4-postgresql-open", failure.Code);
    }

    [Fact]
    public void Sanitize_DropsAdversarialExceptionGraphAndCustomProperties()
    {
        var canaries = new[]
        {
            "message-CANARY-4f31",
            "inner-CANARY-5b72",
            "data-key-CANARY-609a",
            "data-value-CANARY-71bd",
            "/private/path-CANARY-82ce",
            "f82a4fae-6ad4-4b4c-a642-60e88fbb7b92",
            "api-key-CANARY-93df",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };
        var raw = new AdversarialException(canaries);

        var failure = SanitizeFailure(raw, TransferV3Phase4Boundary.Posix);
        Assert.True(failure.TryAddSecondary(TransferV3Phase4SecondaryCode.SpoolResidue));

        AssertNoCanaries(failure, canaries);
        Assert.DoesNotContain(
            typeof(TransferV3Phase4Exception)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => ReferenceEquals(raw, field.GetValue(failure)));
    }

    [Fact]
    public void Sanitize_DropsEveryAdversarialPostgresField()
    {
        var canaries = new[]
        {
            "pg-message-CANARY-a101",
            "pg-severity-CANARY-a202",
            "pg-invariant-severity-CANARY-a303",
            "pg-detail-CANARY-a404",
            "pg-hint-CANARY-a505",
            "pg-internal-query-CANARY-a606",
            "pg-context-CANARY-a707",
            "pg-schema-CANARY-a808",
            "pg-table-CANARY-a909",
            "pg-column-CANARY-b010",
            "pg-datatype-CANARY-b111",
            "pg-constraint-CANARY-b212",
            "/pg/file-CANARY-b313",
            "pg-line-CANARY-b414",
            "pg-routine-CANARY-b515",
            "pg-data-key-CANARY-b616",
            "pg-data-value-CANARY-b717",
        };
        var raw = new PostgresException(
            canaries[0],
            canaries[1],
            canaries[2],
            "23505",
            canaries[3],
            canaries[4],
            7,
            9,
            canaries[5],
            canaries[6],
            canaries[7],
            canaries[8],
            canaries[9],
            canaries[10],
            canaries[11],
            canaries[12],
            canaries[13],
            canaries[14]);
        raw.Data[canaries[15]] = canaries[16];

        var failure = SanitizeFailure(
            raw,
            TransferV3Phase4Boundary.PostgreSqlCommit);
        Assert.True(failure.TryAddSecondary(
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed));

        Assert.Equal("23505", failure.SqlState);
        AssertNoCanaries(failure, canaries);
        Assert.DoesNotContain(
            typeof(TransferV3Phase4Exception)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => ReferenceEquals(raw, field.GetValue(failure)));
    }

    [Fact]
    public void Sanitize_ExactRequestedCallerCancellationCreatesFreshSafeCancellation()
    {
        const string RawMessageCanary = "cancel-message-CANARY-c818";
        const string RawInnerCanary = "cancel-inner-CANARY-c919";
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var raw = new OperationCanceledException(
            RawMessageCanary,
            new InvalidOperationException(RawInnerCanary),
            caller.Token);
        raw.Data["cancel-data-CANARY-d020"] = "cancel-data-value-CANARY-d121";

        var returned = TransferV3Phase4FailureMapper.Sanitize(
            raw,
            TransferV3Phase4Boundary.Parser,
            caller.Token);
        var cancellation = Assert.IsType<OperationCanceledException>(returned);

        Assert.NotSame(raw, cancellation);
        Assert.Equal(FixedCancellationMessage, cancellation.Message);
        Assert.Equal(caller.Token, cancellation.CancellationToken);
        Assert.Null(cancellation.InnerException);
        Assert.Empty(cancellation.Data);
        Assert.DoesNotContain(RawMessageCanary, cancellation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RawInnerCanary, cancellation.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_CancellationNegativeMatrixMapsToFixedBoundaryFailure()
    {
        AssertNegativeCancellation(
            new OperationCanceledException(CancellationToken.None),
            CancellationToken.None);

        using var unrequested = new CancellationTokenSource();
        AssertNegativeCancellation(
            new OperationCanceledException(unrequested.Token),
            unrequested.Token);

        using var requestedCaller = new CancellationTokenSource();
        using var differentRequestedRaw = new CancellationTokenSource();
        requestedCaller.Cancel();
        differentRequestedRaw.Cancel();
        AssertNegativeCancellation(
            new OperationCanceledException(differentRequestedRaw.Token),
            requestedCaller.Token);

        AssertNegativeCancellation(
            new OperationCanceledException(CancellationToken.None),
            requestedCaller.Token);
    }

    private static void AssertNegativeCancellation(
        OperationCanceledException raw,
        CancellationToken callerToken)
    {
        var failure = SanitizeFailure(raw, TransferV3Phase4Boundary.Codec, callerToken);

        Assert.Equal("phase4-codec", failure.Code);
        Assert.Equal(FixedFailureMessage, failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Empty(failure.Data);
    }

    private static TransferV3Phase4Exception SanitizeFailure(
        Exception raw,
        TransferV3Phase4Boundary boundary = TransferV3Phase4Boundary.Unexpected,
        CancellationToken callerToken = default) =>
        Assert.IsType<TransferV3Phase4Exception>(
            TransferV3Phase4FailureMapper.Sanitize(raw, boundary, callerToken));

    private static PostgresException CreatePostgresException(string sqlState) =>
        new(
            "postgres-message-CANARY",
            "ERROR",
            "ERROR",
            sqlState);

    private static void AssertNoCanaries(
        TransferV3Phase4Exception failure,
        IEnumerable<string> canaries)
    {
        var exposedValues = new[]
        {
            failure.Message,
            failure.ToString(),
            failure.Code,
            failure.SqlState ?? string.Empty,
            string.Join('|', failure.SecondaryCodes),
            string.Join('|', failure.Data.Keys.Cast<object>()),
            string.Join('|', failure.Data.Values.Cast<object>()),
            failure.Source ?? string.Empty,
            failure.HelpLink ?? string.Empty,
            failure.StackTrace ?? string.Empty,
            failure.TargetSite?.ToString() ?? string.Empty,
        };

        foreach (var canary in canaries)
        {
            Assert.All(
                exposedValues,
                exposed => Assert.DoesNotContain(canary, exposed, StringComparison.Ordinal));
        }

        Assert.Null(failure.InnerException);
        Assert.Empty(failure.Data);
    }

    private sealed class AdversarialException : Exception
    {
        internal AdversarialException(IReadOnlyList<string> canaries)
            : base(canaries[0], new InvalidOperationException(canaries[1]))
        {
            Path = canaries[4];
            Uuid = canaries[5];
            ApiKey = canaries[6];
            Digest = canaries[7];
            Data[canaries[2]] = canaries[3];
        }

        internal string Path { get; }
        internal string Uuid { get; }
        internal string ApiKey { get; }
        internal string Digest { get; }

        public override string ToString() =>
            $"{base.ToString()} {Path} {Uuid} {ApiKey} {Digest}";
    }
}
