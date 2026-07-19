using System.Net;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class SonarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    private static readonly Dictionary<string, int> SeriesPathToSeriesIdCache = new();
    private static readonly Dictionary<string, int> SymlinkToEpisodeFileIdCache = new();

    public virtual Task<SonarrQueue> GetSonarrQueueAsync(CancellationToken ct = default) =>
        Get<SonarrQueue>($"/queue?protocol=usenet&pageSize=5000", ct);

    public virtual Task<ArrCommand> DownloadedEpisodesScanAsync(
        string path,
        string downloadClientId,
        CancellationToken ct = default) =>
        CommandAsync(new { name = "DownloadedEpisodesScan", path, downloadClientId, importMode = 0 }, ct);

    public Task<ArrPagedResponse<SonarrMissingEpisode>> GetMissingEpisodesAsync(int pageSize = 500, CancellationToken ct = default) =>
        Get<ArrPagedResponse<SonarrMissingEpisode>>(
            $"/wanted/missing?page=1&pageSize={pageSize}&sortKey=airDateUtc&sortDirection=descending&monitored=true",
            ct);

    public Task<List<SonarrSeries>> GetAllSeries(CancellationToken ct = default) =>
        Get<List<SonarrSeries>>($"/series", ct);

    public Task<SonarrSeries> GetSeries(int seriesId, CancellationToken ct = default) =>
        Get<SonarrSeries>($"/series/{seriesId}", ct);

    public Task<SonarrEpisodeFile> GetEpisodeFile(int episodeFileId, CancellationToken ct = default) =>
        Get<SonarrEpisodeFile>($"/episodefile/{episodeFileId}", ct);

    public Task<List<SonarrEpisodeFile>> GetAllEpisodeFiles(int seriesId, CancellationToken ct = default) =>
        Get<List<SonarrEpisodeFile>>($"/episodefile?seriesId={seriesId}", ct);

    public Task<List<SonarrEpisode>> GetEpisodesFromEpisodeFileId(int episodeFileId, CancellationToken ct = default) =>
        Get<List<SonarrEpisode>>($"/episode?episodeFileId={episodeFileId}", ct);

    public Task<HttpStatusCode> DeleteEpisodeFile(int episodeFileId, CancellationToken ct = default) =>
        Delete($"/episodefile/{episodeFileId}", ct: ct);

    public Task<ArrCommand> SearchEpisodesAsync(List<int> episodeIds, CancellationToken ct = default) =>
        CommandAsync(new { name = "EpisodeSearch", episodeIds }, ct);

    public override async Task<bool> RemoveAndSearch(string symlinkPath, CancellationToken ct = default)
    {
        // get episode-file-id and episode-ids
        var mediaIds = await GetMediaIds(symlinkPath, ct).ConfigureAwait(false);
        if (mediaIds == null) return false;

        // delete the episode-file
        if (await DeleteEpisodeFile(mediaIds.Value.episodeFileId, ct).ConfigureAwait(false) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete episode file `{symlinkPath}` from sonarr instance `{Host}`.");

        // trigger a new search for each episode
        await SearchEpisodesAsync(mediaIds.Value.episodeIds, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<(int episodeFileId, List<int> episodeIds)?> GetMediaIds
    (
        string symlinkPath,
        CancellationToken ct
    )
    {
        // get episode-file-id
        var episodeFileId = await GetEpisodeFileId(symlinkPath, ct).ConfigureAwait(false);
        if (episodeFileId == null) return null;

        // get episode-ids
        var episodes = await GetEpisodesFromEpisodeFileId(episodeFileId.Value, ct).ConfigureAwait(false);
        var episodeIds = episodes.Select(x => x.Id).ToList();
        if (episodeIds.Count == 0) return null;

        // return
        return (episodeFileId.Value, episodeIds);
    }

    private async Task<int?> GetEpisodeFileId(string symlinkPath, CancellationToken ct)
    {
        // if episode-file-id is found in the cache, verify it and return it
        if (SymlinkToEpisodeFileIdCache.TryGetValue(symlinkPath, out var episodeFileId))
        {
            var episodeFile = await GetEpisodeFile(episodeFileId, ct).ConfigureAwait(false);
            if (episodeFile.Path == symlinkPath) return episodeFileId;
        }

        // otherwise, find the series-id
        var seriesId = await GetSeriesId(symlinkPath, ct).ConfigureAwait(false);
        if (seriesId == null) return null;

        // then use it to find all episode-files and repopulate the cache
        int? result = null;
        foreach (var episodeFile in await GetAllEpisodeFiles(seriesId.Value, ct).ConfigureAwait(false))
        {
            SymlinkToEpisodeFileIdCache[episodeFile.Path!] = episodeFile.Id;
            if (episodeFile.Path == symlinkPath)
                result = episodeFile.Id;
        }

        // return the found episode-file-id
        return result;
    }

    private async Task<int?> GetSeriesId(string symlinkPath, CancellationToken ct)
    {
        // get series-id from cache
        var cachedSeriesId = PathUtil.GetAllParentDirectories(symlinkPath)
            .Where(x => SeriesPathToSeriesIdCache.ContainsKey(x))
            .Select(x => SeriesPathToSeriesIdCache[x])
            .Select(x => (int?)x)
            .FirstOrDefault();

        // if found, verify and return it
        if (cachedSeriesId != null)
        {
            var series = await GetSeries(cachedSeriesId.Value, ct).ConfigureAwait(false);
            if (symlinkPath.StartsWith(series.Path!))
                return cachedSeriesId;
        }

        // otherwise, fetch all series and repopulate the cache
        int? result = null;
        foreach (var series in await GetAllSeries(ct).ConfigureAwait(false))
        {
            SeriesPathToSeriesIdCache[series.Path!] = series.Id;
            if (symlinkPath.StartsWith(series.Path!))
                result = series.Id;
        }

        // return the found series-id
        return result;
    }
}
