using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class HealthCheckRepairPolicyTests
{
    [Theory]
    [InlineData("/media/movies/Movie/Movie.mkv", "/media/movies")]
    [InlineData("/media/movies", "/media/movies")]
    [InlineData(@"C:\Media\Movies\Movie\Movie.mkv", @"C:\Media\Movies")]
    public void IsPathInsideRootReturnsTrueForMatchingRoot(string path, string rootPath)
    {
        Assert.True(HealthCheckService.IsPathInsideRoot(path, rootPath));
    }

    [Theory]
    [InlineData("/media/movies2/Movie/Movie.mkv", "/media/movies")]
    [InlineData("/media/movie", "/media/movies")]
    [InlineData("/media/tv/Series/Episode.mkv", "/media/movies")]
    [InlineData("/media/movies/Movie/Movie.mkv", null)]
    [InlineData("", "/media/movies")]
    public void IsPathInsideRootReturnsFalseForNonMatchingRoot(string path, string? rootPath)
    {
        Assert.False(HealthCheckService.IsPathInsideRoot(path, rootPath));
    }
}
