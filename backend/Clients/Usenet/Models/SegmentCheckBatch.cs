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
}
