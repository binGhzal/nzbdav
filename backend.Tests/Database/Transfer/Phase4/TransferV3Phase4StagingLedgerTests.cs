using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

public sealed class TransferV3Phase4StagingLedgerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsANonpositiveCeiling(long maximumBytes)
    {
        AssertCode("phase4-argument", () =>
            new TransferV3Phase4StagingLedger(maximumBytes));
    }

    [Fact]
    public void Debit_ChargesLogicalBytesPlusTheExactEntryReservation()
    {
        var ledger = new TransferV3Phase4StagingLedger(10_000);
        var scope = ledger.BeginScope();

        scope.Debit(logicalBytes: 100, entries: 2);
        scope.Debit(logicalBytes: 25, entries: 0);
        scope.Debit(logicalBytes: 0, entries: 1);

        Assert.Equal(512, TransferV3Phase4StagingLedger.EntryReservationBytes);
        Assert.Equal(125 + (3 * 512), scope.CurrentBytes);
        Assert.Equal(125, scope.CurrentLogicalBytes);
        Assert.Equal(3, scope.CurrentEntries);
        Assert.Equal(scope.CurrentBytes, ledger.CurrentBytes);
        Assert.Equal(scope.CurrentBytes, ledger.PeakBytes);
        Assert.Equal(scope.CurrentLogicalBytes, ledger.CurrentLogicalBytes);
        Assert.Equal(scope.CurrentEntries, ledger.CurrentEntries);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, -1)]
    [InlineData(0, 0)]
    public void Debit_RejectsInvalidInputsWithoutChangingState(long logicalBytes, int entries)
    {
        var ledger = new TransferV3Phase4StagingLedger(10_000);
        var scope = ledger.BeginScope();

        AssertCode("phase4-argument", () => scope.Debit(logicalBytes, entries));

        Assert.Equal(0, ledger.CurrentBytes);
        Assert.Equal(0, ledger.PeakBytes);
        Assert.Equal(0, ledger.CurrentLogicalBytes);
        Assert.Equal(0, ledger.CurrentEntries);
    }

    [Fact]
    public void CeilingRefusal_IsAnArgumentFailureAndDoesNotPartiallyDebit()
    {
        var ledger = new TransferV3Phase4StagingLedger(1024);
        var scope = ledger.BeginScope();
        scope.Debit(1, 1);
        var current = ledger.CurrentBytes;

        AssertCode("phase4-argument", () => scope.Debit(512, 1));

        Assert.Equal(current, ledger.CurrentBytes);
        Assert.Equal(current, ledger.PeakBytes);
        Assert.Equal(1, ledger.CurrentLogicalBytes);
        Assert.Equal(1, ledger.CurrentEntries);
    }

    [Fact]
    public void ArithmeticOverflow_IsUnexpectedAndDoesNotMutateTheLedger()
    {
        var ledger = new TransferV3Phase4StagingLedger(long.MaxValue);
        var scope = ledger.BeginScope();

        AssertCode("phase4-unexpected", () => scope.Debit(long.MaxValue, 1));

        Assert.Equal(0, ledger.CurrentBytes);
        Assert.Equal(0, ledger.PeakBytes);
        Assert.Equal(0, ledger.CurrentLogicalBytes);
        Assert.Equal(0, ledger.CurrentEntries);
    }

    [Fact]
    public void ExactlyOneActiveScopePreventsStaleAndAbaRelease()
    {
        var ledger = new TransferV3Phase4StagingLedger(10_000);
        var first = ledger.BeginScope();
        first.Debit(100, 1);
        AssertCode("phase4-unexpected", () => ledger.BeginScope());

        first.ReleaseAllAfterProvenRemoval();

        Assert.Equal(0, ledger.CurrentBytes);
        Assert.Equal(100 + 512, ledger.PeakBytes);
        AssertCode("phase4-unexpected", first.ReleaseAllAfterProvenRemoval);
        AssertCode("phase4-unexpected", () => first.Debit(1, 0));
        AssertCode("phase4-unexpected", () => _ = first.CurrentBytes);

        var second = ledger.BeginScope();
        second.Debit(200, 2);
        var secondCharge = 200 + (2 * 512);
        Assert.Equal(secondCharge, ledger.CurrentBytes);
        AssertCode("phase4-unexpected", first.ReleaseAllAfterProvenRemoval);
        Assert.Equal(secondCharge, ledger.CurrentBytes);
        second.ReleaseAllAfterProvenRemoval();
        Assert.Equal(0, ledger.CurrentBytes);
    }

    [Fact]
    public void EmptyScope_CanReleaseAfterAbsenceIsProvenWithoutChangingPeak()
    {
        var ledger = new TransferV3Phase4StagingLedger(1024);
        var scope = ledger.BeginScope();

        scope.ReleaseAllAfterProvenRemoval();

        Assert.Equal(0, ledger.CurrentBytes);
        Assert.Equal(0, ledger.PeakBytes);
        var next = ledger.BeginScope();
        next.Debit(1, 0);
        Assert.Equal(1, ledger.CurrentBytes);
        next.ReleaseAllAfterProvenRemoval();
    }

    [Fact]
    public async Task ConcurrentDebits_AreThreadSafeAndPeakIsMonotonic()
    {
        const int count = 1000;
        var ledger = new TransferV3Phase4StagingLedger(2_000_000);
        var scope = ledger.BeginScope();

        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(() =>
            scope.Debit(logicalBytes: 3, entries: 1))));

        var expected = (3L + TransferV3Phase4StagingLedger.EntryReservationBytes) * count;
        Assert.Equal(expected, ledger.CurrentBytes);
        Assert.Equal(expected, ledger.PeakBytes);
        Assert.Equal(3L * count, ledger.CurrentLogicalBytes);
        Assert.Equal(count, ledger.CurrentEntries);
        scope.ReleaseAllAfterProvenRemoval();
        Assert.Equal(0, ledger.CurrentBytes);
        Assert.Equal(expected, ledger.PeakBytes);
    }

    [Fact]
    public async Task ConcurrentReleaseAndStaleUse_CannotAffectTheNextScope()
    {
        var ledger = new TransferV3Phase4StagingLedger(100_000);
        var first = ledger.BeginScope();
        first.Debit(1, 1);
        using var start = new ManualResetEventSlim();
        var releaseTasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return Record.Exception(first.ReleaseAllAfterProvenRemoval);
        })).ToArray();
        start.Set();
        var outcomes = await Task.WhenAll(releaseTasks);

        Assert.Single(outcomes, outcome => outcome is null);
        Assert.All(
            outcomes.Where(outcome => outcome is not null),
            outcome => Assert.Equal(
                "phase4-unexpected",
                Assert.IsType<TransferV3Phase4Exception>(outcome).Code));

        var second = ledger.BeginScope();
        second.Debit(10, 1);
        var expected = 10 + TransferV3Phase4StagingLedger.EntryReservationBytes;
        Assert.Equal(expected, ledger.CurrentBytes);
        AssertCode("phase4-unexpected", first.ReleaseAllAfterProvenRemoval);
        Assert.Equal(expected, ledger.CurrentBytes);
        second.ReleaseAllAfterProvenRemoval();
    }

    private static void AssertCode(string expected, Action action)
    {
        var failure = Assert.IsType<TransferV3Phase4Exception>(Record.Exception(action));
        Assert.Equal(expected, failure.Code);
        Assert.Equal("Transfer-v3 Phase 4 failed.", failure.Message);
    }
}
