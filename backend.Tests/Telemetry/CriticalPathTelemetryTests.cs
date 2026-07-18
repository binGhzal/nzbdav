using System.Text.Json.Serialization;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Api.SabControllers.GetFullStatus;
using NzbWebDAV.Api.SabControllers.GetStatus;
using NzbWebDAV.Telemetry;

namespace backend.Tests.Telemetry;

public sealed class CriticalPathTelemetryTests
{
    [Fact]
    public void SnapshotReportsLifetimeCountsFailuresAndNearestRankPercentiles()
    {
        var telemetry = new CriticalPathTelemetry(sampleCapacity: 256);
        for (var milliseconds = 1; milliseconds <= 100; milliseconds++)
        {
            telemetry.Record(
                CriticalPathStage.AddFileBlobWrite,
                TimeSpan.FromMilliseconds(milliseconds),
                failed: milliseconds is 25 or 75);
        }

        var snapshot = telemetry.GetSnapshot();

        Assert.Equal(100, snapshot.AddFileBlobWrite.Count);
        Assert.Equal(2, snapshot.AddFileBlobWrite.Failures);
        Assert.Equal(100, snapshot.AddFileBlobWrite.LatencySamples);
        Assert.Equal(95, snapshot.AddFileBlobWrite.P95Milliseconds);
        Assert.Equal(99, snapshot.AddFileBlobWrite.P99Milliseconds);
        Assert.Equal(0, snapshot.QueueParse.Count);
        Assert.Equal(0, snapshot.QueueParse.Failures);
        Assert.Equal(0, snapshot.QueueParse.LatencySamples);
        Assert.Equal(0, snapshot.QueueParse.P95Milliseconds);
        Assert.Equal(0, snapshot.QueueParse.P99Milliseconds);
        Assert.Equal(0, snapshot.QueuePar2Discovery.Count);
    }

    [Fact]
    public void SnapshotBoundsLatencySamplesWithoutResettingLifetimeCounters()
    {
        var telemetry = new CriticalPathTelemetry(sampleCapacity: 3);
        telemetry.Record(CriticalPathStage.QueueProcessors, TimeSpan.FromMilliseconds(1), failed: false);
        telemetry.Record(CriticalPathStage.QueueProcessors, TimeSpan.FromMilliseconds(2), failed: true);
        telemetry.Record(CriticalPathStage.QueueProcessors, TimeSpan.FromMilliseconds(3), failed: false);
        telemetry.Record(CriticalPathStage.QueueProcessors, TimeSpan.FromMilliseconds(100), failed: true);

        var stage = telemetry.GetSnapshot().QueueProcessors;

        Assert.Equal(4, stage.Count);
        Assert.Equal(2, stage.Failures);
        Assert.Equal(3, stage.LatencySamples);
        Assert.Equal(100, stage.P95Milliseconds);
        Assert.Equal(100, stage.P99Milliseconds);
    }

    [Fact]
    public void StatusMappingExposesEveryFixedCriticalPathStage()
    {
        var telemetry = new CriticalPathTelemetry(sampleCapacity: 8);
        foreach (var stage in Enum.GetValues<CriticalPathStage>())
            telemetry.Record(stage, TimeSpan.FromMilliseconds((int)stage + 1), failed: stage == CriticalPathStage.QueueCompletion);

        var status = CriticalPathStatus.FromSnapshot(telemetry.GetSnapshot());

        Assert.Equal(1, status.AddFileBlobWrite.Count);
        Assert.Equal(1, status.AddFileNzbScan.Count);
        Assert.Equal(1, status.AddFileAtomicCommit.Count);
        Assert.Equal(1, status.QueueParse.Count);
        Assert.Equal(1, status.QueueFirstSegmentDiscovery.Count);
        Assert.Equal(1, status.QueuePar2Discovery.Count);
        Assert.Equal(1, status.QueueProcessors.Count);
        Assert.Equal(1, status.QueueCompletion.Count);
        Assert.Equal(1, status.QueueCompletion.Failures);
    }

    [Theory]
    [InlineData(typeof(GetStatusResponse.StatusObject))]
    [InlineData(typeof(GetFullStatusResponse.FullStatusObject))]
    public void StatusResponseContractsExposeCriticalPathDiagnostics(Type responseType)
    {
        var property = responseType.GetProperty("CriticalPath");

        Assert.NotNull(property);
        Assert.Equal(typeof(CriticalPathStatus), property.PropertyType);
        Assert.Equal(
            "critical_path",
            property.GetCustomAttributes(typeof(JsonPropertyNameAttribute), inherit: false)
                .Cast<JsonPropertyNameAttribute>()
                .Single()
                .Name);
    }
}
