namespace NzbWebDAV.Clients.Usenet.Models;

public enum SegmentCheckState
{
    Exists = 0,
    Missing = 1,
    ProviderError = 2,
    Unknown = 3
}

public sealed record SegmentCheckResult(
    string SegmentId,
    SegmentCheckState State,
    string? Provider,
    string? Error,
    string? CandidateSegmentId = null
);

public sealed record SegmentCheckBatch(
    IReadOnlyList<SegmentCheckResult> Results,
    int Checked,
    int Missing,
    int ProviderErrors,
    int Unknown
)
{
    public bool IsClean => Missing == 0 && ProviderErrors == 0 && Unknown == 0;

    public static SegmentCheckBatch FromResults(IReadOnlyList<SegmentCheckResult> results)
    {
        return new SegmentCheckBatch(
            results,
            Checked: results.Count,
            Missing: results.Count(x => x.State == SegmentCheckState.Missing),
            ProviderErrors: results.Count(x => x.State == SegmentCheckState.ProviderError),
            Unknown: results.Count(x => x.State == SegmentCheckState.Unknown)
        );
    }

    public static SegmentCheckBatch AllExists(IReadOnlyList<string> segmentIds)
    {
        return new SegmentCheckBatch(
            new AllExistsResults(segmentIds),
            Checked: segmentIds.Count,
            Missing: 0,
            ProviderErrors: 0,
            Unknown: 0);
    }

    private sealed class AllExistsResults(IReadOnlyList<string> segmentIds) : IReadOnlyList<SegmentCheckResult>
    {
        public int Count => segmentIds.Count;

        public SegmentCheckResult this[int index] =>
            new(segmentIds[index], SegmentCheckState.Exists, Provider: null, Error: null);

        public IEnumerator<SegmentCheckResult> GetEnumerator()
        {
            for (var i = 0; i < segmentIds.Count; i++)
                yield return this[i];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
