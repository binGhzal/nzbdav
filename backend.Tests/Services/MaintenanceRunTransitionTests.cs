using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using backend.Tests.Services;

namespace backend.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class MaintenanceRunTransitionTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public MaintenanceRunTransitionTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(true, MaintenanceRunStatus.Cancelled)]
    [InlineData(false, MaintenanceRunStatus.Completed)]
    public async Task AtomicCancellationAndCompletionInterleavingsCannotOverwriteTerminalState(
        bool cancellationFirst,
        MaintenanceRunStatus expected)
    {
        var id = await SeedRunningRunAsync();
        await using var cancellationContext = await _fixture.CreateMigratedContextAsync();
        await using var completionContext = await _fixture.CreateMigratedContextAsync();
        _ = await cancellationContext.MaintenanceRuns.SingleAsync(x => x.Id == id);
        _ = await completionContext.MaintenanceRuns.SingleAsync(x => x.Id == id);
        var now = DateTimeOffset.UtcNow;

        if (cancellationFirst)
        {
            await MaintenanceRunTransitions.RequestCancellationAsync(
                cancellationContext, id, now, CancellationToken.None);
            await MaintenanceRunTransitions.FinishAsync(
                completionContext, id, MaintenanceRunStatus.Completed, null, now.AddSeconds(1), CancellationToken.None);
        }
        else
        {
            await MaintenanceRunTransitions.FinishAsync(
                completionContext, id, MaintenanceRunStatus.Completed, null, now, CancellationToken.None);
            await MaintenanceRunTransitions.RequestCancellationAsync(
                cancellationContext, id, now.AddSeconds(1), CancellationToken.None);
        }

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var saved = await assertionContext.MaintenanceRuns.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(expected, saved.Status);
        Assert.Null(saved.ActiveSlot);
        Assert.NotNull(saved.CompletedAt);
    }

    [Fact]
    public async Task InterruptedRunCannotBeOverwrittenByStaleCompletion()
    {
        var id = await SeedRunningRunAsync();
        await using var interruptionContext = await _fixture.CreateMigratedContextAsync();
        await using var staleCompletionContext = await _fixture.CreateMigratedContextAsync();
        _ = await staleCompletionContext.MaintenanceRuns.SingleAsync(x => x.Id == id);
        var now = DateTimeOffset.UtcNow;

        await MaintenanceRunTransitions.InterruptActiveAsync(
            interruptionContext, id, now, "Interrupted for test.", CancellationToken.None);
        await MaintenanceRunTransitions.FinishAsync(
            staleCompletionContext,
            id,
            MaintenanceRunStatus.Completed,
            null,
            now.AddSeconds(1),
            CancellationToken.None);

        await using var assertionContext = await _fixture.CreateMigratedContextAsync();
        var saved = await assertionContext.MaintenanceRuns.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(MaintenanceRunStatus.Interrupted, saved.Status);
        Assert.Equal("Interrupted for test.", saved.Message);
        Assert.Null(saved.ActiveSlot);
    }

    private async Task<Guid> SeedRunningRunAsync()
    {
        await using var dbContext = await _fixture.ResetAndCreateMigratedContextAsync();
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var run = new MaintenanceRun
        {
            Id = Guid.NewGuid(),
            Kind = MaintenanceRunKind.RecreateStrmFiles,
            Status = MaintenanceRunStatus.Running,
            ActiveSlot = 1,
            RequestedBy = "manual",
            CreatedAt = now,
            StartedAt = now,
            UpdatedAt = now,
            ProgressCurrent = 0,
            Message = "Running.",
        };
        dbContext.MaintenanceRuns.Add(run);
        await dbContext.SaveChangesAsync();
        return run.Id;
    }
}
