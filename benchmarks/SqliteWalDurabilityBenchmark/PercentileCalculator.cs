namespace NzbWebDAV.Benchmarks.SqliteWal;

public static class PercentileCalculator
{
    public static double Calculate(IReadOnlyCollection<double> samples, double percentile)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        if (percentile is < 0 or > 1 || double.IsNaN(percentile))
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be between 0 and 1.");

        var ordered = samples.Order().ToArray();
        var rank = (ordered.Length - 1) * percentile;
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);
        if (lowerIndex == upperIndex)
            return ordered[lowerIndex];

        var fraction = rank - lowerIndex;
        return ordered[lowerIndex] + ((ordered[upperIndex] - ordered[lowerIndex]) * fraction);
    }
}
