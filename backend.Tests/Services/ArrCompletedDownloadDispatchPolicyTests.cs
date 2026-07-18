using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

public sealed class ArrCompletedDownloadDispatchPolicyTests
{
    private const string DownloadId = "SAB-job-Aa19";

    [Fact]
    public void ExactSonarrMatchReturnsTypedRequestAndPreservesWindowsPath()
    {
        const string path = @"Z:\arr mapped\Show.Name.S01E01";
        var queue = SonarrQueue(
            SonarrRecord(
                DownloadId,
                path,
                seriesId: 41,
                episodeId: 42,
                numericDownloadClientId: 999));

        var decision = EvaluateSonarr(
            Correlation(DownloadId, seriesId: 41, episodeId: 42),
            queue);

        Assert.True(decision.IsDirectScan);
        Assert.Null(decision.FallbackReason);
        Assert.Null(decision.FallbackReasonCode);
        var request = Assert.IsType<ArrCompletedDownloadDirectScanRequest>(decision.DirectScan);
        Assert.Equal(ArrCompletedDownloadClientKind.Sonarr, request.ClientKind);
        Assert.Equal("DownloadedEpisodesScan", request.CommandName);
        Assert.Equal(DownloadId, request.DownloadClientId);
        Assert.Equal(path, request.Path);
    }

    [Fact]
    public void ExactRadarrMatchReturnsTypedRequestAndPreservesPosixPath()
    {
        const string path = "/remote/arr mapped/Movie.Name.2026";
        var queue = RadarrQueue(RadarrRecord(DownloadId, path, movieId: 73));

        var decision = EvaluateRadarr(Correlation(DownloadId, movieId: 73), queue);

        Assert.True(decision.IsDirectScan);
        Assert.Null(decision.FallbackReason);
        var request = Assert.IsType<ArrCompletedDownloadDirectScanRequest>(decision.DirectScan);
        Assert.Equal(ArrCompletedDownloadClientKind.Radarr, request.ClientKind);
        Assert.Equal("DownloadedMoviesScan", request.CommandName);
        Assert.Equal(DownloadId, request.DownloadClientId);
        Assert.Equal(path, request.Path);
    }

    [Fact]
    public void RepeatedCorrelationsWithSameOrdinalDownloadIdRemainEligible()
    {
        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            SonarrTarget(),
            [
                Correlation(DownloadId, seriesId: 41),
                Correlation(DownloadId, seriesId: 41, episodeId: 42)
            ],
            SonarrQueue(SonarrRecord(DownloadId, "/mapped/show", seriesId: 41, episodeId: 42)));

