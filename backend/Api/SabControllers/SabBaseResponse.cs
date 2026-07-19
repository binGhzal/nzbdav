using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers;

public class SabBaseResponse
{
    public bool Status { get; set; } = true;
    public string? Error { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    [JsonPropertyName("correlation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; set; }
}
