namespace NzbWebDAV.Extensions;

public static class ProgressExtensions
{
    public static IProgress<int> FromAction(Action<int> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new DelegateProgress(report);
    }

    public static IProgress<int> ToPercentage(this IProgress<int>? progress, int total)
    {
        var denominator = Math.Max(1, total);
        return new PercentageProgress(progress, denominator);
    }

    public static IProgress<int> Scale(this IProgress<int>? progress, int numerator, int denominator)
    {
        denominator = Math.Max(1, denominator);
        return new ScaledProgress(progress, numerator, denominator);
    }

    public static IProgress<int> Offset(this IProgress<int>? progress, int offset)
    {
        return new OffsetProgress(progress, offset);
    }

    public static MultiProgress ToMultiProgress(this IProgress<int>? progress, int total)
    {
        return new MultiProgress(progress, total);
    }

    public class MultiProgress(IProgress<int>? progress, int total)
    {
        private int _numerator;
        private readonly int _denominator = 100 * Math.Max(1, total);
        private readonly Lock _lock = new();

        public IProgress<int> SubProgress
        {
            get
            {
                var previous = 0;
                return new DelegateProgress(x =>
                {
                    int current;
                    lock (_lock)
                    {
                        _numerator -= previous;
                        _numerator += x;
                        current = _numerator;
                    }

                    previous = x;
                    progress?.Report(current * 100 / _denominator);
                });
            }
        }
    }

    private sealed class ScaledProgress(IProgress<int>? inner, int numerator, int denominator) : IProgress<int>
    {
        public void Report(int value)
        {
            inner?.Report(value * numerator / denominator);
        }
    }

    private sealed class OffsetProgress(IProgress<int>? inner, int offset) : IProgress<int>
    {
        public void Report(int value)
        {
            inner?.Report(value + offset);
        }
    }

    private sealed class DelegateProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value)
        {
            report(value);
        }
    }

    private sealed class PercentageProgress(IProgress<int>? inner, int denominator) : IProgress<int>
    {
        private readonly Lock _lock = new();
        private int _lastPercentage = -1;

        public void Report(int value)
        {
            var percentage = (int)((long)value * 100 / denominator);
            lock (_lock)
            {
                if (percentage <= _lastPercentage) return;
                _lastPercentage = percentage;
            }

            inner?.Report(percentage);
        }
    }
}
