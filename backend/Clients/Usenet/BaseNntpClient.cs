using System.Reflection;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using UsenetSharp.Clients;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This class has four responsibilities that differ from the underlying UsenetClient implementation
///   1. throw `CouldNotConnectToUsenetException` after any connection error.
///   2. throw `CouldNotLoginToUsenetException` after any login error.
///   3. Provide yenc-decoded data for articles retrieved through article/body commands.
///   4. throw `UsenetArticleNotFound` when articles do not exist, within article/body/head commands.
/// </summary>
public class BaseNntpClient : NntpClient
{
    private readonly UsenetClient _client = new();
    protected override bool SupportsPipelinedSegmentChecks => true;
    private static readonly FieldInfo CommandLockField = GetUsenetClientField("_commandLock");
    private static readonly FieldInfo ReaderField = GetUsenetClientField("_reader");
    private static readonly FieldInfo WriterField = GetUsenetClientField("_writer");
    private static readonly FieldInfo CancellationSourceField = GetUsenetClientField("_cts");
    private static readonly MethodInfo ThrowIfNotConnectedMethod = GetUsenetClientMethod("ThrowIfNotConnected");
    private static readonly MethodInfo ThrowIfUnhealthyMethod = GetUsenetClientMethod("ThrowIfUnhealthy");
    private static readonly MethodInfo CommandLockWaitAsyncMethod = CommandLockField.FieldType.GetMethod(
        "WaitAsync",
        BindingFlags.Instance | BindingFlags.Public,
        [typeof(CancellationToken)]) ?? throw new MissingMethodException("UsenetSharp command lock WaitAsync was not found.");
    private static readonly MethodInfo CommandLockReleaseMethod = CommandLockField.FieldType.GetMethod(
        "Release",
        BindingFlags.Instance | BindingFlags.Public,
        Type.EmptyTypes) ?? throw new MissingMethodException("UsenetSharp command lock Release was not found.");
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectTimeout =
        int.TryParse(Environment.GetEnvironmentVariable("NNTP_CONNECT_TIMEOUT_SECONDS"), out var timeoutSeconds)
        && timeoutSeconds > 0
            ? TimeSpan.FromSeconds(timeoutSeconds)
            : TimeSpan.FromSeconds(15);

