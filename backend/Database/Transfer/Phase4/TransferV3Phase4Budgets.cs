using System.Security.Cryptography;

namespace NzbWebDAV.Database.Transfer;

internal enum TransferV3Phase4MemoryKind
{
    RuntimeReserve,
    Manifest,
    Parser,
    Row,
    Field,
    Copy,
    Digest,
    Receipt,
    DirectoryEnumeration,
    Cleanup,
}

internal sealed class TransferV3Phase4MemoryLease : IDisposable
{
    private readonly TransferV3Phase4ManagedBudget _owner;
    private long _managedElementStorageBytes;
    private bool _disposed;

    internal TransferV3Phase4MemoryLease(
        TransferV3Phase4ManagedBudget owner,
        long capacityBytes,
        TransferV3Phase4MemoryKind kind)
    {
        _owner = owner;
        CapacityBytes = capacityBytes;
        Kind = kind;
    }

    internal long CapacityBytes { get; }

    internal TransferV3Phase4MemoryKind Kind { get; }

    internal TransferV3Phase4ManagedBudget Owner => _owner;

    internal long ManagedElementStorageBytes => _managedElementStorageBytes;

    internal bool IsDisposed => _disposed;

    internal void MarkManagedElementStorageAllocated(long elementStorageBytes) =>
        _owner.MarkManagedElementStorageAllocated(this, elementStorageBytes);

    internal void SetManagedElementStorageBytes(long elementStorageBytes) =>
        _managedElementStorageBytes = elementStorageBytes;

    internal void SetDisposed() => _disposed = true;

    public void Dispose() => _owner.Release(this);
}

internal sealed class TransferV3Phase4ManagedBudget
{
    internal const long LimitBytes = 32L * 1024 * 1024;
    internal const long RuntimeReserveBytes = 8L * 1024 * 1024;
    internal const int RowReservationBytes = 256;
    internal const int FieldReservationBytes = 64;

    private readonly object _gate = new();
    private long _currentBytes = RuntimeReserveBytes;
    private long _peakBytes = RuntimeReserveBytes;
    private long _currentAllocatedManagedElementStorageBytes;
    private long _cumulativeAllocatedManagedElementStorageBytes;

    internal TransferV3Phase4MemoryLease Reserve(
        long capacityBytes,
        TransferV3Phase4MemoryKind kind)
    {
        ValidateReservation(capacityBytes, kind);
        if (!TryReserveCore(capacityBytes, kind, out var lease))
            throw UnexpectedFailure();

        return lease!;
    }

    internal bool TryReserve(
        long capacityBytes,
        TransferV3Phase4MemoryKind kind,
        out TransferV3Phase4MemoryLease? lease)
    {
        ValidateReservation(capacityBytes, kind);
        return TryReserveCore(capacityBytes, kind, out lease);
    }

    internal TransferV3Phase4MemoryLease ReserveCharacters(
        int capacityChars,
        TransferV3Phase4MemoryKind kind)
    {
        if (capacityChars <= 0)
            throw ArgumentFailure();

        long capacityBytes;
        try
        {
            capacityBytes = checked((long)capacityChars * sizeof(char));
        }
        catch (OverflowException exception)
        {
            throw UnexpectedFailure(exception);
        }

        return Reserve(capacityBytes, kind);
    }

    internal bool TryReserveCharacters(
        int capacityChars,
        TransferV3Phase4MemoryKind kind,
        out TransferV3Phase4MemoryLease? lease)
    {
        if (capacityChars <= 0)
            throw ArgumentFailure();

        long capacityBytes;
        try
        {
            capacityBytes = checked((long)capacityChars * sizeof(char));
        }
        catch (OverflowException exception)
        {
            throw UnexpectedFailure(exception);
        }

        return TryReserve(capacityBytes, kind, out lease);
    }

    internal long CurrentBytes
    {
        get
        {
            lock (_gate)
            {
                return _currentBytes;
            }
        }
    }

    internal long PeakBytes
    {
        get
        {
            lock (_gate)
            {
                return _peakBytes;
            }
        }
    }

    internal long AvailableBytes
    {
        get
        {
            lock (_gate)
            {
                return LimitBytes - _currentBytes;
            }
        }
    }

