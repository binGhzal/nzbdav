using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;

namespace backend.Tests.Services;

public sealed class MaintenanceServiceRegistrationTests
{
    [Fact]
    public void AddMaintenanceLifecycleRegistersOneSharedHostedRunService()
    {
        var services = new ServiceCollection()
            .AddSingleton(new ConfigManager())
            .AddSingleton(new WebsocketManager());

        services.AddMaintenanceLifecycle();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<MaintenanceTaskExecutor>(provider.GetRequiredService<IMaintenanceTaskExecutor>());
        var runService = provider.GetRequiredService<MaintenanceRunService>();
        Assert.Contains(provider.GetServices<IHostedService>(), hosted => ReferenceEquals(runService, hosted));
    }
}
