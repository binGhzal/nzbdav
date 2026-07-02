using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.Repair;

public sealed class RepairStatusResponse : BaseApiResponse
{
    [JsonPropertyName("active_run")]
    public RepairRunDto? ActiveRun { get; init; }

    [JsonPropertyName("last_run")]
    public RepairRunDto? LastRun { get; init; }

    [JsonPropertyName("broken_files")]
    public IReadOnlyList<RepairBrokenFileDto> BrokenFiles { get; init; } = [];

    [JsonPropertyName("verify_queue")]
    public required RepairWorkerQueueDto VerifyQueue { get; init; }

    [JsonPropertyName("repair_queue")]
    public required RepairWorkerQueueDto RepairQueue { get; init; }
}
