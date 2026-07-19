using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Extensions;
using NzbWebDAV.Security;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers;

public abstract class BaseApiController : ControllerBase
{
    protected virtual bool RequiresAuthentication => true;
    protected abstract Task<IActionResult> HandleRequest();

    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> HandleApiRequest()
    {
        try
        {
            if (RequiresAuthentication)
            {
                var apiKey = HttpContext.GetInternalRequestApiKey();
                if (apiKey == null)
                    throw new UnauthorizedAccessException("API Key Required");
                if (apiKey != EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"))
                    throw new UnauthorizedAccessException("API Key Incorrect");
            }

            return await HandleRequest().ConfigureAwait(false);
        }
        catch (BadHttpRequestException)
        {
            return Failure(StatusCodes.Status400BadRequest, PublicFailureContract.InvalidRequest());
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(StatusCodes.Status401Unauthorized, PublicFailureContract.AuthenticationRequired());
        }
        catch (Exception e) when (e is not OperationCanceledException ||
                                  !HttpContext.RequestAborted.IsCancellationRequested)
        {
            return Failure(StatusCodes.Status500InternalServerError, PublicFailureContract.InternalError());
        }
    }

    protected internal IActionResult Failure(
        int statusCode,
        PublicFailure failure,
        BaseApiResponse? response = null)
    {
        PublicFailureContract.ApplyHeaders(HttpContext.Response, failure);
        response ??= new BaseApiResponse();
        response.Status = false;
        response.Error = failure.Message;
        response.Code = failure.Code;
        response.CorrelationId = failure.CorrelationId;
        return statusCode switch
        {
            StatusCodes.Status400BadRequest => BadRequest(response),
            StatusCodes.Status401Unauthorized => Unauthorized(response),
            StatusCodes.Status404NotFound => NotFound(response),
            StatusCodes.Status409Conflict => Conflict(response),
            _ => StatusCode(statusCode, response),
        };
    }

    protected IActionResult CompatibilityFailure(
        int statusCode,
        PublicFailure failure,
        BaseApiResponse response)
    {
        PublicFailureContract.ApplyHeaders(HttpContext.Response, failure);
        response.Error = failure.Message;
        response.Code = failure.Code;
        response.CorrelationId = failure.CorrelationId;
        return StatusCode(statusCode, response);
    }

    protected T CompatibilityFailure<T>(PublicFailure failure, T response)
        where T : BaseApiResponse
    {
        PublicFailureContract.ApplyHeaders(HttpContext.Response, failure);
        response.Error = failure.Message;
        response.Code = failure.Code;
        response.CorrelationId = failure.CorrelationId;
        return response;
    }
}
