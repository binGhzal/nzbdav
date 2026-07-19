using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using backend.Tests.Security;
using backend.Tests.Services;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace backend.Tests.Websocket;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class WebsocketManagerTests
{
    [Fact]
    public async Task SendMessage_RemovesSocketAfterSendFailure()
    {
        var manager = new WebsocketManager();
        var failedSocket = new RecordingWebSocket(failSends: true);
        var healthySocket = new RecordingWebSocket(failSends: false);
        AddAuthenticatedSockets(manager, failedSocket, healthySocket);

        await manager.SendMessage(WebsocketTopic.QueueItemProgress, "first");
        await manager.SendMessage(WebsocketTopic.QueueItemProgress, "second");

        Assert.Equal(1, failedSocket.SendCount);
        Assert.Equal(2, healthySocket.SendCount);
        Assert.Equal(1, GetAuthenticatedSocketCount(manager));
        Assert.Equal(1, GetSocketSendStateCount(manager));
    }

    [Fact]
    public async Task SendMessageFailureLogsOnlyStableEvent()
    {
        using var logger = new LoggerScope();
        var manager = new WebsocketManager();
        var failedSocket = new RecordingWebSocket(
            failSends: true,
            failureMessage: PublicFailureCanary.Composite);
        AddAuthenticatedSockets(manager, failedSocket);

        await manager.SendMessage(WebsocketTopic.QueueItemProgress, "message");

        PublicFailureCanary.AssertSafe(logger.Rendered);
        Assert.Contains("Failed to send message to websocket.", logger.Rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMessage_RemovesSocketAfterSendTimeout()
    {
        var manager = new WebsocketManager();
        var blockedSocket = new BlockingWebSocket();
        var healthySocket = new RecordingWebSocket(failSends: false);
        AddAuthenticatedSockets(manager, blockedSocket, healthySocket);

        var sendTask = manager.SendMessage(WebsocketTopic.QueueItemProgress, "first");
        await blockedSocket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            var completed = await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(sendTask, completed);
        }
        finally
        {
            blockedSocket.Release();
            await sendTask.WaitAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, blockedSocket.SendCount);
        Assert.Equal(1, healthySocket.SendCount);
        Assert.Equal(1, GetAuthenticatedSocketCount(manager));
        Assert.Equal(1, GetSocketSendStateCount(manager));
    }

    [Fact]
    public async Task SendMessage_SerializesEachSocketWithoutBlockingFanoutToOtherSockets()
    {
        var manager = new WebsocketManager();
        var orderedSocket = new RecordingWebSocket(
            failSends: false,
            blockFirstSend: true,
            rejectConcurrentSends: true);
        var fastSocket = new RecordingWebSocket(failSends: false);
        AddAuthenticatedSockets(manager, orderedSocket, fastSocket);

        var removeTask = manager.SendMessage(WebsocketTopic.QueueItemRemoved, "queue-id");
        await orderedSocket.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var historyTask = manager.SendMessage(WebsocketTopic.HistoryItemAdded, "history-json");

        int orderedSendCountBeforeRelease;
        bool fastSocketReceivedBothBeforeRelease;
        try
        {
            await fastSocket.SecondSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            orderedSendCountBeforeRelease = orderedSocket.SendCount;
            fastSocketReceivedBothBeforeRelease = fastSocket.SendCount == 2;
        }
        finally
        {
            orderedSocket.ReleaseFirstSend();
        }

        await Task.WhenAll(removeTask, historyTask).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, orderedSendCountBeforeRelease);
        Assert.True(fastSocketReceivedBothBeforeRelease);
        Assert.Equal(1, orderedSocket.MaxConcurrentSends);
        Assert.Equal(["qr", "ha"], orderedSocket.Messages.Select(GetTopic));
        Assert.Equal(["qr", "ha"], fastSocket.Messages.Select(GetTopic));
        Assert.Equal(2, GetAuthenticatedSocketCount(manager));
        Assert.Equal(2, GetSocketSendStateCount(manager));
    }

    private static void AddAuthenticatedSockets(WebsocketManager manager, params WebSocket[] sockets)
    {
        var authenticatedSockets = GetAuthenticatedSockets(manager);
        foreach (var socket in sockets)
            authenticatedSockets.Add(socket);
    }

    private static int GetAuthenticatedSocketCount(WebsocketManager manager)
    {
        return GetAuthenticatedSockets(manager).Count;
    }

    private static int GetSocketSendStateCount(WebsocketManager manager)
    {
        var field = typeof(WebsocketManager).GetField(
            "_socketSendStates",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var states = Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(manager));
        return states.Count;
    }

    private static string GetTopic(string message)
    {
        using var json = JsonDocument.Parse(message);
        return Assert.IsType<string>(json.RootElement.GetProperty("Topic").GetString());
    }

    private static HashSet<WebSocket> GetAuthenticatedSockets(WebsocketManager manager)
    {
        var field = typeof(WebsocketManager).GetField(
            "_authenticatedSockets",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<HashSet<WebSocket>>(field.GetValue(manager));
    }

    private sealed class RecordingWebSocket(
        bool failSends,
        bool blockFirstSend = false,
        bool rejectConcurrentSends = false,
        string failureMessage = "send failed") : WebSocket
    {
        private readonly Lock _messagesLock = new();
        private readonly List<string> _messages = [];
        private readonly TaskCompletionSource _releaseFirstSend =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeSends;
        private int _maxConcurrentSends;
        private int _sendCount;

        public TaskCompletionSource FirstSendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount => Volatile.Read(ref _sendCount);
        public int MaxConcurrentSends => Volatile.Read(ref _maxConcurrentSends);
        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messagesLock) return _messages.ToArray();
            }
        }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public void ReleaseFirstSend()
        {
            _releaseFirstSend.TrySetResult();
        }

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new WebSocketReceiveResult(
                count: 0,
                messageType: WebSocketMessageType.Close,
                endOfMessage: true,
                closeStatus: WebSocketCloseStatus.NormalClosure,
                closeStatusDescription: "closed"));
        }

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            var activeSends = Interlocked.Increment(ref _activeSends);
            UpdateMax(ref _maxConcurrentSends, activeSends);
            var sendCount = Interlocked.Increment(ref _sendCount);
            lock (_messagesLock)
                _messages.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            if (sendCount == 1) FirstSendStarted.TrySetResult();
            if (sendCount == 2) SecondSendStarted.TrySetResult();

            try
            {
                if (rejectConcurrentSends && activeSends > 1)
                    throw new IOException("overlapping WebSocket sends are not supported");
                if (failSends)
                    throw new IOException(failureMessage);
                if (blockFirstSend && sendCount == 1)
                    await _releaseFirstSend.Task.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeSends);
            }
        }

        private static void UpdateMax(ref int location, int value)
        {
            var observed = Volatile.Read(ref location);
            while (value > observed)
            {
                var original = Interlocked.CompareExchange(ref location, value, observed);
                if (original == observed) return;
                observed = original;
            }
        }
    }

    private sealed class LoggerScope : IDisposable
    {
        private readonly ILogger _previous = Log.Logger;
        private readonly CollectingSink _sink = new();
        private readonly ILogger _logger;

        public LoggerScope()
        {
            _logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(_sink)
                .CreateLogger();
            Log.Logger = _logger;
        }

        public string Rendered => string.Join("\n", _sink.Events.Select(Render));

        public void Dispose()
        {
            Log.Logger = _previous;
            (_logger as IDisposable)?.Dispose();
        }

        private static string Render(LogEvent logEvent)
        {
            var properties = string.Join(" ", logEvent.Properties.Select(x => $"{x.Key}={x.Value}"));
            return string.Join(" ",
                logEvent.RenderMessage(),
                logEvent.Exception?.ToString() ?? "",
                properties);
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class BlockingWebSocket : WebSocket
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public void Release()
        {
            _release.TrySetResult();
        }

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            Release();
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new WebSocketReceiveResult(
                count: 0,
                messageType: WebSocketMessageType.Close,
                endOfMessage: true,
                closeStatus: WebSocketCloseStatus.NormalClosure,
                closeStatusDescription: "closed"));
        }

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SendCount++;
            SendStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
