using NzbWebDAV.Utils;

namespace backend.Tests.Utils;

public sealed class BackgroundServiceCancellationUtilTests
{
    [Fact]
    public void IsExpectedCancellationReturnsTrueWhenStoppingTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var isExpected = BackgroundServiceCancellationUtil.IsExpectedCancellation(
            new OperationCanceledException(cts.Token),
            cts.Token);

        Assert.True(isExpected);
    }

    [Fact]
    public void IsExpectedCancellationReturnsFalseWhenStoppingTokenIsNotCancelled()
    {
        using var cts = new CancellationTokenSource();

        var isExpected = BackgroundServiceCancellationUtil.IsExpectedCancellation(
            new OperationCanceledException(),
            cts.Token);

        Assert.False(isExpected);
    }

    [Fact]
    public void IsExpectedCancellationReturnsFalseForNonCancellationExceptions()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var isExpected = BackgroundServiceCancellationUtil.IsExpectedCancellation(
            new InvalidOperationException("boom"),
            cts.Token);

        Assert.False(isExpected);
    }
}
