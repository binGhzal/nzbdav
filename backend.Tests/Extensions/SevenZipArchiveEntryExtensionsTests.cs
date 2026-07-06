using NzbWebDAV.Extensions;

namespace backend.Tests.Extensions;

public sealed class SevenZipArchiveEntryExtensionsTests
{
    [Fact]
    public void GetPackedSize_ReturnsNullWhenPackedSizeIsUnavailable()
    {
        Assert.Null(SevenZipArchiveEntryExtensions.GetPackedSize(null!));
    }
}
