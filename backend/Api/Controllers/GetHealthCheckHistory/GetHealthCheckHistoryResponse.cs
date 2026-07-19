using NzbWebDAV.Database.Models;
using NzbWebDAV.Security;

namespace NzbWebDAV.Api.Controllers.GetHealthCheckHistory;

public class GetHealthCheckHistoryResponse : BaseApiResponse
{
    public required List<HealthCheckStat> Stats { get; init; }
    public required List<HealthCheckResult> Items { get; init; }

    public static HealthCheckResult Project(HealthCheckResult item)
    {
        return new HealthCheckResult
        {
            Id = item.Id,
            CreatedAt = item.CreatedAt,
            DavItemId = item.DavItemId,
            Path = item.Path,
            Result = item.Result,
            RepairStatus = item.RepairStatus,
            Message = item.Result == HealthCheckResult.HealthResult.Healthy
                ? PublicDiagnosticContract.HealthHealthy
                : PublicDiagnosticContract.HealthDetail(item.Message, item.RepairStatus),
        };
    }
}
