using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Auth;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Coordination;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Hosting;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Mount;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace NzbWebDAV;

class Program
{
    private const long DefaultMaxStartupVacuumBytes = 1L * 1024 * 1024 * 1024;

    static async Task Main(string[] args)
    {
        EnvironmentUtil.LoadDotEnvFile();

        var role = NzbdavRoleResolver.Resolve(EnvironmentUtil.GetVariable("NZBDAV_ROLE"));
        if (role != NzbdavRole.All)
        {
            throw new InvalidOperationException(
                $"NZBDAV_ROLE '{role}' is defined but not executable until its service implementation is installed.");
        }

        ConfigureThreadPool();

        // Initialize logger
        var defaultLevel = LogEventLevel.Information;
        var envLevel = EnvironmentUtil.GetVariable("LOG_LEVEL");
        var level = Enum.TryParse<LogEventLevel>(envLevel, true, out var parsed) ? parsed : defaultLevel;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("NWebDAV", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", LogEventLevel.Error)
            .WriteTo.Console(theme: AnsiConsoleTheme.Code)
            .CreateLogger();

        // Block upgrades to version 0.6.x
        BlockUpgradesToV06X();

        // initialize database
        await using var databaseContext = new DavDatabaseContext();

        // run database migration, if necessary.
        if (args.Contains("--db-migration"))
        {
            var argIndex = args.ToList().IndexOf("--db-migration");
            var targetMigration = args.Length > argIndex + 1 ? args[argIndex + 1] : null;
            await databaseContext.Database
                .MigrateAsync(targetMigration, SigtermUtil.GetCancellationToken())
                .ConfigureAwait(false);
            await PerformDatabaseVacuumIfEnabled();
            return;
        }

        if (TryGetArgumentValue(args, "--db-export-json", out var exportPath))
        {
            await DatabaseTransferService
                .ExportJsonAsync(databaseContext, exportPath, SigtermUtil.GetCancellationToken())
                .ConfigureAwait(false);
            return;
        }

        if (TryGetArgumentValue(args, "--db-import-json", out var importPath))
        {
            var replace = args.Contains("--replace");
            var result = await DatabaseTransferService
                .ImportJsonAsync(databaseContext, importPath, replace, SigtermUtil.GetCancellationToken())
                .ConfigureAwait(false);
            Log.Information("Imported {ImportedRows} database rows.", result.ImportedRows);
            return;
        }

        // initialize the config-manager
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);

        // initialize rclone client
        RcloneClient.Initialize(configManager);

        // initialize websocket-manager
        var websocketManager = new WebsocketManager();

        // initialize webapp
        var builder = WebApplication.CreateBuilder(args);
        var maxRequestBodySize = EnvironmentUtil.GetLongVariable("MAX_REQUEST_BODY_SIZE") ?? 100 * 1024 * 1024;
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodySize);
        builder.Host.UseSerilog();
        builder.Services.AddControllers();
        builder.Services.AddHealthChecks();
        builder.Services
            .AddWebdavBasicAuthentication(configManager)
            .AddSingleton(configManager)
            .AddSingleton(websocketManager)
            .AddSingleton<ActiveStreamTracker>()
            .AddSingleton<MountStatusProvider>()
            .AddSingleton<StreamingConnectionLimiter>()
            .AddSingleton<QueueWorkLaneCoordinator>()
            .AddOptions<WorkerLeaseOptions>()
            .Validate(WorkerLeaseOptions.IsValid, WorkerLeaseOptions.ValidationMessage)
            .ValidateOnStart()
            .Services
            .AddSingleton<IWorkerLaneCapacityPolicy, ConfigWorkerLaneCapacityPolicy>()
            .AddSingleton<IWorkerJobCoordinator, DatabaseWorkerJobCoordinator>()
            .AddSingleton<ArrDownloadReportService>()
            .AddSingleton<ArrOperationsService>()
            .AddSingleton<UsenetStreamingClient>()
            .AddSingleton<QueueManager>()
            .AddSingleton<HealthCheckService>()
            .AddHostedService<ContentIndexSnapshotWriterService>()
            .AddHostedService<ContentIndexRecoveryService>()
            .AddHostedService(sp => sp.GetRequiredService<HealthCheckService>())
            .AddHostedService<ArrMonitoringService>()
            .AddHostedService<ArrCorrelationService>()
            .AddHostedService<ArrPriorityService>()
            .AddHostedService<ArrSearchNudgeService>()
            .AddHostedService<BlobCleanupService>()
            .AddHostedService<NzbBlobCleanupService>()
            .AddHostedService<HistoryCleanupService>()
            .AddHostedService<DavCleanupService>()
            .AddHostedService<RcloneInvalidationService>()
            .AddHostedService<DfsMountService>()
            .AddHostedService<UsenetFileToBlobstoreMigrationService>()
            .AddHostedService<RemoveOrphanedFilesSchedulerService>()
            .AddScoped<DavDatabaseContext>()
            .AddScoped<DavDatabaseClient>()
            .AddScoped<DatabaseStore>()
            .AddScoped<IStore, DatabaseStore>()
            .AddScoped<GetAndHeadHandlerPatch>()
            .AddScoped<SabApiController>()
            .AddNWebDav(opts =>
            {
                opts.Handlers["GET"] = typeof(GetAndHeadHandlerPatch);
                opts.Handlers["HEAD"] = typeof(GetAndHeadHandlerPatch);
                opts.Filter = opts.GetFilter();
                opts.RequireAuthentication = !WebApplicationAuthExtensions
                    .IsWebdavAuthDisabled();
            });

