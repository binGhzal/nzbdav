using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using backend.Tests.Database;
using backend.Tests.Security;
using NzbWebDAV.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace backend.Tests.Logging;

public sealed class V1SafeConsoleFormatterTests
{
    private static readonly DateTimeOffset TestTimestamp =
        new(2026, 7, 19, 17, 45, 12, TimeSpan.FromHours(5));

    [Fact]
    public void FormatEmitsOnlyStableFieldsAndStrictlyTypedOperationalIds()
    {
        const string correlationId = "0123456789abcdef0123456789abcdef";
        var davItemId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var historyItemId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var maintenanceRunId = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var repairRunId = Guid.Parse("40000000-0000-0000-0000-000000000004");
        var workerJobId = Guid.Parse("50000000-0000-0000-0000-000000000005");
        var nzbBlobId = Guid.Parse("60000000-0000-0000-0000-000000000006");
        var template = new MessageTemplateParser().Parse(
            string.Concat(PublicFailureCanary.Composite, " {DavItemId} {UnsafeValue}"));
        var logEvent = CreateEvent(
            TestTimestamp,
            LogEventLevel.Warning,
            template,
            PublicFailureCanary.NestedException,
            new LogEventProperty(
                V1SafeConsoleFormatter.EventIdPropertyName,
                new ScalarValue(V1OperationalEventId.ContentArticleMissing)),
            new LogEventProperty("CorrelationId", new ScalarValue(correlationId)),
            new LogEventProperty("DavItemId", new ScalarValue(davItemId)),
            new LogEventProperty("QueueItemId", new ScalarValue(davItemId.ToString("D"))),
            new LogEventProperty("HistoryItemId", new ScalarValue(historyItemId)),
            new LogEventProperty("MaintenanceRunId", new ScalarValue(maintenanceRunId)),
            new LogEventProperty("RepairRunId", new ScalarValue(repairRunId)),
            new LogEventProperty("WorkerJobId", new ScalarValue(workerJobId)),
            new LogEventProperty("NzbBlobId", new ScalarValue(nzbBlobId)),
            new LogEventProperty("RequestId", new ScalarValue(Guid.NewGuid())),
            new LogEventProperty("UnsafeValue", new ScalarValue(PublicFailureCanary.Composite)));

        var output = Format(logEvent);

        AssertSafeConsoleOutput(output);
        var expectedEventCode = ComputeExpectedEventCode(V1OperationalEventId.ContentArticleMissing);
        Assert.Equal(
            $"ts=2026-07-19T12:45:12.0000000Z level=WRN event={expectedEventCode}"
            + $" CorrelationId={correlationId}"
            + $" DavItemId={davItemId:N}"
            + $" HistoryItemId={historyItemId:N}"
            + $" MaintenanceRunId={maintenanceRunId:N}"
            + $" RepairRunId={repairRunId:N}"
            + $" WorkerJobId={workerJobId:N}"
            + $" NzbBlobId={nzbBlobId:N}\n",
            output);
        Assert.DoesNotContain("QueueItemId=", output, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestId=", output, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRejectsMalformedCorrelationAndWronglyTypedOrUnlistedProperties()
    {
        var template = new MessageTemplateParser().Parse("stable_template");
        var logEvent = CreateEvent(
            TestTimestamp,
            LogEventLevel.Error,
            template,
            null,
            new LogEventProperty(
                "CorrelationId",
                new ScalarValue("ABCDEF0123456789ABCDEF0123456789")),
            new LogEventProperty("DavItemId", new ScalarValue(42)),
            new LogEventProperty(
                "QueueItemId",
                new ScalarValue("70000000-0000-0000-0000-000000000007")),
            new LogEventProperty(
                "TraceId",
                new ScalarValue(Guid.Parse("80000000-0000-0000-0000-000000000008"))));

        var output = Format(logEvent);

        AssertSafeConsoleOutput(output);
        var expectedEventCode = ComputeExpectedEventCode(V1OperationalEventId.Unclassified);
        Assert.Equal(
            $"ts=2026-07-19T12:45:12.0000000Z level=ERR event={expectedEventCode}\n",
            output);
    }

    [Fact]
    public void EventCodeRequiresClosedTypedIdentityAndCannotOracleDynamicTemplateData()
    {
        var template = new MessageTemplateParser().Parse("queue_failure {QueueItemId}");
        var first = CreateEvent(
            TestTimestamp,
            LogEventLevel.Error,
            template,
            PublicFailureCanary.NestedException,
            new LogEventProperty(
                V1SafeConsoleFormatter.EventIdPropertyName,
                new ScalarValue(V1OperationalEventId.QueueTerminalFailure)),
            new LogEventProperty(
                "QueueItemId",
                new ScalarValue(Guid.Parse("90000000-0000-0000-0000-000000000009"))));
        var second = CreateEvent(
            TestTimestamp.AddYears(1),
            LogEventLevel.Error,
            template,
            new InvalidOperationException("different"),
            new LogEventProperty(
                V1SafeConsoleFormatter.EventIdPropertyName,
                new ScalarValue(V1OperationalEventId.QueueTerminalFailure)),
            new LogEventProperty(
                "QueueItemId",
                new ScalarValue(Guid.Parse("a0000000-0000-0000-0000-00000000000a"))));
        var differentLevel = CreateEvent(
            TestTimestamp,
            LogEventLevel.Warning,
            template,
            null,
            new LogEventProperty(
                V1SafeConsoleFormatter.EventIdPropertyName,
                new ScalarValue(V1OperationalEventId.QueueTerminalFailure)));
        var differentIdentity = CreateEvent(
            TestTimestamp,
            LogEventLevel.Error,
            template,
            null,
            new LogEventProperty(
                V1SafeConsoleFormatter.EventIdPropertyName,
                new ScalarValue(V1OperationalEventId.QueueTerminalCommitFailure)));
        var differentTemplate = CreateEvent(
            TestTimestamp,
            LogEventLevel.Error,
            new MessageTemplateParser().Parse("queue_failure_changed {QueueItemId}"),
            null,
            new LogEventProperty(
                V1SafeConsoleFormatter.EventIdPropertyName,
                new ScalarValue(V1OperationalEventId.QueueTerminalFailure)));

        var firstCode = ReadField(Format(first), "event");
        var secondCode = ReadField(Format(second), "event");
        var differentLevelCode = ReadField(Format(differentLevel), "event");
        var differentIdentityCode = ReadField(Format(differentIdentity), "event");
        var differentTemplateCode = ReadField(Format(differentTemplate), "event");

        Assert.Equal(64, firstCode.Length);
        Assert.True(firstCode.All(IsLowerHex));
        Assert.Equal(firstCode, secondCode);
        Assert.Equal(firstCode, differentLevelCode);
        Assert.NotEqual(firstCode, differentIdentityCode);
        Assert.Equal(firstCode, differentTemplateCode);

        var firstHostile = CreateEvent(
            TestTimestamp,
            LogEventLevel.Error,
            new MessageTemplateParser().Parse(PublicFailureCanary.Composite),
            null,
            new LogEventProperty(
                V1SafeConsoleFormatter.EventIdPropertyName,
                new ScalarValue("QueueTerminalFailure")));
        var secondHostile = CreateEvent(
            TestTimestamp,
            LogEventLevel.Error,
            new MessageTemplateParser().Parse("different-private-path|different-secret"),
            null,
            new LogEventProperty(
                V1SafeConsoleFormatter.EventIdPropertyName,
                new ScalarValue((int)V1OperationalEventId.QueueTerminalFailure)));
        Assert.Equal(ReadField(Format(firstHostile), "event"), ReadField(Format(secondHostile), "event"));
        Assert.Equal(
            ComputeExpectedEventCode(V1OperationalEventId.Unclassified),
            ReadField(Format(firstHostile), "event"));
    }

    [Fact]
    public void ProgramRoutesTheProductionConsoleSinkThroughTheSafeFormatter()
    {
        var source = File.ReadAllText(SqliteContractTestSupport.AbsolutePath("backend/Program.cs"));

        Assert.Contains(
            ".WriteTo.Console(new V1SafeConsoleFormatter())",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AnsiConsoleTheme", source, StringComparison.Ordinal);
    }

    private static LogEvent CreateEvent(
        DateTimeOffset timestamp,
        LogEventLevel level,
        MessageTemplate template,
        Exception? exception,
        params LogEventProperty[] properties) =>
        new(timestamp, level, exception, template, properties);

    private static string Format(LogEvent logEvent)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new V1SafeConsoleFormatter().Format(logEvent, writer);
        return writer.ToString();
    }

    private static string ComputeExpectedEventCode(V1OperationalEventId eventId)
    {
        var source = Encoding.UTF8.GetBytes(eventId switch
        {
            V1OperationalEventId.Unclassified => "unclassified",
            V1OperationalEventId.ContentArticleMissing => "content_article_missing",
            V1OperationalEventId.QueueTerminalFailure => "queue_terminal_failure",
            V1OperationalEventId.QueueTerminalCommitFailure => "queue_terminal_commit_failure",
            _ => throw new ArgumentOutOfRangeException(nameof(eventId), eventId, null),
        });
        return Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
    }

    private static string ReadField(string output, string fieldName)
    {
        AssertSafeConsoleOutput(output);
        var prefix = string.Concat(fieldName, "=");
        var field = output
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Single(part => part.StartsWith(prefix, StringComparison.Ordinal));
        return field[prefix.Length..].TrimEnd('\n');
    }

    private static void AssertSafeConsoleOutput(string output)
    {
        PublicFailureCanary.AssertSafe(output, maximumLength: 1024);
        Assert.True(output.All(character => character == '\n' || !char.IsControl(character)));
        Assert.Equal(1, output.Count(character => character == '\n'));
        Assert.EndsWith("\n", output, StringComparison.Ordinal);
    }

    private static bool IsLowerHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f';
}
