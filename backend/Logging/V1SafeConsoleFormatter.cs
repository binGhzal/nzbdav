using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Serilog.Events;
using Serilog.Formatting;

namespace NzbWebDAV.Logging;

public enum V1OperationalEventId
{
    Unclassified,
    ContentArticleMissing,
    ContentProviderTemporarilyUnavailable,
    ContentRangeUnavailable,
    ContentMetadataMissing,
    ContentReadFailure,
    RequestFailure,
    DynamicRepairScheduled,
    DynamicRepairScheduleFailure,
    QueueTerminalFailure,
    QueueTerminalCommitFailure,
    UsenetConnectionTimeout,
    UsenetConnectionFailure,
    UsenetAuthenticationFailure,
    UsenetUnexpectedFailure,
}

public sealed class V1SafeConsoleFormatter : ITextFormatter
{
    public const string EventIdPropertyName = "V1OperationalEventId";

    private static readonly string[] GuidPropertyNames =
    [
        "DavItemId",
        "QueueItemId",
        "HistoryItemId",
        "MaintenanceRunId",
        "RepairRunId",
        "WorkerJobId",
        "NzbBlobId",
    ];

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        output.Write("ts=");
        output.Write(logEvent.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        output.Write(" level=");
        output.Write(ToLevelCode(logEvent.Level));
        output.Write(" event=");
        output.Write(ComputeEventCode(logEvent));

        WriteCorrelationId(logEvent, output);
        foreach (var propertyName in GuidPropertyNames)
            WriteGuid(logEvent, output, propertyName);

        output.Write('\n');
    }

    private static string ComputeEventCode(LogEvent logEvent)
    {
        var eventId = logEvent.Properties.TryGetValue(EventIdPropertyName, out var propertyValue)
                      && propertyValue is ScalarValue { Value: V1OperationalEventId typed }
                      && Enum.IsDefined(typed)
            ? typed
            : V1OperationalEventId.Unclassified;
        var canonicalId = eventId switch
        {
            V1OperationalEventId.Unclassified => "unclassified",
            V1OperationalEventId.ContentArticleMissing => "content_article_missing",
            V1OperationalEventId.ContentProviderTemporarilyUnavailable =>
                "content_provider_temporarily_unavailable",
            V1OperationalEventId.ContentRangeUnavailable => "content_range_unavailable",
            V1OperationalEventId.ContentMetadataMissing => "content_metadata_missing",
            V1OperationalEventId.ContentReadFailure => "content_read_failure",
            V1OperationalEventId.RequestFailure => "request_failure",
            V1OperationalEventId.DynamicRepairScheduled => "dynamic_repair_scheduled",
            V1OperationalEventId.DynamicRepairScheduleFailure => "dynamic_repair_schedule_failure",
            V1OperationalEventId.QueueTerminalFailure => "queue_terminal_failure",
            V1OperationalEventId.QueueTerminalCommitFailure => "queue_terminal_commit_failure",
            V1OperationalEventId.UsenetConnectionTimeout => "usenet_connection_timeout",
            V1OperationalEventId.UsenetConnectionFailure => "usenet_connection_failure",
            V1OperationalEventId.UsenetAuthenticationFailure => "usenet_authentication_failure",
            V1OperationalEventId.UsenetUnexpectedFailure => "usenet_unexpected_failure",
            _ => "unclassified",
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalId)))
            .ToLowerInvariant();
    }

    private static void WriteCorrelationId(LogEvent logEvent, TextWriter output)
    {
        if (!logEvent.Properties.TryGetValue("CorrelationId", out var propertyValue)
            || propertyValue is not ScalarValue { Value: string correlationId }
            || !IsLowerHexCorrelationId(correlationId))
        {
            return;
        }

        output.Write(" CorrelationId=");
        output.Write(correlationId);
    }

    private static void WriteGuid(LogEvent logEvent, TextWriter output, string propertyName)
    {
        if (!logEvent.Properties.TryGetValue(propertyName, out var propertyValue)
            || propertyValue is not ScalarValue { Value: Guid identifier })
        {
            return;
        }

        output.Write(' ');
        output.Write(propertyName);
        output.Write('=');
        output.Write(identifier.ToString("N", CultureInfo.InvariantCulture));
    }

    private static bool IsLowerHexCorrelationId(string value)
    {
        if (value.Length != 32)
            return false;

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    private static string ToLevelCode(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "UNK",
    };
}
