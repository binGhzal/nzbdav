using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.SabControllers.GetWarnings;

public class GetWarningsResponse
{
    [JsonPropertyName("warnings")]
    public List<WarningObject> Warnings { get; init; } = [];

    public class WarningObject
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "INFO";

        [JsonPropertyName("time")]
        public string Time { get; init; } = "";

        [JsonPropertyName("message")]
        public string Message { get; init; } = "";
    }
}
