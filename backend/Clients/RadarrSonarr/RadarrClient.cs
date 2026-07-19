using System.Net;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.RadarrModels;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class RadarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    private static readonly Dictionary<string, int> SymlinkToMovieIdCache = new();

    public Task<RadarrMovie> GetMovieAsync(int id, CancellationToken ct = default) =>
        Get<RadarrMovie>($"/movie/{id}", ct);

    public Task<List<RadarrMovie>> GetMoviesAsync(CancellationToken ct = default) =>
        Get<List<RadarrMovie>>($"/movie", ct);

    public virtual Task<RadarrQueue> GetRadarrQueueAsync(CancellationToken ct = default) =>
        Get<RadarrQueue>($"/queue?protocol=usenet&pageSize=5000", ct);

    public virtual Task<ArrCommand> DownloadedMoviesScanAsync(
        string path,
        string downloadClientId,
        CancellationToken ct = default) =>
        CommandAsync(new { name = "DownloadedMoviesScan", path, downloadClientId, importMode = 0 }, ct);

    public Task<ArrPagedResponse<RadarrMissingMovie>> GetMissingMoviesAsync(int pageSize = 500, CancellationToken ct = default) =>
        Get<ArrPagedResponse<RadarrMissingMovie>>(
            $"/wanted/missing?page=1&pageSize={pageSize}&sortKey=physicalRelease&sortDirection=descending&monitored=true",
            ct);

    public Task<HttpStatusCode> DeleteMovieFile(int id, CancellationToken ct = default) =>
        Delete($"/moviefile/{id}", ct: ct);

    public Task<ArrCommand> SearchMovieAsync(int id, CancellationToken ct = default) =>
        CommandAsync(new { name = "MoviesSearch", movieIds = new List<int> { id } }, ct);

    public Task<ArrCommand> SearchMoviesAsync(List<int> ids, CancellationToken ct = default) =>
        CommandAsync(new { name = "MoviesSearch", movieIds = ids }, ct);

    public override async Task<bool> RemoveAndSearch(string symlinkPath, CancellationToken ct = default)
    {
        var mediaIds = await GetMediaIds(symlinkPath, ct).ConfigureAwait(false);
        if (mediaIds == null) return false;

        if (await DeleteMovieFile(mediaIds.Value.movieFileId, ct).ConfigureAwait(false) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete movie file `{symlinkPath}` from radarr instance `{Host}`.");

        await SearchMovieAsync(mediaIds.Value.movieId, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<(int movieFileId, int movieId)?> GetMediaIds(string symlinkPath, CancellationToken ct)
    {
        // if we already have the movie-id cached
        // then let's use it to find and return the corresponding movie-file-id
        if (SymlinkToMovieIdCache.TryGetValue(symlinkPath, out var movieId))
        {
            var movie = await GetMovieAsync(movieId, ct).ConfigureAwait(false);
            if (movie.MovieFile?.Path == symlinkPath)
                return (movie.MovieFile.Id!, movieId);
        }

        // otherwise, let's fetch all movies, cache all movie files
        // and return the matching movie-id and movie-file-id
        var allMovies = await GetMoviesAsync(ct).ConfigureAwait(false);
        (int movieFileId, int movieId)? result = null;
        foreach (var movie in allMovies)
        {
            var movieFile = movie.MovieFile;
            if (movieFile?.Path != null)
                SymlinkToMovieIdCache[movieFile.Path] = movie.Id;
            if (movieFile?.Path == symlinkPath)
                result = (movieFile.Id!, movie.Id);
        }

        return result;
    }
}
