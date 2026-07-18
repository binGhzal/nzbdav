using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Websocket;

public class WebsocketManager
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromMilliseconds(250);
    private readonly HashSet<WebSocket> _authenticatedSockets = [];
    private readonly Dictionary<WebSocket, SocketSendState> _socketSendStates =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<WebsocketTopic, string> _lastMessage = new();

    public async Task HandleRoute(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            if (!await Authenticate(webSocket).ConfigureAwait(false))
            {
                Log.Warning($"Closing unauthenticated websocket connection from {context.Connection.RemoteIpAddress}");
                await CloseUnauthorizedConnection(webSocket).ConfigureAwait(false);
                return;
            }

            // mark the socket as authenticated
            SocketSendState socketSendState;
            lock (_authenticatedSockets)
            {
                _authenticatedSockets.Add(webSocket);
                socketSendState = GetOrCreateSocketSendStateLocked(webSocket);
            }

            // send current state for all topics
            List<KeyValuePair<WebsocketTopic, string>>? lastMessage;
            lock (_lastMessage) lastMessage = _lastMessage.ToList();
            foreach (var message in lastMessage)
                if (message.Key.Type == WebsocketTopic.TopicType.State)
                    await SendMessage(webSocket, socketSendState, message.Key, message.Value)
                        .ConfigureAwait(false);

            // wait for the socket to disconnect
            await WaitForDisconnected(webSocket).ConfigureAwait(false);
            RemoveAuthenticatedSocket(webSocket, socketSendState);
        }
        else
        {
            context.Response.StatusCode = 400;
        }
    }

    /// <summary>
    /// Send a message to all authenticated websockets.
    /// </summary>
    /// <param name="topic">The topic of the message to send</param>
    /// <param name="message">The message to send</param>
    public virtual Task SendMessage(WebsocketTopic topic, string message)
    {
        lock (_lastMessage) _lastMessage[topic] = message;
        List<SocketSendTarget> authenticatedSockets;
        lock (_authenticatedSockets)
        {
            authenticatedSockets = _authenticatedSockets
                .Select(socket => new SocketSendTarget(socket, GetOrCreateSocketSendStateLocked(socket)))
                .ToList();
        }
        var topicMessage = new TopicMessage(topic, message);
        var bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(topicMessage.ToJson()));
        return SendMessageToAuthenticatedSockets(authenticatedSockets, bytes);
    }

    private async Task SendMessageToAuthenticatedSockets
    (
        List<SocketSendTarget> authenticatedSockets,
        ArraySegment<byte> bytes
    )
    {
        var results = await Task.WhenAll(authenticatedSockets.Select(x => SendMessage(x, bytes)))
            .ConfigureAwait(false);
        var failedSockets = authenticatedSockets
            .Where((_, index) => !results[index])
            .ToList();
        if (failedSockets.Count == 0) return;

        foreach (var target in failedSockets)
            RemoveAuthenticatedSocket(target.Socket, target.State);
    }

    private SocketSendState GetOrCreateSocketSendStateLocked(WebSocket socket)
    {
        if (_socketSendStates.TryGetValue(socket, out var state)) return state;
        state = new SocketSendState();
        _socketSendStates.Add(socket, state);
        return state;
    }

    private void RemoveAuthenticatedSocket(WebSocket socket, SocketSendState state)
    {
        lock (_authenticatedSockets)
        {
            _authenticatedSockets.Remove(socket);
            if (!_socketSendStates.TryGetValue(socket, out var current)
                || !ReferenceEquals(current, state))
                return;

            state.Disable();
            _socketSendStates.Remove(socket);
        }
    }

    /// <summary>
    /// Ensure a websocket sends a valid api key.
    /// </summary>
    /// <param name="socket">The websocket to authenticate.</param>
    /// <returns>True if authenticated, False otherwise.</returns>
    private static async Task<bool> Authenticate(WebSocket socket)
    {
        var apiKey = await ReceiveAuthToken(socket).ConfigureAwait(false);
        return apiKey == EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    /// <summary>
    /// Ignore all messages from the websocket and
    /// wait for it to disconnect.
    /// </summary>
    /// <param name="socket">The websocket to wait for disconnect.</param>
    private static async Task WaitForDisconnected(WebSocket socket)
    {
        try
        {
            var buffer = new byte[1024];
            WebSocketReceiveResult? result = null;
            while (result is not { CloseStatus: not null })
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), SigtermUtil.GetCancellationToken()).ConfigureAwait(false);
            await socket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Application is shutting down - send a proper close frame
            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warning(e.Message);
        }
    }

    /// <summary>
    /// Send a message to a connected websocket.
    /// </summary>
    /// <param name="socket">The websocket to send the message to.</param>
    /// <param name="topic">The topic of the message to send</param>
    /// <param name="message">The message to send</param>
    private static Task<bool> SendMessage(
        WebSocket socket,
        SocketSendState state,
        WebsocketTopic topic,
        string message)
    {
        var topicMessage = new TopicMessage(topic, message);
        var bytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(topicMessage.ToJson()));
        return SendMessage(new SocketSendTarget(socket, state), bytes);
    }

    private static async Task<bool> SendMessage(SocketSendTarget target, ArraySegment<byte> message)
    {
        await target.State.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (target.State.IsDisabled) return false;
            var sent = await SendMessage(target.Socket, message).ConfigureAwait(false);
            if (!sent) target.State.Disable();
            return sent;
        }
        finally
        {
            target.State.Gate.Release();
        }
    }

    /// <summary>
    /// Send a message to a connected websocket.
    /// </summary>
    /// <param name="socket">The websocket to send the message to.</param>
    /// <param name="message">The message to send.</param>
    private static async Task<bool> SendMessage(WebSocket socket, ArraySegment<byte> message)
    {
        if (socket.State != WebSocketState.Open) return false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
            cts.CancelAfter(SendTimeout);
            await socket.SendAsync(message, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Timed out sending message to websocket.");
            return false;
        }
        catch (Exception e)
        {
            Log.Debug($"Failed to send message to websocket. {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Receive an authentication token from a connected websocket.
    /// With timeout after five seconds.
    /// </summary>
    /// <param name="socket">The websocket to receive from.</param>
    /// <returns>The authentication token. Or null if none provided.</returns>
    private static async Task<string?> ReceiveAuthToken(WebSocket socket)
    {
        try
        {
            var buffer = new byte[1024];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
            return result.MessageType == WebSocketMessageType.Text
                ? Encoding.UTF8.GetString(buffer, 0, result.Count)
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Close a websocket connection as unauthorized.
    /// </summary>
    /// <param name="socket">The websocket whose connection to close.</param>
    private static async Task CloseUnauthorizedConnection(WebSocket socket)
    {
        if (socket.State == WebSocketState.Open)
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class TopicMessage(WebsocketTopic topic, string message)
    {
        public string Topic { get; } = topic.Name;
        public string Message { get; } = message;
    }

    private sealed record SocketSendTarget(WebSocket Socket, SocketSendState State);

    private sealed class SocketSendState
    {
        private int _disabled;

        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool IsDisabled => Volatile.Read(ref _disabled) != 0;

        public void Disable()
        {
            Interlocked.Exchange(ref _disabled, 1);
        }
    }
}
