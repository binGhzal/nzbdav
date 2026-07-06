using System.Net.WebSockets;
using System.Reflection;
using NzbWebDAV.Websocket;

namespace backend.Tests.Websocket;

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

    private static HashSet<WebSocket> GetAuthenticatedSockets(WebsocketManager manager)
    {
        var field = typeof(WebsocketManager).GetField(
            "_authenticatedSockets",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<HashSet<WebSocket>>(field.GetValue(manager));
    }

    private sealed class RecordingWebSocket(bool failSends) : WebSocket
    {
        public int SendCount { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

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

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SendCount++;
            if (failSends)
                throw new IOException("send failed");
            return Task.CompletedTask;
        }
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
