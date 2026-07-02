using System.Text.Json.Serialization;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.SabControllers.GetServerStats;

public class GetServerStatsResponse
{
    [JsonPropertyName("status")]
    public bool Status { get; init; } = true;

    [JsonPropertyName("total")]
    public long TotalBytes { get; init; }

    [JsonPropertyName("day")]
    public long DayBytes { get; init; }

    [JsonPropertyName("week")]
    public long WeekBytes { get; init; }

    [JsonPropertyName("month")]
    public long MonthBytes { get; init; }

    [JsonPropertyName("servers")]
    public Dictionary<string, ServerStatsObject> Servers { get; init; } = [];

    public static Dictionary<string, ServerStatsObject> GetServers(UsenetProviderConfig providerConfig)
    {
        return providerConfig.Providers
            .Select((provider, index) => new
            {
                Key = string.IsNullOrWhiteSpace(provider.Host) ? $"server_{index + 1}" : provider.Host,
                Stats = new ServerStatsObject
                {
                    Host = provider.Host,
                    Port = provider.Port,
                    Connections = provider.MaxConnections
                }
            })
            .GroupBy(x => x.Key)
            .ToDictionary(
                group => group.Key,
                group => group.First().Stats);
    }

    public class ServerStatsObject
    {
        [JsonPropertyName("host")]
        public string Host { get; init; } = "";

        [JsonPropertyName("port")]
        public int Port { get; init; }

        [JsonPropertyName("connections")]
        public int Connections { get; init; }

        [JsonPropertyName("total")]
        public long TotalBytes { get; init; }

        [JsonPropertyName("day")]
        public long DayBytes { get; init; }

        [JsonPropertyName("week")]
        public long WeekBytes { get; init; }

        [JsonPropertyName("month")]
        public long MonthBytes { get; init; }

        [JsonPropertyName("articles_tried")]
        public long ArticlesTried { get; init; }

        [JsonPropertyName("articles_success")]
        public long ArticlesSuccess { get; init; }
    }
}
