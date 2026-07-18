using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

public sealed class TransferV3Phase4ManagedBudgetTests
{
    [Fact]
    public void Construction_ChargesOnlyTheFixedRuntimeReserve()
    {
        var budget = new TransferV3Phase4ManagedBudget();

        Assert.Equal(32L * 1024 * 1024, TransferV3Phase4ManagedBudget.LimitBytes);
        Assert.Equal(8L * 1024 * 1024, TransferV3Phase4ManagedBudget.RuntimeReserveBytes);
        Assert.Equal(256, TransferV3Phase4ManagedBudget.RowReservationBytes);
        Assert.Equal(64, TransferV3Phase4ManagedBudget.FieldReservationBytes);
        Assert.Equal(8L * 1024 * 1024, budget.CurrentBytes);
        Assert.Equal(8L * 1024 * 1024, budget.PeakBytes);
        Assert.Equal(24L * 1024 * 1024, budget.AvailableBytes);
        Assert.Equal(0, budget.CurrentAllocatedManagedElementStorageBytes);
        Assert.Equal(0, budget.CumulativeAllocatedManagedElementStorageBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reservations_RejectNonpositiveCapacityWithTheFixedArgumentCode(long capacity)
    {
        var budget = new TransferV3Phase4ManagedBudget();

        AssertCode("phase4-argument", () => budget.Reserve(
            capacity,
            TransferV3Phase4MemoryKind.Parser));
        AssertCode("phase4-argument", () => budget.TryReserve(
            capacity,
            TransferV3Phase4MemoryKind.Parser,
            out _));
    }

    [Fact]
    public void Reservations_RejectInvalidAndSyntheticRuntimeKinds()
    {
        var budget = new TransferV3Phase4ManagedBudget();

        AssertCode("phase4-argument", () => budget.Reserve(
            1,
            TransferV3Phase4MemoryKind.RuntimeReserve));
        AssertCode("phase4-argument", () => budget.Reserve(
            1,
            (TransferV3Phase4MemoryKind)int.MaxValue));
        AssertCode("phase4-argument", () => budget.TryReserve(
            1,
            TransferV3Phase4MemoryKind.RuntimeReserve,
            out _));
        AssertCode("phase4-argument", () => budget.TryReserve(
            1,
            (TransferV3Phase4MemoryKind)(-1),
            out _));
    }

    [Fact]
    public void CharacterReservations_ChargeExactlyTwoBytesPerCharacter()
    {
        var budget = new TransferV3Phase4ManagedBudget();

        using var lease = budget.ReserveCharacters(
            257,
            TransferV3Phase4MemoryKind.Field);

        Assert.Equal(514, lease.CapacityBytes);
        Assert.Equal(TransferV3Phase4MemoryKind.Field, lease.Kind);
        Assert.Equal(TransferV3Phase4ManagedBudget.RuntimeReserveBytes + 514, budget.CurrentBytes);
        AssertCode("phase4-argument", () => budget.ReserveCharacters(
            0,
            TransferV3Phase4MemoryKind.Field));
        AssertCode("phase4-argument", () => budget.TryReserveCharacters(
            -1,
            TransferV3Phase4MemoryKind.Field,
            out _));
    }

    [Fact]
    public void CeilingPressure_IsTheOnlyFalseTryReserveOutcomeAndDoesNotChangeState()
    {
        var budget = new TransferV3Phase4ManagedBudget();
        using var ceiling = budget.Reserve(
            24L * 1024 * 1024,
            TransferV3Phase4MemoryKind.Manifest);
        var current = budget.CurrentBytes;
        var peak = budget.PeakBytes;

        var accepted = budget.TryReserve(
            1,
            TransferV3Phase4MemoryKind.Parser,
            out var refused);

        Assert.False(accepted);
        Assert.Null(refused);
        Assert.Equal(TransferV3Phase4ManagedBudget.LimitBytes, current);
        Assert.Equal(current, budget.CurrentBytes);
        Assert.Equal(peak, budget.PeakBytes);
        Assert.Equal(0, budget.AvailableBytes);
        AssertCode("phase4-unexpected", () => budget.Reserve(
            1,
            TransferV3Phase4MemoryKind.Parser));

        var direct = new TransferV3Phase4ManagedBudget();
        AssertCode("phase4-unexpected", () => direct.Reserve(
            32L * 1024 * 1024,
            TransferV3Phase4MemoryKind.Manifest));
        Assert.Equal(TransferV3Phase4ManagedBudget.RuntimeReserveBytes, direct.CurrentBytes);
        Assert.Equal(TransferV3Phase4ManagedBudget.RuntimeReserveBytes, direct.PeakBytes);
    }

    [Fact]
    public void CounterOverflow_IsUnexpectedRatherThanAFalseCeilingResult()
    {
        var budget = new TransferV3Phase4ManagedBudget();

        AssertCode("phase4-unexpected", () => budget.TryReserve(
            long.MaxValue,
            TransferV3Phase4MemoryKind.Manifest,
            out _));
        AssertCode("phase4-unexpected", () => budget.Reserve(
            long.MaxValue,
            TransferV3Phase4MemoryKind.Manifest));

        Assert.Equal(TransferV3Phase4ManagedBudget.RuntimeReserveBytes, budget.CurrentBytes);
        Assert.Equal(TransferV3Phase4ManagedBudget.RuntimeReserveBytes, budget.PeakBytes);
    }

    [Fact]
    public void LeaseMarking_TracksExactManagedElementStorageAndCumulativeNeverDecreases()
    {
        var budget = new TransferV3Phase4ManagedBudget();
        var lease = budget.Reserve(128, TransferV3Phase4MemoryKind.Copy);

        lease.MarkManagedElementStorageAllocated(96);

        Assert.Equal(96, budget.CurrentAllocatedManagedElementStorageBytes);
        Assert.Equal(96, budget.CumulativeAllocatedManagedElementStorageBytes);
        AssertCode("phase4-unexpected", () =>
            lease.MarkManagedElementStorageAllocated(1));
        lease.Dispose();
        Assert.Equal(0, budget.CurrentAllocatedManagedElementStorageBytes);
        Assert.Equal(96, budget.CumulativeAllocatedManagedElementStorageBytes);
        Assert.Equal(TransferV3Phase4ManagedBudget.RuntimeReserveBytes, budget.CurrentBytes);
        AssertCode("phase4-unexpected", () =>
            lease.MarkManagedElementStorageAllocated(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(129)]
    public void LeaseMarking_RejectsInvalidElementStorageWithoutChangingCounters(long bytes)
    {
        var budget = new TransferV3Phase4ManagedBudget();
        using var lease = budget.Reserve(128, TransferV3Phase4MemoryKind.Receipt);

        AssertCode("phase4-unexpected", () =>
            lease.MarkManagedElementStorageAllocated(bytes));
        Assert.Equal(0, budget.CurrentAllocatedManagedElementStorageBytes);
        Assert.Equal(0, budget.CumulativeAllocatedManagedElementStorageBytes);
    }

    [Fact]
    public async Task ConcurrentLeaseDisposal_ReleasesExactlyOnceAndPreservesPeak()
    {
        var budget = new TransferV3Phase4ManagedBudget();
        var lease = budget.Reserve(4096, TransferV3Phase4MemoryKind.Cleanup);
        lease.MarkManagedElementStorageAllocated(4096);

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(lease.Dispose)));

        Assert.Equal(4096, lease.CapacityBytes);
        Assert.Equal(TransferV3Phase4MemoryKind.Cleanup, lease.Kind);
        Assert.Equal(TransferV3Phase4ManagedBudget.RuntimeReserveBytes, budget.CurrentBytes);
        Assert.Equal(
            TransferV3Phase4ManagedBudget.RuntimeReserveBytes + 4096,
            budget.PeakBytes);
        Assert.Equal(0, budget.CurrentAllocatedManagedElementStorageBytes);
        Assert.Equal(4096, budget.CumulativeAllocatedManagedElementStorageBytes);
    }

    [Fact]
    public async Task ConcurrentReservations_AreLinearizableAtTheExactCeiling()
    {
        var budget = new TransferV3Phase4ManagedBudget();
        var leases = new System.Collections.Concurrent.ConcurrentBag<TransferV3Phase4MemoryLease>();

        await Task.WhenAll(Enumerable.Range(0, 48).Select(_ => Task.Run(() =>
        {
            if (budget.TryReserve(
                    1024 * 1024,
                    TransferV3Phase4MemoryKind.Row,
                    out var lease))
            {
                leases.Add(Assert.IsType<TransferV3Phase4MemoryLease>(lease));
            }
        })));

        Assert.Equal(24, leases.Count);
        Assert.Equal(TransferV3Phase4ManagedBudget.LimitBytes, budget.CurrentBytes);
        Assert.Equal(TransferV3Phase4ManagedBudget.LimitBytes, budget.PeakBytes);
        foreach (var lease in leases) lease.Dispose();
        Assert.Equal(TransferV3Phase4ManagedBudget.RuntimeReserveBytes, budget.CurrentBytes);
    }

    private static void AssertCode(string expected, Action action)
    {
        var failure = Assert.IsType<TransferV3Phase4Exception>(Record.Exception(action));
        Assert.Equal(expected, failure.Code);
        Assert.Equal("Transfer-v3 Phase 4 failed.", failure.Message);
    }
}