    internal long CurrentAllocatedManagedElementStorageBytes
    {
        get
        {
            lock (_gate)
            {
                return _currentAllocatedManagedElementStorageBytes;
            }
        }
    }

    internal long CumulativeAllocatedManagedElementStorageBytes
    {
        get
        {
            lock (_gate)
            {
                return _cumulativeAllocatedManagedElementStorageBytes;
            }
        }
    }

    internal void MarkManagedElementStorageAllocated(
        TransferV3Phase4MemoryLease lease,
        long elementStorageBytes)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(lease.Owner, this)
                || lease.IsDisposed
                || lease.ManagedElementStorageBytes != 0
                || elementStorageBytes <= 0
                || elementStorageBytes > lease.CapacityBytes)
            {
                throw UnexpectedFailure();
            }

            long nextCurrent;
            long nextCumulative;
            try
            {
                nextCurrent = checked(
                    _currentAllocatedManagedElementStorageBytes + elementStorageBytes);
                nextCumulative = checked(
                    _cumulativeAllocatedManagedElementStorageBytes + elementStorageBytes);
            }
            catch (OverflowException exception)
            {
                throw UnexpectedFailure(exception);
            }

            if (nextCurrent > _currentBytes - RuntimeReserveBytes)
                throw UnexpectedFailure();

            _currentAllocatedManagedElementStorageBytes = nextCurrent;
            _cumulativeAllocatedManagedElementStorageBytes = nextCumulative;
            lease.SetManagedElementStorageBytes(elementStorageBytes);
        }
    }

    internal void Release(TransferV3Phase4MemoryLease lease)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(lease.Owner, this))
                throw UnexpectedFailure();

            if (lease.IsDisposed)
                return;

            long nextCurrentBytes;
            long nextAllocatedManagedElementStorageBytes;
            try
            {
                nextCurrentBytes = checked(_currentBytes - lease.CapacityBytes);
                nextAllocatedManagedElementStorageBytes = checked(
                    _currentAllocatedManagedElementStorageBytes
                    - lease.ManagedElementStorageBytes);
            }
            catch (OverflowException exception)
            {
                throw UnexpectedFailure(exception);
            }
            if (nextCurrentBytes < RuntimeReserveBytes
                || nextAllocatedManagedElementStorageBytes < 0)
            {
                throw UnexpectedFailure();
            }

            _currentBytes = nextCurrentBytes;
            _currentAllocatedManagedElementStorageBytes =
                nextAllocatedManagedElementStorageBytes;
            lease.SetDisposed();
        }
    }

    private bool TryReserveCore(
        long capacityBytes,
        TransferV3Phase4MemoryKind kind,
        out TransferV3Phase4MemoryLease? lease)
    {
        lock (_gate)
        {
            long nextCurrent;
            try
            {
                nextCurrent = checked(_currentBytes + capacityBytes);
            }
            catch (OverflowException exception)
            {
                throw UnexpectedFailure(exception);
            }

            if (nextCurrent > LimitBytes)
            {
                lease = null;
                return false;
            }

            _currentBytes = nextCurrent;
            if (nextCurrent > _peakBytes)
                _peakBytes = nextCurrent;

            try
            {
                lease = new TransferV3Phase4MemoryLease(this, capacityBytes, kind);
                return true;
            }
            catch (Exception exception)
            {
                _currentBytes -= capacityBytes;
                lease = null;
                if (exception is TransferV3Phase4Exception)
                    throw;

                throw UnexpectedFailure(exception);
            }
        }
    }

    private static void ValidateReservation(
        long capacityBytes,
        TransferV3Phase4MemoryKind kind)
    {
        if (capacityBytes <= 0
            || kind <= TransferV3Phase4MemoryKind.RuntimeReserve
            || kind > TransferV3Phase4MemoryKind.Cleanup)
        {
            throw ArgumentFailure();
        }
    }

    private static TransferV3Phase4Exception ArgumentFailure() =>
        TransferV3Phase4Exception.Create(
            new ArgumentException(),
            TransferV3Phase4Boundary.Argument);

    private static TransferV3Phase4Exception UnexpectedFailure() =>
        UnexpectedFailure(new InvalidOperationException());

    private static TransferV3Phase4Exception UnexpectedFailure(Exception raw) =>
        TransferV3Phase4Exception.Create(raw, TransferV3Phase4Boundary.Unexpected);
}

