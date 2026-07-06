using System.Net;
using NzbWebDAV.Clients.RadarrSonarr.LidarrModels;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class LidarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    protected override string BasePath => "/api/v1";

    private static readonly Dictionary<string, int> ArtistPathToArtistIdCache = new();
    private static readonly Dictionary<string, int> TrackFilePathToTrackFileIdCache = new();

    public Task<List<LidarrArtist>> GetAllArtists(CancellationToken ct = default) =>
        Get<List<LidarrArtist>>($"/artist", ct);

    public Task<LidarrArtist> GetArtist(int artistId, CancellationToken ct = default) =>
        Get<LidarrArtist>($"/artist/{artistId}", ct);

    public Task<LidarrQueue> GetLidarrQueueAsync(CancellationToken ct = default) =>
        Get<LidarrQueue>($"/queue?protocol=usenet&pageSize=5000", ct);

    public Task<List<LidarrTrackFile>> GetAllTrackFiles(int artistId, CancellationToken ct = default) =>
        Get<List<LidarrTrackFile>>($"/trackfile?artistId={artistId}", ct);

    public Task<LidarrTrackFile> GetTrackFile(int trackFileId, CancellationToken ct = default) =>
        Get<LidarrTrackFile>($"/trackfile/{trackFileId}", ct);

    public Task<HttpStatusCode> DeleteTrackFile(int trackFileId, CancellationToken ct = default) =>
        Delete($"/trackfile/{trackFileId}", ct: ct);

    public Task SearchArtistAsync(int artistId, CancellationToken ct = default) =>
        CommandAsync(new { name = "ArtistSearch", artistId }, ct);

    public override async Task<bool> RemoveAndSearch(string symlinkOrStrmPath, CancellationToken ct = default)
    {
        var mediaIds = await GetMediaIds(symlinkOrStrmPath, ct).ConfigureAwait(false);
        if (mediaIds == null) return false;

        if (await DeleteTrackFile(mediaIds.Value.trackFileId, ct).ConfigureAwait(false) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete track file `{symlinkOrStrmPath}` from Lidarr instance `{Host}`.");

        await SearchArtistAsync(mediaIds.Value.artistId, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<(int trackFileId, int artistId)?> GetMediaIds(string symlinkOrStrmPath, CancellationToken ct)
    {
        // if track-file-id is cached, verify and return it
        if (TrackFilePathToTrackFileIdCache.TryGetValue(symlinkOrStrmPath, out var cachedTrackFileId))
        {
            var trackFile = await GetTrackFile(cachedTrackFileId, ct).ConfigureAwait(false);
            if (trackFile.Path == symlinkOrStrmPath)
                return (cachedTrackFileId, trackFile.ArtistId);
        }

        // find the artist whose root path is a prefix of the given file path
        var artistId = await GetArtistId(symlinkOrStrmPath, ct).ConfigureAwait(false);
        if (artistId == null) return null;

        // scan all track files for that artist and populate the cache
        int? result = null;
        foreach (var trackFile in await GetAllTrackFiles(artistId.Value, ct).ConfigureAwait(false))
        {
            if (trackFile.Path != null)
                TrackFilePathToTrackFileIdCache[trackFile.Path] = trackFile.Id;
            if (trackFile.Path == symlinkOrStrmPath)
                result = trackFile.Id;
        }

        return result == null ? null : (result.Value, artistId.Value);
    }

    private async Task<int?> GetArtistId(string symlinkOrStrmPath, CancellationToken ct)
    {
        // check cache first using all parent directories
        var cachedArtistId = PathUtil.GetAllParentDirectories(symlinkOrStrmPath)
            .Where(p => ArtistPathToArtistIdCache.ContainsKey(p))
            .Select(p => ArtistPathToArtistIdCache[p])
            .Select(id => (int?)id)
            .FirstOrDefault();

        if (cachedArtistId != null)
        {
            var artist = await GetArtist(cachedArtistId.Value, ct).ConfigureAwait(false);
            if (artist.Path != null && symlinkOrStrmPath.StartsWith(artist.Path))
                return cachedArtistId;
        }

        // refresh all artists and repopulate cache
        int? result = null;
        foreach (var artist in await GetAllArtists(ct).ConfigureAwait(false))
        {
            if (artist.Path != null)
                ArtistPathToArtistIdCache[artist.Path] = artist.Id;
            if (artist.Path != null && symlinkOrStrmPath.StartsWith(artist.Path))
                result = artist.Id;
        }

        return result;
    }
}