        Assert.True(decision.IsDirectScan);
    }

    [Fact]
    public void PreparationAllowsCallerToSkipQueueProbeForIneligibleCorrelationInput()
    {
        var unsupported = ArrCompletedDownloadDispatchPolicy.Prepare(
            new ArrCompletedDownloadTarget(
                "lidarr",
                ArrCompletedDownloadClientKind.Lidarr,
                ArrCompletedDownloadRouteKind.Correlation),
            [Correlation(DownloadId)]);
        var categoryOnly = ArrCompletedDownloadDispatchPolicy.Prepare(
            SonarrTarget(ArrCompletedDownloadRouteKind.CategoryOwnership),
            [Correlation(DownloadId)]);
        var conflicting = ArrCompletedDownloadDispatchPolicy.Prepare(
            SonarrTarget(),
            [Correlation(DownloadId), Correlation("other-id")]);

        Assert.False(unsupported.ShouldProbeQueue);
        Assert.Equal(ArrCompletedDownloadFallbackReason.UnsupportedTarget, unsupported.FallbackReason);
        Assert.False(categoryOnly.ShouldProbeQueue);
        Assert.Equal(ArrCompletedDownloadFallbackReason.RouteNotCorrelated, categoryOnly.FallbackReason);
        Assert.False(conflicting.ShouldProbeQueue);
        Assert.Equal(
            ArrCompletedDownloadFallbackReason.CorrelationDownloadIdConflict,
            conflicting.FallbackReason);
    }

    [Fact]
    public void EligiblePreparationCanBeCompletedWithTypedQueueWithoutExposingIdentifier()
    {
        var preparation = ArrCompletedDownloadDispatchPolicy.Prepare(
            SonarrTarget(),
            [Correlation(DownloadId, seriesId: 41)]);

        Assert.True(preparation.ShouldProbeQueue);
        Assert.Null(preparation.FallbackReason);
        Assert.Null(preparation.FallbackReasonCode);
        Assert.DoesNotContain(DownloadId, preparation.ToString(), StringComparison.Ordinal);

        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            preparation,
            SonarrQueue(SonarrRecord(DownloadId, "/mapped/show", seriesId: 41)));

        Assert.True(decision.IsDirectScan);
    }

    [Fact]
    public void PreparationSnapshotsCorrelationFactsBeforeQueueProbe()
    {
        var correlations = new List<ArrCompletedDownloadCorrelationFact>
        {
            Correlation(DownloadId, movieId: 91)
        };
        var preparation = ArrCompletedDownloadDispatchPolicy.Prepare(RadarrTarget(), correlations);
        correlations[0] = Correlation(DownloadId);

        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            preparation,
            RadarrQueue(RadarrRecord(DownloadId, "/mapped/movie", movieId: 92)));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueMediaIdentityConflict);
    }

    [Theory]
    [InlineData(ArrCompletedDownloadRouteKind.None)]
    [InlineData(ArrCompletedDownloadRouteKind.CategoryOwnership)]
    public void NonCorrelationRoutingAlwaysFallsBack(ArrCompletedDownloadRouteKind routeKind)
    {
        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            SonarrTarget(routeKind),
            [Correlation(DownloadId)],
            SonarrQueue(SonarrRecord(DownloadId, "/mapped/show")));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.RouteNotCorrelated);
    }

    [Fact]
    public void NullCorrelationsFallBack()
    {
        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate<SonarrQueueRecord>(
            SonarrTarget(),
            null,
            SonarrQueue(SonarrRecord(DownloadId, "/mapped/show")));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.CorrelationMissing);
    }

    [Fact]
    public void EmptyCorrelationsFallBack()
    {
        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            SonarrTarget(),
            Array.Empty<ArrCompletedDownloadCorrelationFact>(),
            SonarrQueue(SonarrRecord(DownloadId, "/mapped/show")));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.CorrelationMissing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnyBlankCorrelationDownloadIdFallsBack(string? blankDownloadId)
    {
        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            SonarrTarget(),
            [Correlation(DownloadId), Correlation(blankDownloadId)],
            SonarrQueue(SonarrRecord(DownloadId, "/mapped/show")));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.CorrelationDownloadIdMissing);
    }

    [Theory]
    [InlineData("SAB-job-Aa20")]
    [InlineData("sab-job-aa19")]
    public void ConflictingOrCaseChangedCorrelationDownloadIdFallsBack(string otherDownloadId)
    {
        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            SonarrTarget(),
            [Correlation(DownloadId), Correlation(otherDownloadId)],
            SonarrQueue(SonarrRecord(DownloadId, "/mapped/show")));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.CorrelationDownloadIdConflict);
    }

    [Fact]
    public void CaseChangedQueueDownloadIdDoesNotMatch()
    {
        var decision = EvaluateSonarr(
            Correlation(DownloadId),
            SonarrQueue(SonarrRecord("sab-job-aa19", "/mapped/show")));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueMatchMissing);
    }

    [Fact]
    public void NullQueueFallsBackAsMalformed()
    {
        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate<SonarrQueueRecord>(
            SonarrTarget(),
            [Correlation(DownloadId)],
            null);

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueMalformed);
    }

    [Fact]
    public void NullRecordsFallsBackAsMalformed()
    {
        var queue = SonarrQueue();
        queue.TotalRecords = 0;
        queue.Records = null!;

        var decision = EvaluateSonarr(Correlation(DownloadId), queue);

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueMalformed);
    }

    [Fact]
    public void NullRecordFallsBackAsMalformed()
    {
        var queue = SonarrQueue((SonarrQueueRecord)null!);

        var decision = EvaluateSonarr(Correlation(DownloadId), queue);

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueMalformed);
    }

    [Theory]
    [MemberData(nameof(MalformedPagination))]
    public void ImpossibleOrUnboundedPaginationFallsBackAsMalformed(
        int page,
        int pageSize,
        int totalRecords,
        int recordCount)
    {
        var records = Enumerable.Range(0, recordCount)
            .Select(index => SonarrRecord($"other-{index}", $"/mapped/{index}"))
            .ToArray();
        var queue = SonarrQueue(records);
        queue.Page = page;
        queue.PageSize = pageSize;
        queue.TotalRecords = totalRecords;

        var decision = EvaluateSonarr(Correlation(DownloadId), queue);

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueMalformed);
    }

    public static TheoryData<int, int, int, int> MalformedPagination => new()
    {
        { 0, 5000, 0, 0 },
        { -1, 5000, 0, 0 },
        { 2, 5000, 1, 1 },
        { 1, 0, 0, 0 },
        { 1, -1, 0, 0 },
        { 1, ArrCompletedDownloadDispatchPolicy.MaximumQueueRecords + 1, 0, 0 },
        { 1, 1, -1, 0 },
        { 1, 1, 2, 2 }
    };

    [Theory]
    [InlineData(2, 1)]
    [InlineData(0, 1)]
    public void TotalRecordCountMismatchFallsBackAsIncomplete(int totalRecords, int recordCount)
    {
        var records = Enumerable.Range(0, recordCount)
            .Select(index => SonarrRecord(index == 0 ? DownloadId : $"other-{index}", $"/mapped/{index}"))
            .ToArray();
        var queue = SonarrQueue(records);
        queue.TotalRecords = totalRecords;

        var decision = EvaluateSonarr(Correlation(DownloadId), queue);

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueIncomplete);
    }

    [Fact]
    public void ZeroQueueMatchesFallsBack()
    {
        var decision = EvaluateSonarr(
            Correlation(DownloadId),
            SonarrQueue(SonarrRecord("other-id", "/mapped/show")));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueMatchMissing);
    }

    [Fact]
    public void DuplicateDownloadIdMatchesFallBackEvenWhenOnlyOneIsUsenet()
    {
        var decision = EvaluateSonarr(
            Correlation(DownloadId),
            SonarrQueue(
                SonarrRecord(DownloadId, "/mapped/show", protocol: "usenet"),
                SonarrRecord(DownloadId, "/mapped/show", protocol: "torrent")));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueMatchDuplicate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("torrent")]
    [InlineData("Usenet")]
    [InlineData(" usenet")]
    public void ProtocolMustBeExactOrdinalUsenet(string? protocol)
    {
        var decision = EvaluateSonarr(
            Correlation(DownloadId),
            SonarrQueue(SonarrRecord(DownloadId, "/mapped/show", protocol: protocol)));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueProtocolUnsupported);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOutputPathFallsBack(string? path)
    {
        var decision = EvaluateSonarr(
            Correlation(DownloadId),
            SonarrQueue(SonarrRecord(DownloadId, path)));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueOutputPathMissing);
    }

    [Fact]
    public void NumericDownloadClientIdIsNeverUsedForIdentity()
    {
        var decision = EvaluateSonarr(
            Correlation(DownloadId),
            SonarrQueue(SonarrRecord(
                DownloadId,
                "/mapped/show",
                numericDownloadClientId: int.MinValue)));

        Assert.True(decision.IsDirectScan);
        Assert.Equal(DownloadId, decision.DirectScan!.DownloadClientId);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(91, 0)]
    [InlineData(0, 91)]
    [InlineData(91, 91)]
    public void RadarrOnlyComparesPositiveMediaIdsSuppliedOnBothSides(int correlationMovieId, int queueMovieId)
    {
        var decision = EvaluateRadarr(
            Correlation(DownloadId, movieId: correlationMovieId),
            RadarrQueue(RadarrRecord(DownloadId, "/mapped/movie", movieId: queueMovieId)));

        Assert.True(decision.IsDirectScan);
    }

    [Fact]
    public void RadarrQueueMovieIdConflictFallsBack()
    {
        var decision = EvaluateRadarr(
            Correlation(DownloadId, movieId: 91),
            RadarrQueue(RadarrRecord(DownloadId, "/mapped/movie", movieId: 92)));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueMediaIdentityConflict);
    }

    [Fact]
    public void RadarrCorrelationMovieIdConflictFallsBack()
    {
        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            RadarrTarget(),
            [Correlation(DownloadId, movieId: 91), Correlation(DownloadId, movieId: 92)],
            RadarrQueue(RadarrRecord(DownloadId, "/mapped/movie", movieId: 91)));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.CorrelationMediaIdentityConflict);
    }

    [Fact]
    public void RadarrNegativeCorrelationMovieIdFallsBackWithStableBoundedCode()
    {
        var decision = EvaluateRadarr(
            Correlation(DownloadId, movieId: -1),
            RadarrQueue(RadarrRecord(DownloadId, "/mapped/movie")));

        Assert.False(decision.IsDirectScan);
        Assert.Equal("correlation-media-identity-invalid", decision.FallbackReasonCode);
        Assert.InRange(decision.FallbackReasonCode!.Length, 1, 48);
    }

    [Fact]
    public void RadarrNegativeQueueMovieIdFallsBackWithStableBoundedCode()
    {
        var decision = EvaluateRadarr(
            Correlation(DownloadId),
            RadarrQueue(RadarrRecord(DownloadId, "/mapped/movie", movieId: -1)));

        Assert.False(decision.IsDirectScan);
        Assert.Equal("queue-media-identity-invalid", decision.FallbackReasonCode);
        Assert.InRange(decision.FallbackReasonCode!.Length, 1, 48);
    }

    [Fact]
    public void RadarrNullQueueMovieIdRemainsEligible()
    {
        var record = JsonSerializer.Deserialize<RadarrQueueRecord>("""
            {"downloadId":"SAB-job-Aa19","outputPath":"/mapped/movie","protocol":"usenet","movieId":null}
            """)!;

        var decision = EvaluateRadarr(Correlation(DownloadId), RadarrQueue(record));

        Assert.True(decision.IsDirectScan);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(81, 0, 81, 82)]
    [InlineData(0, 82, 81, 82)]
    [InlineData(81, 82, 81, 82)]
    public void SonarrOnlyComparesPositiveMediaIdsSuppliedOnBothSides(
        int correlationSeriesId,
        int correlationEpisodeId,
        int queueSeriesId,
        int queueEpisodeId)
    {
        var decision = EvaluateSonarr(
            Correlation(
                DownloadId,
                seriesId: correlationSeriesId,
                episodeId: correlationEpisodeId),
            SonarrQueue(SonarrRecord(
                DownloadId,
                "/mapped/show",
                seriesId: queueSeriesId,
                episodeId: queueEpisodeId)));

        Assert.True(decision.IsDirectScan);
    }

    [Theory]
    [InlineData(81, 82, 83, 82)]
    [InlineData(81, 82, 81, 83)]
    public void SonarrQueueMediaIdentityConflictFallsBack(
        int correlationSeriesId,
        int correlationEpisodeId,
        int queueSeriesId,
        int queueEpisodeId)
    {
        var decision = EvaluateSonarr(
            Correlation(
                DownloadId,
                seriesId: correlationSeriesId,
                episodeId: correlationEpisodeId),
            SonarrQueue(SonarrRecord(
                DownloadId,
                "/mapped/show",
                seriesId: queueSeriesId,
                episodeId: queueEpisodeId)));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueMediaIdentityConflict);
    }

    [Theory]
    [InlineData(81, 82, 83, 82)]
    [InlineData(81, 82, 81, 83)]
    public void SonarrCorrelationMediaIdentityConflictFallsBack(
        int firstSeriesId,
        int firstEpisodeId,
        int secondSeriesId,
        int secondEpisodeId)
    {
        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            SonarrTarget(),
            [
                Correlation(DownloadId, seriesId: firstSeriesId, episodeId: firstEpisodeId),
                Correlation(DownloadId, seriesId: secondSeriesId, episodeId: secondEpisodeId)
            ],
            SonarrQueue(SonarrRecord(
                DownloadId,
                "/mapped/show",
                seriesId: firstSeriesId,
                episodeId: firstEpisodeId)));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.CorrelationMediaIdentityConflict);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, -1)]
    public void SonarrNegativeCorrelationMediaIdFallsBackWithStableBoundedCode(
        int? seriesId,
        int? episodeId)
    {
        var decision = EvaluateSonarr(
            Correlation(DownloadId, seriesId: seriesId, episodeId: episodeId),
            SonarrQueue(SonarrRecord(DownloadId, "/mapped/show")));

        Assert.False(decision.IsDirectScan);
        Assert.Equal("correlation-media-identity-invalid", decision.FallbackReasonCode);
        Assert.InRange(decision.FallbackReasonCode!.Length, 1, 48);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void SonarrNegativeQueueMediaIdFallsBackWithStableBoundedCode(
        int seriesId,
        int episodeId)
    {
        var decision = EvaluateSonarr(
            Correlation(DownloadId),
            SonarrQueue(SonarrRecord(
                DownloadId,
                "/mapped/show",
                seriesId: seriesId,
                episodeId: episodeId)));

        Assert.False(decision.IsDirectScan);
        Assert.Equal("queue-media-identity-invalid", decision.FallbackReasonCode);
        Assert.InRange(decision.FallbackReasonCode!.Length, 1, 48);
    }

    [Fact]
    public void SonarrNullQueueMediaIdsRemainEligible()
    {
        var record = JsonSerializer.Deserialize<SonarrQueueRecord>("""
            {"downloadId":"SAB-job-Aa19","outputPath":"/mapped/show","protocol":"usenet","seriesId":null,"episodeId":null,"seasonNumber":null}
            """)!;

        var decision = EvaluateSonarr(Correlation(DownloadId), SonarrQueue(record));

        Assert.True(decision.IsDirectScan);
    }

    [Fact]
    public void NegativeMediaIdsOutsideTheTargetKindRemainUnavailableAndIgnored()
    {
        var sonarr = EvaluateSonarr(
            Correlation(DownloadId, movieId: -1),
            SonarrQueue(SonarrRecord(DownloadId, "/mapped/show")));
        var radarr = EvaluateRadarr(
            Correlation(DownloadId, seriesId: -1, episodeId: -1),
            RadarrQueue(RadarrRecord(DownloadId, "/mapped/movie")));

        Assert.True(sonarr.IsDirectScan);
        Assert.True(radarr.IsDirectScan);
    }

    [Theory]
    [MemberData(nameof(UnsupportedTargets))]
    public void LidarrUnknownAndMismatchedTargetIdentityFallBack(ArrCompletedDownloadTarget target)
    {
        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            target,
            [Correlation(DownloadId)],
            SonarrQueue(SonarrRecord(DownloadId, "/mapped/show")));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.UnsupportedTarget);
    }

    public static TheoryData<ArrCompletedDownloadTarget> UnsupportedTargets => new()
    {
        new("lidarr", ArrCompletedDownloadClientKind.Lidarr, ArrCompletedDownloadRouteKind.Correlation),
        new("unknown", ArrCompletedDownloadClientKind.Unknown, ArrCompletedDownloadRouteKind.Correlation),
        new("Sonarr", ArrCompletedDownloadClientKind.Sonarr, ArrCompletedDownloadRouteKind.Correlation),
        new("radarr", ArrCompletedDownloadClientKind.Sonarr, ArrCompletedDownloadRouteKind.Correlation),
        new("sonarr", ArrCompletedDownloadClientKind.Radarr, ArrCompletedDownloadRouteKind.Correlation)
    };

    [Fact]
    public void QueueRecordTypeMustAgreeWithTargetClientKind()
    {
        var decision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            SonarrTarget(),
            [Correlation(DownloadId)],
            RadarrQueue(RadarrRecord(DownloadId, "/mapped/movie")));

        AssertFallback(decision, ArrCompletedDownloadFallbackReason.QueueTypeMismatch);
    }

    [Fact]
    public void DiagnosticsAndToStringNeverExposeSensitiveInput()
    {
        const string sensitiveId = "secret-download-id-8421";
        const string sensitivePath = "/secret/library/Title.With.Secret";
        const string sensitiveApp = "https://user:password@example.invalid?apikey=secret-api-key";
        var target = new ArrCompletedDownloadTarget(
            sensitiveApp,
            ArrCompletedDownloadClientKind.Unknown,
            ArrCompletedDownloadRouteKind.Correlation);
        var fact = Correlation(sensitiveId);
        var directDecision = EvaluateSonarr(
            fact,
            SonarrQueue(SonarrRecord(
                sensitiveId,
                sensitivePath,
                title: "raw-release-title")));
        var fallbackDecision = ArrCompletedDownloadDispatchPolicy.Evaluate(
            target,
            [fact],
            SonarrQueue(SonarrRecord(
                sensitiveId,
                sensitivePath,
                title: "raw-release-title")));

        var diagnostics = new[]
        {
            target.ToString(),
            fact.ToString(),
            directDecision.ToString(),
            directDecision.DirectScan!.ToString(),
            fallbackDecision.ToString(),
            fallbackDecision.FallbackReasonCode!
        };
        var sensitiveValues = new[]
        {
            sensitiveId,
            sensitivePath,
            "Title.With.Secret",
            "raw-release-title",
            "secret-api-key",
            "user:password",
            "example.invalid"
        };

        Assert.All(
            diagnostics,
            diagnostic => Assert.All(
                sensitiveValues,
                sensitive => Assert.DoesNotContain(sensitive, diagnostic, StringComparison.Ordinal)));
    }

    [Fact]
    public void EveryFallbackReasonHasUniqueBoundedStableCode()
    {
        var codes = Enum.GetValues<ArrCompletedDownloadFallbackReason>()
            .Select(ArrCompletedDownloadDispatchPolicy.GetFallbackReasonCode)
            .ToArray();

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code =>
        {
            Assert.InRange(code.Length, 1, 48);
            Assert.Matches("^[a-z0-9]+(?:-[a-z0-9]+)*$", code);
        });
    }

    private static ArrCompletedDownloadDispatchDecision EvaluateSonarr(
        ArrCompletedDownloadCorrelationFact correlation,
        SonarrQueue queue) =>
        ArrCompletedDownloadDispatchPolicy.Evaluate(SonarrTarget(), [correlation], queue);

    private static ArrCompletedDownloadDispatchDecision EvaluateRadarr(
        ArrCompletedDownloadCorrelationFact correlation,
        RadarrQueue queue) =>
        ArrCompletedDownloadDispatchPolicy.Evaluate(RadarrTarget(), [correlation], queue);

    private static ArrCompletedDownloadTarget SonarrTarget(
        ArrCompletedDownloadRouteKind routeKind = ArrCompletedDownloadRouteKind.Correlation) =>
        new("sonarr", ArrCompletedDownloadClientKind.Sonarr, routeKind);

    private static ArrCompletedDownloadTarget RadarrTarget() =>
        new("radarr", ArrCompletedDownloadClientKind.Radarr, ArrCompletedDownloadRouteKind.Correlation);

    private static ArrCompletedDownloadCorrelationFact Correlation(
        string? downloadId,
        int? movieId = null,
        int? seriesId = null,
        int? episodeId = null) =>
        new(downloadId, movieId, seriesId, episodeId);

    private static SonarrQueue SonarrQueue(params SonarrQueueRecord[] records) => new()
    {
        Page = 1,
        PageSize = ArrCompletedDownloadDispatchPolicy.MaximumQueueRecords,
        TotalRecords = records.Length,
        Records = [.. records]
    };

    private static RadarrQueue RadarrQueue(params RadarrQueueRecord[] records) => new()
    {
        Page = 1,
        PageSize = ArrCompletedDownloadDispatchPolicy.MaximumQueueRecords,
        TotalRecords = records.Length,
        Records = [.. records]
    };

    private static SonarrQueueRecord SonarrRecord(
        string? downloadId,
        string? path,
        string? protocol = "usenet",
        int seriesId = 0,
        int episodeId = 0,
        int? numericDownloadClientId = null,
        string? title = null) => new()
    {
        DownloadId = downloadId,
        DownloadClientId = numericDownloadClientId,
        OutputPath = path,
        Protocol = protocol,
        SeriesId = seriesId,
        EpisodeId = episodeId,
        Title = title
    };

    private static RadarrQueueRecord RadarrRecord(
        string? downloadId,
        string? path,
        string? protocol = "usenet",
        int movieId = 0) => new()
    {
        DownloadId = downloadId,
        OutputPath = path,
        Protocol = protocol,
        MovieId = movieId
    };

    private static void AssertFallback(
        ArrCompletedDownloadDispatchDecision decision,
        ArrCompletedDownloadFallbackReason reason)
    {
        Assert.False(decision.IsDirectScan);
        Assert.Null(decision.DirectScan);
        Assert.Equal(reason, decision.FallbackReason);
        Assert.Equal(
            ArrCompletedDownloadDispatchPolicy.GetFallbackReasonCode(reason),
            decision.FallbackReasonCode);
    }
}
