using System.Reflection;
using NzbWebDAV.Queue.FileProcessors;

namespace backend.Tests.Queue.FileProcessors;

public sealed class FilePartNumberParsingTests
{
    private const string OversizedPartNumber = "999999999999999999999999999999";

    [Theory]
    [InlineData($"movie.part{OversizedPartNumber}.rar")]
    [InlineData($"movie.r{OversizedPartNumber}")]
    public void RarPartNumberParserIgnoresOversizedFilenameNumbers(string filename)
    {
        var partNumber = InvokePrivateStatic<int?>(
            typeof(RarProcessor),
            "GetPartNumberFromFilename",
            filename);

        Assert.Null(partNumber);
    }

    [Fact]
    public void SevenZipPartNumberParserOrdersOversizedFilenameNumbersLast()
    {
        var partNumber = InvokePrivateStatic<int>(
            typeof(SevenZipProcessor),
            "GetPartNumber",
            $"movie.7z.{OversizedPartNumber}");

        Assert.Equal(int.MaxValue, partNumber);
    }

    [Fact]
    public void MultipartMkvPartNumberParserOrdersOversizedFilenameNumbersLast()
    {
        var partNumber = InvokePrivateStatic<int>(
            typeof(MultipartMkvProcessor),
            "GetPartNumber",
            $"movie.mkv.{OversizedPartNumber}");

        Assert.Equal(int.MaxValue, partNumber);
    }

    private static T? InvokePrivateStatic<T>(Type type, string methodName, params object[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (T?)method.Invoke(null, args);
    }
}
