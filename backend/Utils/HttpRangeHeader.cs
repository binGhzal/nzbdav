using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Utils;

public sealed record HttpRangeHeader(long? Start, long? End, long? SuffixLength)
{
    private const string BytesPrefix = "bytes=";

    public static HttpRangeHeader? Parse(string? rangeHeader)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader)) return null;

        rangeHeader = rangeHeader.Trim();
        if (!rangeHeader.StartsWith(BytesPrefix, StringComparison.OrdinalIgnoreCase)) return null;

        var rangeSpec = rangeHeader[BytesPrefix.Length..].Trim();
        if (rangeSpec.Length == 0 || rangeSpec.Contains(','))
            throw InvalidRangeHeader();

        var separatorIndex = rangeSpec.IndexOf('-');
        if (separatorIndex < 0 || separatorIndex != rangeSpec.LastIndexOf('-'))
            throw InvalidRangeHeader();

        var startText = rangeSpec[..separatorIndex].Trim();
        var endText = rangeSpec[(separatorIndex + 1)..].Trim();

        if (startText.Length == 0)
        {
            if (!TryParseNonNegativeLong(endText, out var suffixLength) || suffixLength <= 0)
                throw InvalidRangeHeader();

            return new HttpRangeHeader(null, null, suffixLength);
        }

        if (!TryParseNonNegativeLong(startText, out var start))
            throw InvalidRangeHeader();

        if (endText.Length == 0)
            return new HttpRangeHeader(start, null, null);

        if (!TryParseNonNegativeLong(endText, out var end) || end < start)
            throw InvalidRangeHeader();

        return new HttpRangeHeader(start, end, null);
    }

    public bool TryResolve(long fileLength, out ResolvedHttpRange range)
    {
        if (fileLength < 0)
            throw new ArgumentOutOfRangeException(nameof(fileLength));

        if (SuffixLength is long suffixLength)
        {
            if (fileLength == 0)
            {
                range = default;
                return false;
            }

            var length = Math.Min(suffixLength, fileLength);
            range = new ResolvedHttpRange(fileLength - length, fileLength - 1);
            return true;
        }

        var start = Start ?? 0;
        if (start >= fileLength)
        {
            range = default;
            return false;
        }

        var end = Math.Min(End ?? fileLength - 1, fileLength - 1);
        if (start > end)
        {
            range = default;
            return false;
        }

        range = new ResolvedHttpRange(start, end);
        return true;
    }

    public static string GetSeekPositionForLog(string? rangeHeader, string defaultValue = "0")
    {
        try
        {
            var range = Parse(rangeHeader);
            if (range is null) return defaultValue;
            if (range.SuffixLength is long suffixLength)
                return $"-{suffixLength.ToString(CultureInfo.InvariantCulture)}";

            return range.Start?.ToString(CultureInfo.InvariantCulture) ?? defaultValue;
        }
        catch (BadHttpRequestException)
        {
            return "invalid";
        }
    }

    private static bool TryParseNonNegativeLong(string value, out long result)
    {
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;
    }

    private static BadHttpRequestException InvalidRangeHeader()
    {
        return new BadHttpRequestException("Invalid Range header.");
    }
}

public readonly record struct ResolvedHttpRange(long Start, long End)
{
    public long Length => End - Start + 1;
}
