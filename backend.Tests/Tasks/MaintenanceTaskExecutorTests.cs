using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Tasks;
using NzbWebDAV.Websocket;
using backend.Tests.Services;

namespace backend.Tests.Tasks;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class MaintenanceTaskExecutorTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public MaintenanceTaskExecutorTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(MaintenanceRunKind.RemoveUnlinkedFiles)]
    [InlineData(MaintenanceRunKind.RemoveUnlinkedFilesDryRun)]
    [InlineData(MaintenanceRunKind.ConvertStrmToSymlinks)]
    [InlineData(MaintenanceRunKind.RecreateStrmFiles)]
    public async Task ExecuteAsync_MapsEveryPersistedKindToARealMaintenanceTask(MaintenanceRunKind kind)
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var libraryPath = _fixture.CreateLibraryDirectory();
        var configManager = _fixture.CreateConfigManager(libraryPath);
        var reports = new List<MaintenanceTaskProgress>();
        var executor = new MaintenanceTaskExecutor(configManager, new WebsocketManager());

        await executor.ExecuteAsync(
            kind,
            progress =>
            {
                reports.Add(progress);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.NotEmpty(reports);
    }
}