        // run
        var app = builder.Build();
        app.UseMiddleware<ExceptionMiddleware>();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.MapHealthChecks("/health");
        app.Map("/ws", websocketManager.HandleRoute);
        app.MapControllers();
        app.UseWebdavBasicAuthentication();
        app.UseNWebDav();
        app.Lifetime.ApplicationStopping.Register(SigtermUtil.Cancel);
        await app.RunAsync().ConfigureAwait(false);
    }

    private static void BlockUpgradesToV06X()
    {
        // If the database file doesn't exist.
        // Then this is a new installation.
        // Do nothing.
        if (!DavDatabaseContext.IsSqlite) return;
        if (!File.Exists(DavDatabaseContext.DatabaseFilePath)) return;

        // If there is no pending database migration,
        // Then the user has already upgraded.
        // Do nothing.
        using var databaseContext = new DavDatabaseContext();
        const string migration = "20260226053712_Add-NzbBlobId-And-NzbNames";
        var hasPendingMigration = databaseContext.Database.GetPendingMigrations().Contains(migration);
        if (!hasPendingMigration) return;

        // If the user has set the UPGRADE env variable,
        // Then they have acknowledged the upgrade message.
        // Do nothing.
        var upgradeEnv = EnvironmentUtil.GetVariable("UPGRADE");
        if (upgradeEnv == "0.6.0") return;

        // Otherwise, display the upgrade message, and exit.
        Console.WriteLine(
            """
            Version 0.6.0 of nzbdav is NOT backwards compatible.
            You can upgrade, but you won't be able to downgrade.
            Make a backup of your entire /config directory prior to upgrading.
            The only way to downgrade back to a previous version is by restoring this backup.
            To acknowledge this message and continue upgrading, set the env variable UPGRADE=0.6.0
            """
        );
        Environment.Exit(1);
    }

    private static void ConfigureThreadPool()
    {
        var configuredMinWorkerThreads = EnvironmentUtil.GetLongVariable("NZBDAV_THREADPOOL_MIN_WORKERS");
        var configuredMinIoThreads = EnvironmentUtil.GetLongVariable("NZBDAV_THREADPOOL_MIN_IO");
        if (configuredMinWorkerThreads == null && configuredMinIoThreads == null) return;

        ThreadPool.GetMinThreads(out var currentMinWorkers, out var currentMinIo);
        var minWorkers = configuredMinWorkerThreads is > 0
            ? (int)Math.Min(configuredMinWorkerThreads.Value, int.MaxValue)
            : currentMinWorkers;
        var minIo = configuredMinIoThreads is > 0
            ? (int)Math.Min(configuredMinIoThreads.Value, int.MaxValue)
            : currentMinIo;
        ThreadPool.SetMinThreads(minWorkers, minIo);
    }

    private static async Task PerformDatabaseVacuumIfEnabled()
    {
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);
        if (configManager.IsDatabaseStartupVacuumEnabled())
        {
            if (!DavDatabaseContext.IsSqlite)
            {
                Console.WriteLine("Skipping database vacuum because it is only supported by the SQLite provider.");
                return;
            }

            var forceVacuum = EnvironmentUtil.IsVariableTrue("NZBDAV_FORCE_STARTUP_VACUUM");
            var maxStartupVacuumBytes = Math.Max(
                1,
                EnvironmentUtil.GetLongVariable("NZBDAV_STARTUP_VACUUM_MAX_BYTES")
                ?? DefaultMaxStartupVacuumBytes);
            var sqliteBytes = GetSqliteDatabaseBytes();
            if (ShouldSkipStartupVacuum(sqliteBytes, maxStartupVacuumBytes, forceVacuum))
            {
                Console.WriteLine(
                    "Skipping database vacuum because SQLite database files total "
                    + $"{FormatBytes(sqliteBytes)}, above the startup vacuum limit "
                    + $"{FormatBytes(maxStartupVacuumBytes)}. "
                    + "Run manual maintenance or set NZBDAV_FORCE_STARTUP_VACUUM=true to override.");
                return;
            }

            Console.Write("Performing database vacuum...");
            await using var databaseContext = new DavDatabaseContext();
            await databaseContext.Database.ExecuteSqlRawAsync("VACUUM;");
            Console.WriteLine("Done.");
        }
    }

    private static bool ShouldSkipStartupVacuum(long sqliteBytes, long maxStartupVacuumBytes, bool forceVacuum)
    {
        return !forceVacuum && sqliteBytes > maxStartupVacuumBytes;
    }

    private static long GetSqliteDatabaseBytes()
    {
        var paths = new[]
        {
            DavDatabaseContext.DatabaseFilePath,
            $"{DavDatabaseContext.DatabaseFilePath}-wal",
            $"{DavDatabaseContext.DatabaseFilePath}-shm"
        };

        var total = 0L;
        foreach (var path in paths)
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Exists)
                total += fileInfo.Length;
        }

        return total;
    }

    private static string FormatBytes(long bytes)
    {
        const double kib = 1024d;
        if (bytes < kib) return $"{bytes} B";
        if (bytes < kib * kib) return $"{bytes / kib:0.##} KiB";
        if (bytes < kib * kib * kib) return $"{bytes / kib / kib:0.##} MiB";
        return $"{bytes / kib / kib / kib:0.##} GiB";
    }

    private static bool TryGetArgumentValue(string[] args, string name, out string value)
    {
        value = "";
        var index = Array.IndexOf(args, name);
        if (index < 0) return false;
        if (args.Length <= index + 1 || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"{name} requires a path argument.");
        value = args[index + 1];
        return true;
    }
}
