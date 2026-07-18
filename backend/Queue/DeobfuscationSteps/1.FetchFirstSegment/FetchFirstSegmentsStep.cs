// ReSharper disable InconsistentNaming

using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;

public static class FetchFirstSegmentsStep
{
    public static async Task<List<NzbFileWithFirstSegment>> FetchFirstSegments
    (
        List<NzbFile> nzbFiles,
        INntpClient usenetClient,
        ConfigManager configManager,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null
    )
    {
        return await nzbFiles
            .Where(x => x.Segments.Count > 0)
            .Select(x => FetchFirstSegment(x, usenetClient, cancellationToken))
            .WithConcurrencyAsync(configManager.GetAdaptiveQueueFileProcessingConcurrency())
            .GetAllAsync(cancellationToken, progress).ConfigureAwait(false);
    }

    private static async Task<NzbFileWithFirstSegment> FetchFirstSegment
    (
        NzbFile nzbFile,
        INntpClient usenetClient,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // NZB's standard file date lets the hot path use BODY instead of
            // ARTICLE. Fall back to ARTICLE for malformed/legacy NZBs where
            // the post timestamp is unavailable.
            var firstSegment = nzbFile.GetSegmentIds()[0];
            YencStream bodyStream;
            DateTimeOffset releaseDate;
            if (nzbFile.PostedAt.HasValue)
            {
                var body = await usenetClient
                    .DecodedBodyWithFallbackAsync(firstSegment, cancellationToken)
                    .ConfigureAwait(false);
                bodyStream = body.Stream!;
                releaseDate = nzbFile.PostedAt.Value;
            }
            else
            {
                var article = await usenetClient
                    .DecodedArticleWithFallbackAsync(firstSegment, cancellationToken)
                    .ConfigureAwait(false);
                bodyStream = article.Stream!;
                releaseDate = article.ArticleHeaders!.Date;
            }

            await using (bodyStream.ConfigureAwait(false))
            {

                // read up to the first 16KB from the stream
                var totalRead = 0;
                var buffer = new byte[16 * 1024];
                while (totalRead < buffer.Length)
                {
                    var read = await bodyStream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    totalRead += read;
                }

                // determine bytes read
                var first16KB = totalRead < buffer.Length
                    ? buffer.AsSpan(0, totalRead).ToArray()
                    : buffer;

                // get the yencHeaders
                var yencHeaders = await bodyStream
                    .GetYencHeadersAsync(cancellationToken)
                    .ConfigureAwait(false);

                // return
                return new NzbFileWithFirstSegment
                {
                    NzbFile = nzbFile,
                    First16KB = first16KB,
                    Header = yencHeaders,
                    MissingFirstSegment = false,
                    ReleaseDate = releaseDate
                };
            }
        }
        catch (UsenetArticleNotFoundException)
        {
            return new NzbFileWithFirstSegment
            {
                NzbFile = nzbFile,
                First16KB = null,
                Header = null,
                MissingFirstSegment = true,
                ReleaseDate = DateTimeOffset.UtcNow
            };
        }
    }

    public class NzbFileWithFirstSegment
    {
        private static readonly byte[] Rar4Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        private static readonly byte[] Rar5Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

        public required NzbFile NzbFile { get; init; }
        public required UsenetYencHeader? Header { get; init; }
        public required byte[]? First16KB { get; init; }
        public required bool MissingFirstSegment { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }

        public bool HasRar4Magic() => HasMagic(Rar4Magic);
        public bool HasRar5Magic() => HasMagic(Rar5Magic);

        private bool HasMagic(byte[] sequence)
        {
            return First16KB?.Length >= sequence.Length &&
                   First16KB.AsSpan(0, sequence.Length).SequenceEqual(sequence);
        }
    }
}
