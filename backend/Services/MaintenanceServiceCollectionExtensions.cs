using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Tasks;

namespace NzbWebDAV.Services;

public static class MaintenanceServiceCollectionExtensions
{
    public static IServiceCollection AddMaintenanceLifecycle(this IServiceCollection services)
    {
        services.AddSingleton<IMaintenanceTaskExecutor, MaintenanceTaskExecutor>();
        services.AddSingleton<MaintenanceRunService>();
        services.AddHostedService(serviceProvider =>
            serviceProvider.GetRequiredService<MaintenanceRunService>());
        return services;
    }
}
