using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.Repair;

public sealed class RepairRunsResponse : BaseApiResponse
{
    [JsonPropertyName("runs")]
    public IReadOnlyList<RepairRunDto> Runs { get; init; } = [];
}
