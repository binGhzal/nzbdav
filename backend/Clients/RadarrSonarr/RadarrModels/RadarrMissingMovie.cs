using System.Text.Json.Serialization;

namespace NzbWebDAV.Clients.RadarrSonarr.RadarrModels;

public class RadarrMissingMovie
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("monitored")]
    public bool Monitored { get; set; }

    [JsonPropertyName("hasFile")]
    public bool HasFile { get; set; }

    [JsonPropertyName("inCinemas")]
    public DateTimeOffset? InCinemas { get; set; }

    [JsonPropertyName("digitalRelease")]
    public DateTimeOffset? DigitalRelease { get; set; }

    [JsonPropertyName("physicalRelease")]
    public DateTimeOffset? PhysicalRelease { get; set; }

    [JsonPropertyName("collection")]
    public RadarrMovieCollection? Collection { get; set; }
}

public class RadarrMovieCollection
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
