using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using Serilog;

namespace NzbWebDAV.Mount;

public sealed class DfsMountService(
    IServiceScopeFactory scopeFactory,
    ConfigManager configManager,
    UsenetStreamingClient usenetClient,
    StreamingConnectionLimiter streamingConnectionLimiter,
    ActiveStreamTracker activeStreamTracker,
    MountStatusProvider mountStatus
) : BackgroundService
{
    private DfsFileSystem? _fileSystem;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mountType = configManager.GetMountType();
        var mountDir = configManager.GetMountDir();

        if (mountType == "none")
        {
            mountStatus.SetDisabled(mountType, mountDir, "mount is disabled");
            return;
        }

        if (mountType != "dfs")
        {
            mountStatus.SetExternal(mountType, mountDir);
            return;
        }

        var unsupportedReason = GetNativeFuseUnsupportedReason();
        if (unsupportedReason is not null)
        {
            mountStatus.SetFailed("dfs", mountDir, unsupportedReason);
            return;
        }

        await Task.Yield();

        try
        {
            Directory.CreateDirectory(mountDir);
            mountStatus.SetStarting(mountDir);
            _fileSystem = new DfsFileSystem(
                mountDir,
                scopeFactory,
                configManager,
                usenetClient,
                streamingConnectionLimiter,
                activeStreamTracker,
                mountStatus)
            {
                Name = "nzbdav-dfs",
                MultiThreaded = true,
                EnableLargeReadRequests = true,
                EnableKernelCache = false,
                EnableDirectIO = false,
                AllowMountOverNonEmptyDirectory = false
            };

            await Task.Run(() => _fileSystem.Start(), CancellationToken.None).ConfigureAwait(false);
            if (!stoppingToken.IsCancellationRequested)
                mountStatus.SetStopped(mountDir);
        }
        catch (Exception ex)
        {
            mountStatus.SetFailed("dfs", mountDir, ex.Message);
            Log.Error(ex, "DFS mount failed for {MountDir}", mountDir);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _fileSystem?.Stop();
        }
        catch (Exception ex)
        {
            mountStatus.RecordFuseError($"DFS unmount failed: {ex.Message}");
            Log.Warning(ex, "DFS unmount failed");
        }

        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _fileSystem?.Dispose();
        base.Dispose();
    }

    public static string? GetNativeFuseUnsupportedReason()
    {
        return GetNativeFuseUnsupportedReason(
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
            RuntimeInformation.ProcessArchitecture);
    }

    public static string? GetNativeFuseUnsupportedReason(bool isLinux, Architecture architecture)
    {
        if (!isLinux)
            return "DFS mount requires Linux FUSE";

        if (architecture != Architecture.X64)
            return "DFS mount currently requires linux-x64 because Mono.Fuse.NETStandard 1.1.0 only ships a linux-x64 native helper";

        return null;
    }
}
