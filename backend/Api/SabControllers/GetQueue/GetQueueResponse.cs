using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueResponse : SabBaseResponse
{
    [JsonPropertyName("queue")]
    public QueueObject Queue { get; init; } = new();

    public class QueueObject
    {
        [JsonPropertyName("status")]
        public string Status { get; init; } = "Idle";

        [JsonPropertyName("paused")]
        public bool Paused { get; init; } = false;

        [JsonPropertyName("paused_all")]
        public bool PausedAll { get; init; } = false;

        [JsonPropertyName("slots")]
        public List<QueueSlot> Slots { get; init; } = new();

        [JsonPropertyName("noofslots")]
        public int TotalCount { get; set; }

        [JsonPropertyName("noofslots_total")]
        public int TotalCountAll { get; set; }

        [JsonPropertyName("start")]
        public int Start { get; init; }

        [JsonPropertyName("limit")]
        public int Limit { get; init; }

        [JsonPropertyName("mb")]
        public string SizeInMB { get; init; } = "0.00";

        [JsonPropertyName("mbleft")]
        public string SizeLeftInMB { get; init; } = "0.00";

        [JsonPropertyName("size")]
        public string Size { get; init; } = "0.00 MB";

        [JsonPropertyName("sizeleft")]
        public string SizeLeft { get; init; } = "0.00 MB";

        [JsonPropertyName("timeleft")]
        [JsonConverter(typeof(SabnzbdQueueTimeConverter))]
        public TimeSpan TimeLeft { get; init; } = TimeSpan.Zero;

        [JsonPropertyName("speed")]
        public string Speed { get; init; } = "0 ";

        [JsonPropertyName("kbpersec")]
        public string KbPerSec { get; init; } = "0.00";
    }

    public class QueueSlot
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("nzo_id")]
        public string NzoId { get; init; } = "";

        [JsonPropertyName("priority")]
        public string Priority { get; init; } = "";

        [JsonPropertyName("filename")]
        public string Filename { get; init; } = "";

        [JsonPropertyName("cat")]
        public string Category { get; init; } = "";

        [JsonPropertyName("percentage")]
        public string Percentage { get; init; } = "0";

        [JsonPropertyName("true_percentage")]
        public string TruePercentage { get; init; } = "0";

        [JsonPropertyName("status")]
        public string Status { get; init; } = "";

        [JsonPropertyName("timeleft")]
        [JsonConverter(typeof(SabnzbdQueueTimeConverter))]
        public TimeSpan TimeLeft { get; init; }

        [JsonPropertyName("mb")]
        public string SizeInMB { get; init; } = "0.00";

        [JsonPropertyName("mbleft")]
        public string SizeLeftInMB { get; init; } = "0.00";

        [JsonPropertyName("arr_priority")]
        public ArrPriorityObject? ArrPriority { get; init; }

        public static QueueSlot FromQueueItem
        (
            QueueItem queueItem,
            int index = 0,
            int progressPercentage = 0,
            string status = "Queued",
            QueuePriorityHint? priorityHint = null
        )
        {
            return new QueueSlot
            {
                Index = index,
                NzoId = queueItem!.Id.ToString(),
                Priority = queueItem.Priority.ToString(),
                Filename = queueItem.FileName,
                Category = queueItem.Category,
                Percentage = Math.Clamp(progressPercentage, 0, 100).ToString(),
                TruePercentage = Math.Clamp(progressPercentage, 0, 100).ToString(),
                Status = status,
                TimeLeft = TimeSpan.Zero,
                SizeInMB = FormatSizeMB(queueItem.TotalSegmentBytes),
                SizeLeftInMB = FormatSizeMB((100 - Math.Clamp(progressPercentage, 0, 100)) * queueItem.TotalSegmentBytes / 100),
                ArrPriority = priorityHint == null ? null : ArrPriorityObject.FromHint(priorityHint),
            };
        }

        public static string FormatSizeMB(long bytes)
        {
            var megabytes = bytes / (1024.0 * 1024.0);
            return megabytes.ToString("0.00");
        }
    }

    public class ArrPriorityObject
    {
        [JsonPropertyName("score")]
        public int Score { get; init; }

        [JsonPropertyName("effective_priority")]
        public string EffectivePriority { get; init; } = "";

        [JsonPropertyName("apply_to_scheduling")]
        public bool ApplyToScheduling { get; init; }

        [JsonPropertyName("reasons")]
        public string[] Reasons { get; init; } = [];

        [JsonPropertyName("source")]
        public string Source { get; init; } = "";

        [JsonPropertyName("stale_reason")]
        public string? StaleReason { get; init; }

        public static ArrPriorityObject FromHint(QueuePriorityHint hint)
        {
            string[] reasons;
            try
            {
                reasons = JsonSerializer.Deserialize<string[]>(hint.ReasonsJson) ?? [];
            }
            catch
            {
                reasons = [];
            }

            return new ArrPriorityObject
            {
                Score = hint.Score,
                EffectivePriority = hint.EffectivePriority.ToString(),
                ApplyToScheduling = hint.ApplyToScheduling,
                Reasons = reasons,
                Source = hint.Source,
                StaleReason = hint.StaleReason
            };
        }
    }

    public class SabnzbdQueueTimeConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
            throw new NotImplementedException();

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(@"d\:h\:m\:s"));
    }
}
