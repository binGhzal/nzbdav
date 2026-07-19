using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Security;

internal enum PublicFailureKind
{
    InvalidRequest,
    AuthenticationRequired,
    InternalError,
    ClientClosedRequest,
    ContentUnavailable,
    ContentTemporarilyUnavailable,
    ContentRangeUnavailable,
    ResourceNotFound,
    MethodNotAllowed,
    MaintenanceRunActive,
    EndpointDisabled,
    ConnectionTimeout,
    ArrConnectionFailure,
    UsenetConnectionFailure,
    RcloneConnectionFailure,
}

public sealed class PublicFailure
{
    private PublicFailure(string code, string message)
    {
        Code = code;
        Message = message;
        CorrelationId = Guid.NewGuid().ToString("N");
    }

    public string Code { get; }
    public string Message { get; }
    public string CorrelationId { get; }

    internal static PublicFailure FromKnownKind(PublicFailureKind kind) => kind switch
    {
        PublicFailureKind.InvalidRequest => new("invalid_request", "The request is invalid."),
        PublicFailureKind.AuthenticationRequired => new("authentication_required", "Authentication is required."),
        PublicFailureKind.InternalError => new("internal_error", "The request could not be completed."),
        PublicFailureKind.ClientClosedRequest => new("client_closed_request", "The client closed the request."),
        PublicFailureKind.ContentUnavailable => new("content_unavailable", "The requested content is unavailable."),
        PublicFailureKind.ContentTemporarilyUnavailable =>
            new("content_temporarily_unavailable", "The requested content is temporarily unavailable."),
        PublicFailureKind.ContentRangeUnavailable =>
            new("content_range_unavailable", "The requested content range is unavailable."),
        PublicFailureKind.ResourceNotFound => new("resource_not_found", "The requested resource was not found."),
        PublicFailureKind.MethodNotAllowed => new("method_not_allowed", "The request method is not allowed."),
        PublicFailureKind.MaintenanceRunActive =>
            new("maintenance_run_active", "A maintenance run is already active."),
        PublicFailureKind.EndpointDisabled => new("endpoint_disabled", "This endpoint is disabled."),
        PublicFailureKind.ConnectionTimeout => new("connection_timeout", "The connection test timed out."),
        PublicFailureKind.ArrConnectionFailure =>
            new("arr_connection_failure", "ARR connection test failed."),
        PublicFailureKind.UsenetConnectionFailure =>
            new("usenet_connection_failure", "Usenet connection test failed."),
        PublicFailureKind.RcloneConnectionFailure =>
            new("rclone_connection_failure", "Rclone connection test failed."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

public static class PublicFailureContract
{
    public const string CorrelationHeaderName = "X-Correlation-ID";
    public const string ErrorCodeHeaderName = "X-Error-Code";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static PublicFailure InvalidRequest() => Create(PublicFailureKind.InvalidRequest);

    public static PublicFailure AuthenticationRequired() =>
        Create(PublicFailureKind.AuthenticationRequired);

    public static PublicFailure InternalError() =>
        Create(PublicFailureKind.InternalError);

    public static PublicFailure ClientClosedRequest() =>
        Create(PublicFailureKind.ClientClosedRequest);

    public static PublicFailure ContentUnavailable() =>
        Create(PublicFailureKind.ContentUnavailable);

    public static PublicFailure ContentTemporarilyUnavailable() =>
        Create(PublicFailureKind.ContentTemporarilyUnavailable);

    public static PublicFailure ContentRangeUnavailable() =>
        Create(PublicFailureKind.ContentRangeUnavailable);

    public static PublicFailure ResourceNotFound() =>
        Create(PublicFailureKind.ResourceNotFound);

    public static PublicFailure MethodNotAllowed() =>
        Create(PublicFailureKind.MethodNotAllowed);

    public static PublicFailure MaintenanceRunActive() =>
        Create(PublicFailureKind.MaintenanceRunActive);

    public static PublicFailure EndpointDisabled() =>
        Create(PublicFailureKind.EndpointDisabled);

    public static PublicFailure ConnectionTimeout() =>
        Create(PublicFailureKind.ConnectionTimeout);

    public static PublicFailure ArrConnectionFailure() =>
        Create(PublicFailureKind.ArrConnectionFailure);

    public static PublicFailure UsenetConnectionFailure() =>
        Create(PublicFailureKind.UsenetConnectionFailure);

    public static PublicFailure RcloneConnectionFailure() =>
        Create(PublicFailureKind.RcloneConnectionFailure);

    private static PublicFailure Create(PublicFailureKind kind) => PublicFailure.FromKnownKind(kind);

    public static void ApplyHeaders(HttpResponse response, PublicFailure failure)
    {
        response.Headers[CorrelationHeaderName] = failure.CorrelationId;
        response.Headers[ErrorCodeHeaderName] = failure.Code;
    }

    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        PublicFailure failure,
        bool includeBody = true)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        ApplyHeaders(context.Response, failure);
        if (!includeBody || HttpMethods.IsHead(context.Request.Method)) return;

        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(
            new PublicFailurePayload
            {
                Error = failure.Message,
                Code = failure.Code,
                CorrelationId = failure.CorrelationId,
            },
            JsonOptions)).ConfigureAwait(false);
    }

    private sealed class PublicFailurePayload
    {
        public bool Status => false;
        public required string Error { get; init; }
        public required string Code { get; init; }

        [JsonPropertyName("correlation_id")]
        public required string CorrelationId { get; init; }
    }
}
