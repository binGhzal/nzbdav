using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers.GetScripts;

public class GetScriptsResponse
{
    [JsonPropertyName("scripts")]
    public List<string> Scripts { get; init; } = ["None"];
}