    public override async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ConnectTimeout);
        try
        {
            await _client.ConnectAsync(host, port, useSsl, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CouldNotConnectToUsenetException(
                $"Connection to {host}:{port} timed out after {ConnectTimeout.TotalSeconds:F0}s.");
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            const string message = "Could not connect to usenet host. Check connection settings.";
            throw new CouldNotConnectToUsenetException(message, e);
        }
    }

    public override async Task<UsenetResponse> AuthenticateAsync
    (
        string user,
        string pass,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await _client.AuthenticateAsync(user, pass, cancellationToken);
            if (!response.Success)
            {
                var message = $"Could not login to usenet host: {response.ResponseMessage}";
                throw new CouldNotLoginToUsenetException(message);
            }

            return response;
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            throw new CouldNotLoginToUsenetException("Could not login to usenet host.", e);
        }
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return _client.StatAsync(segmentId, cancellationToken);
    }

    public override Task<IReadOnlyList<UsenetStatResponse>> StatPipelinedAsync
    (
        IReadOnlyList<string> segmentIds,
        CancellationToken cancellationToken
    )
    {
        return RunStatPipelineAsync(segmentIds, cancellationToken);
    }

    private async Task<IReadOnlyList<UsenetStatResponse>> RunStatPipelineAsync
    (
        IReadOnlyList<string> segmentIds,
        CancellationToken cancellationToken
    )
    {
        if (segmentIds.Count == 0) return [];

        var commandLock = CommandLockField.GetValue(_client)
                          ?? throw new InvalidOperationException("UsenetSharp command lock is unavailable.");
        var waitTask = (Task?)CommandLockWaitAsyncMethod.Invoke(commandLock, [cancellationToken])
                       ?? throw new InvalidOperationException("UsenetSharp command lock wait did not return a task.");
        await waitTask.ConfigureAwait(false);

        try
        {
            InvokeUsenetClientGuard(ThrowIfUnhealthyMethod, _client);
            InvokeUsenetClientGuard(ThrowIfNotConnectedMethod, _client);

            var writer = (StreamWriter?)WriterField.GetValue(_client)
                         ?? throw new InvalidOperationException("Usenet connection writer is unavailable.");
            var reader = (StreamReader?)ReaderField.GetValue(_client)
                         ?? throw new InvalidOperationException("Usenet connection reader is unavailable.");
            var connectionCts = (CancellationTokenSource?)CancellationSourceField.GetValue(_client);
            using var timeoutCts = connectionCts is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionCts.Token);
            timeoutCts.CancelAfter(CommandTimeout);

            for (var i = 0; i < segmentIds.Count; i++)
            {
                var command = $"STAT <{segmentIds[i]}>";
                await writer.WriteLineAsync(command.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            }

            var responses = new UsenetStatResponse[segmentIds.Count];
            for (var i = 0; i < segmentIds.Count; i++)
            {
                var line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)
                           ?? throw new UsenetProtocolException("Invalid NNTP Response: <null>");
                var responseCode = ParseResponseCode(line);
                responses[i] = new UsenetStatResponse
                {
                    ResponseCode = responseCode,
                    ResponseMessage = line,
                    ArticleExists = responseCode == (int)UsenetResponseType.ArticleExists
                };
            }

            return responses;
        }
        finally
        {
            CommandLockReleaseMethod.Invoke(commandLock, null);
        }
    }

    private static FieldInfo GetUsenetClientField(string name)
    {
        return typeof(UsenetClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new MissingFieldException(typeof(UsenetClient).FullName, name);
    }

    private static MethodInfo GetUsenetClientMethod(string name)
    {
        return typeof(UsenetClient).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
               ?? throw new MissingMethodException(typeof(UsenetClient).FullName, name);
    }

    private static void InvokeUsenetClientGuard(MethodInfo method, object target)
    {
        try
        {
            method.Invoke(target, null);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static int ParseResponseCode(string? response)
    {
        if (string.IsNullOrEmpty(response) || response.Length < 3)
            throw new UsenetProtocolException($"Invalid NNTP Response: {response}");

        if (int.TryParse(response.AsSpan(0, 3), out var responseCode))
            return responseCode;

        throw new UsenetProtocolException($"Invalid NNTP Response: {response}");
    }

    public override async Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        var headResponse = await _client.HeadAsync(segmentId, cancellationToken);

        if (headResponse.ResponseType != UsenetResponseType.ArticleRetrievedHeadFollows)
            throw new UsenetArticleNotFoundException(segmentId);

        return new UsenetHeadResponse()
        {
            SegmentId = headResponse.SegmentId,
            ResponseCode = headResponse.ResponseCode,
            ResponseMessage = headResponse.ResponseMessage,
            ArticleHeaders = headResponse.ArticleHeaders!
        };
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedBodyAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        var bodyResponse = await _client.BodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);

        if (bodyResponse.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            throw new UsenetArticleNotFoundException(segmentId);

        return new UsenetDecodedBodyResponse()
        {
            SegmentId = bodyResponse.SegmentId,
            ResponseCode = bodyResponse.ResponseCode,
            ResponseMessage = bodyResponse.ResponseMessage,
            Stream = new YencStream(bodyResponse.Stream!),
        };
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return DecodedArticleAsync(segmentId, onConnectionReadyAgain: null, cancellationToken);
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        var articleResponse = await _client.ArticleAsync(segmentId, onConnectionReadyAgain, cancellationToken);

        if (articleResponse.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            throw new UsenetArticleNotFoundException(segmentId);

        return new UsenetDecodedArticleResponse()
        {
            SegmentId = articleResponse.SegmentId,
            ResponseCode = articleResponse.ResponseCode,
            ResponseMessage = articleResponse.ResponseMessage,
            ArticleHeaders = articleResponse.ArticleHeaders!,
            Stream = new YencStream(articleResponse.Stream!),
        };
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return _client.DateAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
