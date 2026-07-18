namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3Phase4Options : IDisposable
{
    private TransferV3Phase4ConsumedOptions? _consumedOwner;

    internal TransferV3Phase4Options(
        TransferV3Phase4StagingParent stagingParent,
        long maxPostgreSqlTextPayloadBytes,
        long maxPhase4StagingBytes)
    {
        if (stagingParent is null
            || maxPostgreSqlTextPayloadBytes <= 0
            || maxPhase4StagingBytes <= 0)
        {
            throw ArgumentFailure();
        }

        _consumedOwner = new TransferV3Phase4ConsumedOptions(
            stagingParent,
            maxPostgreSqlTextPayloadBytes,
            maxPhase4StagingBytes);
    }

    internal TransferV3Phase4ConsumedOptions Consume()
    {
        var owner = Interlocked.Exchange(ref _consumedOwner, null);
        if (owner is null)
            throw ArgumentFailure();

        return owner;
    }

    public void Dispose() =>
        Interlocked.Exchange(ref _consumedOwner, null)?.Dispose();

    private static TransferV3Phase4Exception ArgumentFailure() =>
        TransferV3Phase4Exception.Create(
            new ArgumentException(),
            TransferV3Phase4Boundary.Argument);
}

internal sealed class TransferV3Phase4ConsumedOptions : IDisposable
{
    private readonly long _maxPostgreSqlTextPayloadBytes;
    private readonly long _maxPhase4StagingBytes;
    private TransferV3Phase4StagingParent? _stagingParent;

    internal TransferV3Phase4ConsumedOptions(
        TransferV3Phase4StagingParent stagingParent,
        long maxPostgreSqlTextPayloadBytes,
        long maxPhase4StagingBytes)
    {
        _stagingParent = stagingParent;
        _maxPostgreSqlTextPayloadBytes = maxPostgreSqlTextPayloadBytes;
        _maxPhase4StagingBytes = maxPhase4StagingBytes;
    }

    internal TransferV3Phase4StagingParent StagingParent => GetStagingParent();

    internal long MaxPostgreSqlTextPayloadBytes
    {
        get
        {
            _ = GetStagingParent();
            return _maxPostgreSqlTextPayloadBytes;
        }
    }

    internal long MaxPhase4StagingBytes
    {
        get
        {
            _ = GetStagingParent();
            return _maxPhase4StagingBytes;
        }
    }

    public void Dispose() =>
        Interlocked.Exchange(ref _stagingParent, null)?.Dispose();

    private TransferV3Phase4StagingParent GetStagingParent()
    {
        var stagingParent = Volatile.Read(ref _stagingParent);
        if (stagingParent is null)
        {
            throw TransferV3Phase4Exception.Create(
                new ObjectDisposedException(nameof(TransferV3Phase4ConsumedOptions)),
                TransferV3Phase4Boundary.Argument);
        }

        return stagingParent;
    }
}
