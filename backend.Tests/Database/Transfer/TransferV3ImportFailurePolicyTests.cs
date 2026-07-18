using System.Diagnostics;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer;

public sealed class TransferV3ImportFailurePolicyTests
{
    private const string DigestA =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string DigestB =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task SuccessfulFailedStateCasStillRethrowsIdenticalPrimaryWithoutSecondaryDiagnostic()
    {
        await using var database =
            await TransferV3ImportStateStoreTests.OwnedSqliteStateDatabase.CreateAsync();
        await database.SetTextValueAsync(TransferV3ImportStateCodec.Serialize(
            TransferV3ImportState.Importing(DigestA)));
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);
        var primary = CapturePrimaryFailure();
        var callbackCount = 0;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3ImportFailurePolicy.RethrowPrimaryAsync(
                primary,
                async () =>
                {
                    callbackCount++;
                    return await store.TryTransitionAsync(
                               TransferV3ImportState.Importing(DigestA),
                               TransferV3ImportState.Failed(DigestA),
                               CancellationToken.None) == 1;
                }));

        Assert.Same(primary, thrown);
        Assert.Equal(1, callbackCount);
        Assert.False(primary.Data.Contains(TransferV3ImportFailurePolicy.SecondaryFailureDataKey));
        Assert.Contains(nameof(CapturePrimaryFailure), thrown.StackTrace, StringComparison.Ordinal);
        Assert.Equal(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Failed(DigestA)),
            await database.ReadValueBytesAsync());
        Assert.DoesNotContain(DigestA, thrown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroRowFailedStateCasLeavesImportingAndAttachesOnlyStableDiagnostic()
    {
        await using var database =
            await TransferV3ImportStateStoreTests.OwnedSqliteStateDatabase.CreateAsync();
        var importing = TransferV3ImportStateCodec.Serialize(
            TransferV3ImportState.Importing(DigestA));
        await database.SetTextValueAsync(importing);
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);
        var primary = CapturePrimaryFailure();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3ImportFailurePolicy.RethrowPrimaryAsync(
                primary,
                async () =>
                    await store.TryTransitionAsync(
                        TransferV3ImportState.Importing(DigestB),
                        TransferV3ImportState.Failed(DigestB),
                        CancellationToken.None) == 1));

        Assert.Same(primary, thrown);
        Assert.Equal(
            TransferV3ImportFailurePolicy.ZeroRowsDiagnostic,
            primary.Data[TransferV3ImportFailurePolicy.SecondaryFailureDataKey]);
        Assert.Equal(importing, await database.ReadValueBytesAsync());
        Assert.DoesNotContain(DigestA, thrown.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(DigestB, thrown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallbackFailureBeforeCasLeavesImportingAndAttachesOnlyStableDiagnostic()
    {
        await using var database =
            await TransferV3ImportStateStoreTests.OwnedSqliteStateDatabase.CreateAsync();
        var importing = TransferV3ImportStateCodec.Serialize(
            TransferV3ImportState.Importing(DigestA));
        await database.SetTextValueAsync(importing);
        var primary = CapturePrimaryFailure();
        var secondary = new InvalidOperationException(
            $"failed-state CAS unavailable for {DigestA} at /tmp/private-state.sqlite");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3ImportFailurePolicy.RethrowPrimaryAsync(
                primary,
                () => Task.FromException<bool>(secondary)));

        Assert.Same(primary, thrown);
        Assert.Equal(
            TransferV3ImportFailurePolicy.CallbackFailureDiagnostic,
            primary.Data[TransferV3ImportFailurePolicy.SecondaryFailureDataKey]);
        Assert.Equal(importing, await database.ReadValueBytesAsync());
        Assert.DoesNotContain(DigestA, thrown.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secondary.Message, primary.Data.Values.Cast<object>().Select(x => x?.ToString()));
    }

    [Fact]
    public async Task AcknowledgementLossAfterSuccessfulCasLeavesFailedAndPrimaryStillWins()
    {
        await using var database =
            await TransferV3ImportStateStoreTests.OwnedSqliteStateDatabase.CreateAsync();
        await database.SetTextValueAsync(TransferV3ImportStateCodec.Serialize(
            TransferV3ImportState.Importing(DigestA)));
        await using var context = database.CreateContext();
        var store = new TransferV3ImportStateStore(context);
        var primary = CapturePrimaryFailure();
        var acknowledgementLoss = new IOException(
            $"failed-state acknowledgement unavailable for {DigestA}; Password=private");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransferV3ImportFailurePolicy.RethrowPrimaryAsync(
                primary,
                async () =>
                {
                    var changedRows = await store.TryTransitionAsync(
                        TransferV3ImportState.Importing(DigestA),
                        TransferV3ImportState.Failed(DigestA),
                        CancellationToken.None);
                    Assert.Equal(1, changedRows);
                    throw acknowledgementLoss;
                }));

        Assert.Same(primary, thrown);
        Assert.Equal(
            TransferV3ImportFailurePolicy.CallbackFailureDiagnostic,
            primary.Data[TransferV3ImportFailurePolicy.SecondaryFailureDataKey]);
        Assert.Equal(
            TransferV3ImportStateCodec.Serialize(TransferV3ImportState.Failed(DigestA)),
            await database.ReadValueBytesAsync());
        Assert.DoesNotContain(DigestA, thrown.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            acknowledgementLoss.Message,
            primary.Data.Values.Cast<object>().Select(x => x?.ToString()));
    }

    private static InvalidOperationException CapturePrimaryFailure()
    {
        try
        {
            ThrowPrimaryFailure();
            throw new UnreachableException();
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }

    private static void ThrowPrimaryFailure() =>
        throw new InvalidOperationException("primary import failure");
}
