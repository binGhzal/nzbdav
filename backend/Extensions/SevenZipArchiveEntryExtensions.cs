using System.Collections;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;

namespace NzbWebDAV.Extensions;

public static class SevenZipArchiveEntryExtensions
{
    public static CompressionType GetCompressionType(this SevenZipArchiveEntry entry)
    {
        try
        {
            return entry.CompressionType;
        }
        catch (NotImplementedException)
        {
            var coders = entry?.GetCoders();
            var compressionMethodId = GetCoderMethodId(coders?.FirstOrDefault());
            return compressionMethodId == 0 ? CompressionType.None
                : compressionMethodId == 116459265 ? CompressionType.None
                : CompressionType.Unknown;
        }
    }

    public static byte[]? GetAesCoderInfoProps(this SevenZipArchiveEntry entry)
    {
        const ulong aesMethodId = 0x06F10701;
        return (byte[]?)entry
            ?.GetCoders()
            ?.FirstOrDefault(x => GetCoderMethodId(x) == aesMethodId)
            ?.GetReflectionField("_props");
    }

    public static long GetFolderStartByteOffset(this SevenZipArchiveEntry entry)
    {
        var filePart = entry?.GetReflectionProperty("FilePart");
        var folder = filePart?.GetReflectionProperty("Folder");
        var database = filePart?.GetReflectionField("_database");
        var folderFirstPackStreamId = (int)folder?.GetReflectionField("_firstPackStreamId")!;
        var databaseDataStartPosition = (long)database?.GetReflectionField("_dataStartPosition")!;
        var databasePackStreamStartPositions = (List<long>)database?.GetReflectionField("_packStreamStartPositions")!;
        return databaseDataStartPosition + databasePackStreamStartPositions[folderFirstPackStreamId];
    }

    public static long? GetPackedSize(this SevenZipArchiveEntry? entry)
    {
        if (entry == null) return null;

        return TryGetLong(entry.GetReflectionProperty("CompressedSize"))
               ?? TryGetLong(entry.GetReflectionProperty("PackedSize"))
               ?? TryGetLong(entry.GetReflectionField("<CompressedSize>k__BackingField"))
               ?? TryGetLong(entry.GetReflectionField("<PackedSize>k__BackingField"))
               ?? TryGetFilePartPackedSize(entry);
    }

    private static long? TryGetFilePartPackedSize(SevenZipArchiveEntry entry)
    {
        try
        {
            var filePart = entry.GetReflectionProperty("FilePart");
            if (filePart == null) return null;

            return TryGetLong(filePart.GetReflectionProperty("PackedSize"))
                   ?? TryGetLong(filePart.GetReflectionProperty("CompressedSize"))
                   ?? TryGetLong(filePart.GetReflectionField("<PackedSize>k__BackingField"))
                   ?? TryGetLong(filePart.GetReflectionField("<CompressedSize>k__BackingField"));
        }
        catch
        {
            return null;
        }
    }

    private static long? TryGetLong(object? value)
    {
        return value switch
        {
            long longValue when longValue >= 0 => longValue,
            ulong ulongValue when ulongValue <= long.MaxValue => (long)ulongValue,
            int intValue when intValue >= 0 => intValue,
            uint uintValue => uintValue,
            _ => null
        };
    }

    private static IEnumerable<object?>? GetCoders(this SevenZipArchiveEntry entry)
    {
        var coders = (IEnumerable?)entry
            ?.GetFolder()
            ?.GetReflectionField("_coders");
        return coders?.Cast<object?>();
    }

    private static object? GetFolder(this SevenZipArchiveEntry entry)
    {
        return entry
            ?.GetReflectionProperty("FilePart")
            ?.GetReflectionProperty("Folder");
    }

    private static ulong? GetCoderMethodId(object? coder)
    {
        return (ulong?)coder
            ?.GetReflectionField("_methodId")
            ?.GetReflectionField("_id");
    }
}
