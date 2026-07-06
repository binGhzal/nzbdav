using NzbWebDAV.Extensions;

namespace backend.Tests.Extensions;

public sealed class ProgressExtensionsTests
{
    [Fact]
    public void FromActionReportsDirectlyWithoutSynchronizationContextPost()
    {
        using var syncContext = new CountingSynchronizationContextScope();
        var reported = new List<int>();

        var progress = ProgressExtensions.FromAction(reported.Add);
        progress.Report(42);

        Assert.Equal([42], reported);
        Assert.Equal(0, syncContext.PostCount);
    }

    [Fact]
    public void ToPercentageTreatsZeroTotalAsSingleUnit()
    {
        using var syncContext = new InlineSynchronizationContextScope();
        var reported = -1;
        var progress = new Progress<int>(x => reported = x);

        var percentage = progress.ToPercentage(0);
        ((IProgress<int>)percentage).Report(1);

        Assert.Equal(100, reported);
    }

    [Fact]
    public void ToPercentageReportsOnlyWhenVisiblePercentageChanges()
    {
        using var syncContext = new InlineSynchronizationContextScope();
        var reported = new List<int>();
        var progress = new Progress<int>(reported.Add);

        var percentage = progress.ToPercentage(1000);
        ((IProgress<int>)percentage).Report(1);
        ((IProgress<int>)percentage).Report(2);
        ((IProgress<int>)percentage).Report(9);
        ((IProgress<int>)percentage).Report(10);
        ((IProgress<int>)percentage).Report(11);

        Assert.Equal([0, 1], reported);
    }

    [Fact]
    public void ToPercentageDoesNotPostRawSegmentReportsToSynchronizationContext()
    {
        using var syncContext = new CountingSynchronizationContextScope();
        var progress = new RecordingProgress();

        var percentage = progress.ToPercentage(1000);
        ((IProgress<int>)percentage).Report(1);
        ((IProgress<int>)percentage).Report(2);
        ((IProgress<int>)percentage).Report(9);
        ((IProgress<int>)percentage).Report(10);
        ((IProgress<int>)percentage).Report(11);

        Assert.Equal([0, 1], progress.Values);
        Assert.Equal(0, syncContext.PostCount);
    }

    [Fact]
    public void ScaleTreatsZeroDenominatorAsSingleUnit()
    {
        using var syncContext = new InlineSynchronizationContextScope();
        var reported = -1;
        var progress = new Progress<int>(x => reported = x);

        var scaled = progress.Scale(5, 0);
        ((IProgress<int>)scaled).Report(1);

        Assert.Equal(5, reported);
    }

    [Fact]
    public void ScaleDoesNotPostReportsToSynchronizationContext()
    {
        using var syncContext = new CountingSynchronizationContextScope();
        var progress = new RecordingProgress();

        var scaled = progress.Scale(5, 10);
        ((IProgress<int>)scaled).Report(4);

        Assert.Equal([2], progress.Values);
        Assert.Equal(0, syncContext.PostCount);
    }

    [Fact]
    public void OffsetDoesNotPostReportsToSynchronizationContext()
    {
        using var syncContext = new CountingSynchronizationContextScope();
        var progress = new RecordingProgress();

        var offset = progress.Offset(7);
        ((IProgress<int>)offset).Report(4);

        Assert.Equal([11], progress.Values);
        Assert.Equal(0, syncContext.PostCount);
    }

    [Fact]
    public void MultiProgressTreatsZeroTotalAsSingleSubProgress()
    {
        using var syncContext = new InlineSynchronizationContextScope();
        var reported = -1;
        var progress = new Progress<int>(x => reported = x);

        var multiProgress = progress.ToMultiProgress(0);
        ((IProgress<int>)multiProgress.SubProgress).Report(100);

        Assert.Equal(100, reported);
    }

    [Fact]
    public void MultiProgressSubProgressDoesNotPostReportsToSynchronizationContext()
    {
        using var syncContext = new CountingSynchronizationContextScope();
        var progress = new RecordingProgress();

        var multiProgress = progress.ToMultiProgress(2);
        ((IProgress<int>)multiProgress.SubProgress).Report(50);

        Assert.Equal([25], progress.Values);
        Assert.Equal(0, syncContext.PostCount);
    }

    private sealed class InlineSynchronizationContextScope : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

        public InlineSynchronizationContextScope()
        {
            SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        }

        public void Dispose()
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
        }
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            d(state);
        }
    }

    private sealed class CountingSynchronizationContextScope : IDisposable
    {
        private readonly CountingSynchronizationContext _context = new();
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;

        public int PostCount => _context.PostCount;

        public CountingSynchronizationContextScope()
        {
            SynchronizationContext.SetSynchronizationContext(_context);
        }

        public void Dispose()
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
        }
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            PostCount++;
            d(state);
        }
    }

    private sealed class RecordingProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value)
        {
            Values.Add(value);
        }
    }
}
