using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Config;
using NzbWebDAV.Mount;
using NzbWebDAV.Streams.Caching;

namespace NzbWebDAV.Api.Controllers.Mount;

[ApiController]
[Route("api/mount/status")]
public sealed class MountStatusController(
    MountStatusProvider mountStatusProvider,
    ConfigManager configManager
) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        var cacheSnapshot = SparseSegmentCacheManager.Shared.GetSnapshot(configManager.GetSparseSegmentCacheOptions());
        return Task.FromResult<IActionResult>(Ok(new MountStatusResponse
        {
            Mount = MountDiagnosticStatus.FromSnapshot(mountStatusProvider.GetSnapshot(cacheSnapshot))
        }));
    }
}
