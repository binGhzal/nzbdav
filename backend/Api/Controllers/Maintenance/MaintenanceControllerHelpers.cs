using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Security;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Maintenance;

internal static class MaintenanceControllerHelpers
{
    public static IActionResult MethodNotAllowed(BaseApiController controller, string method)
    {
        return controller.Failure(
            StatusCodes.Status405MethodNotAllowed,
            PublicFailureContract.MethodNotAllowed());
    }

    public static async Task<IActionResult> StartRunAsync(
        BaseApiController controller,
        MaintenanceRunService service,
        MaintenanceRunKind kind,
        CancellationToken cancellationToken)
    {
        var result = await service.TryStartRunAsync(kind, "manual", cancellationToken).ConfigureAwait(false);
        if (!result.Started)
        {
            return controller.Failure(
                StatusCodes.Status409Conflict,
                PublicFailureContract.MaintenanceRunActive(),
                new MaintenanceRunConflictResponse
                {
                    ActiveRun = MaintenanceRunDto.FromModel(result.Run),
                });
        }

        var dto = MaintenanceRunDto.FromModel(result.Run);
        return controller.Accepted($"/api/maintenance/runs/{dto.Id}", new MaintenanceRunResponse { Run = dto });
    }

    public static bool TryReadKind(HttpContext context, out MaintenanceRunKind? kind)
    {
        var rawKind = context.Request.Query["kind"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawKind))
        {
            kind = null;
            return true;
        }

        if (MaintenanceRunApiValues.TryParseKind(rawKind, out var parsed))
        {
            kind = parsed;
            return true;
        }

        kind = null;
        return false;
    }
}
