using System.Collections;
using Npgsql;

namespace NzbWebDAV.Database.Transfer;

internal enum TransferV3Phase4Boundary
{
    Argument,
    Parser,
    Codec,
    PostgreSqlOpen,
    PostgreSqlCommand,
    PostgreSqlCopy,
    PostgreSqlCommit,
    Posix,
    Cleanup,
    Unexpected,
}

internal enum TransferV3Phase4SecondaryCode
{
    None,
    ObserverAbortFailed,
    CopyCancelFailed,
    TransactionRollbackFailed,
    SpoolResidue,
    BlobStageResidue,
    FailedStateCasZeroRows,
    FailedStateCasUnknown,
    ConnectionCloseFailed,
    DataSourceDisposeFailed,
    SourceReadCloseFailed,
    DeadlineAbandonedProviderTask,
    CleanupDeadlineExceeded,
    CommitOutcomeUnknown,
}

internal sealed class TransferV3Phase4Exception : Exception
{
    private const int SecondaryCapacity = 4;
    private const string FixedMessage = "Transfer-v3 Phase 4 failed.";

    private readonly string[] _secondaryCodes;
    private readonly SecondaryCodeView _secondaryCodeView;
    private int _secondaryCodeCount;

    private TransferV3Phase4Exception(
        string code,
        string? sqlState)
        : base(FixedMessage)
    {
        Code = code;
        SqlState = sqlState;
        _secondaryCodes = new string[SecondaryCapacity];
        _secondaryCodeView = new SecondaryCodeView(this);
    }

    internal static TransferV3Phase4Exception Create(
        Exception raw,
        TransferV3Phase4Boundary boundary)
    {
        var code = boundary switch
        {
            TransferV3Phase4Boundary.Argument => "phase4-argument",
            TransferV3Phase4Boundary.Parser => "phase4-parser",
            TransferV3Phase4Boundary.Codec => "phase4-codec",
            TransferV3Phase4Boundary.PostgreSqlOpen => "phase4-postgresql-open",
            TransferV3Phase4Boundary.PostgreSqlCommand => "phase4-postgresql-command",
            TransferV3Phase4Boundary.PostgreSqlCopy => "phase4-postgresql-copy",
            TransferV3Phase4Boundary.PostgreSqlCommit => "phase4-postgresql-commit",
            TransferV3Phase4Boundary.Posix => "phase4-posix",
            TransferV3Phase4Boundary.Cleanup => "phase4-cleanup",
            _ => "phase4-unexpected",
        };

        return new TransferV3Phase4Exception(
            code,
            GetSafeSqlState(raw, boundary));
    }

    internal string Code { get; }

    internal string? SqlState { get; }

    internal IReadOnlyList<string> SecondaryCodes => _secondaryCodeView;

    internal bool TryAddSecondary(TransferV3Phase4SecondaryCode code)
    {
        var literal = code switch
        {
            TransferV3Phase4SecondaryCode.ObserverAbortFailed =>
                "observer-abort-failed",
            TransferV3Phase4SecondaryCode.CopyCancelFailed =>
                "copy-cancel-failed",
            TransferV3Phase4SecondaryCode.TransactionRollbackFailed =>
                "transaction-rollback-failed",
            TransferV3Phase4SecondaryCode.SpoolResidue =>
                "spool-residue",
            TransferV3Phase4SecondaryCode.BlobStageResidue =>
                "blob-stage-residue",
            TransferV3Phase4SecondaryCode.FailedStateCasZeroRows =>
                "failed-state-cas-zero-rows",
            TransferV3Phase4SecondaryCode.FailedStateCasUnknown =>
                "failed-state-cas-unknown",
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed =>
                "connection-close-failed",
            TransferV3Phase4SecondaryCode.DataSourceDisposeFailed =>
                "data-source-dispose-failed",
            TransferV3Phase4SecondaryCode.SourceReadCloseFailed =>
                "source-read-close-failed",
            TransferV3Phase4SecondaryCode.DeadlineAbandonedProviderTask =>
                "deadline-abandoned-provider-task",
            TransferV3Phase4SecondaryCode.CleanupDeadlineExceeded =>
                "cleanup-deadline-exceeded",
            TransferV3Phase4SecondaryCode.CommitOutcomeUnknown =>
                "commit-outcome-unknown",
            _ => null,
        };

        if (literal is null)
            return false;

        var count = _secondaryCodeCount;
        if ((uint)count > SecondaryCapacity)
            return false;

        for (var index = 0; index < count; index++)
        {
            if (string.Equals(_secondaryCodes[index], literal, StringComparison.Ordinal))
                return false;
        }

        if (count == SecondaryCapacity)
            return false;

        _secondaryCodes[count] = literal;
        _secondaryCodeCount = count + 1;
        return true;
    }

    private static string? GetSafeSqlState(
        Exception raw,
        TransferV3Phase4Boundary boundary)
    {
        if (!IsPostgreSqlBoundary(boundary) || raw is not PostgresException postgres)
            return null;

        var sqlState = postgres.SqlState;
        if (sqlState.Length != 5)
            return null;

        foreach (var character in sqlState)
        {
            if ((character < '0' || character > '9')
                && (character < 'A' || character > 'Z'))
            {
                return null;
            }
        }

        return sqlState;
    }

    private static bool IsPostgreSqlBoundary(TransferV3Phase4Boundary boundary) =>
        boundary is TransferV3Phase4Boundary.PostgreSqlOpen
            or TransferV3Phase4Boundary.PostgreSqlCommand
            or TransferV3Phase4Boundary.PostgreSqlCopy
            or TransferV3Phase4Boundary.PostgreSqlCommit;

    private sealed class SecondaryCodeView : IReadOnlyList<string>
    {
        private readonly TransferV3Phase4Exception _owner;

        internal SecondaryCodeView(TransferV3Phase4Exception owner) =>
            _owner = owner;

        public int Count => _owner._secondaryCodeCount;

        public string this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                return _owner._secondaryCodes[index];
            }
        }

        public IEnumerator<string> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return _owner._secondaryCodes[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal readonly record struct TransferV3Phase4CleanupResult(
    TransferV3Phase4SecondaryCode First,
    TransferV3Phase4SecondaryCode Second,
    TransferV3Phase4SecondaryCode Third,
    TransferV3Phase4SecondaryCode Fourth);

internal static class TransferV3Phase4FailureMapper
{
    private const string FixedCancellationMessage =
        "Transfer-v3 Phase 4 was canceled.";

    internal static Exception Sanitize(
        Exception raw,
        TransferV3Phase4Boundary boundary,
        CancellationToken callerToken)
    {
        if (raw is TransferV3Phase4Exception sanitized)
            return sanitized;

        if (callerToken.CanBeCanceled
            && callerToken.IsCancellationRequested
            && raw is OperationCanceledException cancellation
            && cancellation.CancellationToken == callerToken)
        {
            return new OperationCanceledException(FixedCancellationMessage, callerToken);
        }

        return TransferV3Phase4Exception.Create(raw, boundary);
    }
}
