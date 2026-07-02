using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.Repair;

public sealed class RepairRunResponse : BaseApiResponse
{
    [JsonPropertyName("run")]
    public required RepairRunDto Run { get; init; }
}