internal sealed class TransferV3Phase4Digest : IDisposable
{
    internal const int SizeBytes = 32;
    private const int LowerHexSizeBytes = SizeBytes * 2;

    private byte[]? _bytes;
    private TransferV3Phase4MemoryLease? _lease;

    private TransferV3Phase4Digest(
        byte[] bytes,
        TransferV3Phase4MemoryLease lease)
    {
        _bytes = bytes;
        _lease = lease;
    }

    internal static TransferV3Phase4Digest Create(
        TransferV3Phase4ManagedBudget managedBudget,
        ReadOnlySpan<byte> sha256)
    {
        if (managedBudget is null || sha256.Length != SizeBytes)
            throw ArgumentFailure();

        var lease = managedBudget.Reserve(SizeBytes, TransferV3Phase4MemoryKind.Digest);
        byte[]? bytes = null;
        try
        {
            bytes = new byte[SizeBytes];
            lease.MarkManagedElementStorageAllocated(SizeBytes);
            sha256.CopyTo(bytes);
            return new TransferV3Phase4Digest(bytes, lease);
        }
        catch (Exception exception)
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
            lease.Dispose();

            if (exception is TransferV3Phase4Exception)
                throw;

            throw TransferV3Phase4Exception.Create(
                exception,
                TransferV3Phase4Boundary.Unexpected);
        }
    }

    internal ReadOnlySpan<byte> Bytes
    {
        get
        {
            var bytes = _bytes;
            if (bytes is null)
                throw ArgumentFailure();
            return bytes;
        }
    }

    internal void ValidateOwner(TransferV3Phase4ManagedBudget managedBudget)
    {
        var lease = _lease;
        if (managedBudget is null
            || _bytes is null
            || lease is null
            || lease.IsDisposed
            || !ReferenceEquals(lease.Owner, managedBudget))
        {
            throw ArgumentFailure();
        }
    }

    internal void CopyLowerHexTo(Span<byte> destination)
    {
        var bytes = _bytes;
        if (bytes is null || destination.Length != LowerHexSizeBytes)
            throw ArgumentFailure();

        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            destination[index * 2] = LowerHexDigit(value >> 4);
            destination[(index * 2) + 1] = LowerHexDigit(value & 0x0f);
        }
    }

    public void Dispose()
    {
        var bytes = _bytes;
        if (bytes is null)
            return;

        CryptographicOperations.ZeroMemory(bytes);
        _bytes = null;
        var lease = _lease;
        _lease = null;
        lease!.Dispose();
    }

    private static byte LowerHexDigit(int value) =>
        checked((byte)(value < 10 ? (byte)'0' + value : (byte)'a' + value - 10));

    private static TransferV3Phase4Exception ArgumentFailure() =>
        TransferV3Phase4Exception.Create(
            new ArgumentException(),
            TransferV3Phase4Boundary.Argument);
}

internal sealed class TransferV3Phase4StagingLedger
{
    internal const int EntryReservationBytes = 512;

    private readonly object _gate = new();
    private readonly long _maximumBytes;
    private TransferV3Phase4StagingScope? _activeScope;
    private long _currentBytes;
    private long _peakBytes;
    private long _currentLogicalBytes;
    private long _currentEntries;

    internal TransferV3Phase4StagingLedger(long maximumBytes)
    {
        if (maximumBytes <= 0)
            throw ArgumentFailure();

        _maximumBytes = maximumBytes;
    }

    internal TransferV3Phase4StagingScope BeginScope()
    {
        lock (_gate)
        {
            if (_activeScope is not null)
                throw UnexpectedFailure();

            try
            {
                var scope = new TransferV3Phase4StagingScope(this);
                _activeScope = scope;
                return scope;
            }
            catch (Exception exception)
            {
                if (exception is TransferV3Phase4Exception)
                    throw;

                throw UnexpectedFailure(exception);
            }
        }
    }

    internal long CurrentBytes
    {
        get
        {
            lock (_gate)
            {
                return _currentBytes;
            }
        }
    }

    internal long PeakBytes
    {
        get
        {
            lock (_gate)
            {
                return _peakBytes;
            }
        }
    }

    internal long CurrentLogicalBytes
    {
        get
        {
            lock (_gate)
            {
                return _currentLogicalBytes;
            }
        }
    }

    internal long CurrentEntries
    {
        get
        {
            lock (_gate)
            {
                return _currentEntries;
            }
        }
    }

    internal void Debit(
        TransferV3Phase4StagingScope scope,
        long logicalBytes,
        int entries)
    {
        lock (_gate)
        {
            EnsureActive(scope);
            if (logicalBytes < 0 || entries < 0 || (logicalBytes == 0 && entries == 0))
                throw ArgumentFailure();

            long nextCurrentBytes;
            long nextLogicalBytes;
            long nextEntries;
            try
            {
                var entryBytes = checked((long)EntryReservationBytes * entries);
                var debitBytes = checked(logicalBytes + entryBytes);
                nextCurrentBytes = checked(_currentBytes + debitBytes);
                nextLogicalBytes = checked(_currentLogicalBytes + logicalBytes);
                nextEntries = checked(_currentEntries + entries);
            }
            catch (OverflowException exception)
            {
                throw UnexpectedFailure(exception);
            }

            if (nextCurrentBytes > _maximumBytes)
                throw ArgumentFailure();

            _currentBytes = nextCurrentBytes;
            _currentLogicalBytes = nextLogicalBytes;
            _currentEntries = nextEntries;
            if (nextCurrentBytes > _peakBytes)
                _peakBytes = nextCurrentBytes;
        }
    }

    internal long GetScopeCurrentBytes(TransferV3Phase4StagingScope scope)
    {
        lock (_gate)
        {
            EnsureActive(scope);
            return _currentBytes;
        }
    }

    internal long GetScopeCurrentLogicalBytes(TransferV3Phase4StagingScope scope)
    {
        lock (_gate)
        {
            EnsureActive(scope);
            return _currentLogicalBytes;
        }
    }

    internal long GetScopeCurrentEntries(TransferV3Phase4StagingScope scope)
    {
        lock (_gate)
        {
            EnsureActive(scope);
            return _currentEntries;
        }
    }

    internal void ReleaseAllAfterProvenRemoval(TransferV3Phase4StagingScope scope)
    {
        lock (_gate)
        {
            EnsureActive(scope);
            _currentBytes = 0;
            _currentLogicalBytes = 0;
            _currentEntries = 0;
            _activeScope = null;
        }
    }

    private void EnsureActive(TransferV3Phase4StagingScope scope)
    {
        if (!ReferenceEquals(_activeScope, scope))
            throw UnexpectedFailure();
    }

    private static TransferV3Phase4Exception ArgumentFailure() =>
        TransferV3Phase4Exception.Create(
            new ArgumentException(),
            TransferV3Phase4Boundary.Argument);

    private static TransferV3Phase4Exception UnexpectedFailure() =>
        UnexpectedFailure(new InvalidOperationException());

    private static TransferV3Phase4Exception UnexpectedFailure(Exception raw) =>
        TransferV3Phase4Exception.Create(raw, TransferV3Phase4Boundary.Unexpected);
}

internal sealed class TransferV3Phase4StagingScope
{
    private readonly TransferV3Phase4StagingLedger _owner;

    internal TransferV3Phase4StagingScope(TransferV3Phase4StagingLedger owner) =>
        _owner = owner;

    internal void Debit(long logicalBytes, int entries) =>
        _owner.Debit(this, logicalBytes, entries);

    internal long CurrentBytes => _owner.GetScopeCurrentBytes(this);

    internal long CurrentLogicalBytes => _owner.GetScopeCurrentLogicalBytes(this);

    internal long CurrentEntries => _owner.GetScopeCurrentEntries(this);

    internal void ReleaseAllAfterProvenRemoval() =>
        _owner.ReleaseAllAfterProvenRemoval(this);
}
