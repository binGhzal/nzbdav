using System.Security.Cryptography;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3JsonlParser
{
    internal static async Task<TransferV3BufferMetrics> ParseAsync(
        Stream source,
        TransferV3Limits limits,
        ITransferV3FrameObserver observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(observer);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.CanRead)
            {
                throw new ArgumentException("The JSONL source must be readable.", nameof(source));
            }

            using var lineReader = new TransferV3Utf8LineReader(
                source,
                limits.MaxEncodedFrameBytes);
            using var state = new TransferV3FrameState(limits);
            long frameCount = 0;
            var maxFrameBytes = 0;
            var maxPayloadBytes = 0;
            var maxConcurrentDispatches = 0;
            long maxAccountedBytes = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await lineReader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    throw new FormatException("The JSONL table ended before its table-end frame.");
                }

                TransferV3Frame? frame = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        frame = TransferV3FrameCodec.ParseCanonical(line);
                        state.AcceptParsed(frame, line);
                    }
                    catch (FormatException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException
                            or ArgumentException
                            or OverflowException
                            or CryptographicException)
                    {
                        throw new FormatException(
                            "The JSONL frame violates the transfer sequence.",
                            exception);
                    }

                    var payloadBytes = frame switch
                    {
                        TransferV3RowFrame row => row.Data.Length,
                        TransferV3FieldChunkFrame chunk => chunk.Data.Length,
                        _ => 0,
                    };
                    frameCount++;
                    maxFrameBytes = Math.Max(maxFrameBytes, line.Length);
                    maxPayloadBytes = Math.Max(maxPayloadBytes, payloadBytes);
                    maxAccountedBytes = Math.Max(
                        maxAccountedBytes,
                        checked(2L * line.Length + payloadBytes));

                    if (frame is TransferV3TableEndFrame tableEnd)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        byte[]? trailing = null;
                        try
                        {
                            trailing = await lineReader.ReadLineAsync(cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (trailing is not null)
                            {
                                throw new FormatException(
                                    "The JSONL table contains trailing content.");
                            }
                        }
                        finally
                        {
                            if (trailing is not null)
                            {
                                CryptographicOperations.ZeroMemory(trailing);
                            }
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        maxConcurrentDispatches = Math.Max(maxConcurrentDispatches, 1);
                        observer.CompleteTable(tableEnd);
                        return new TransferV3BufferMetrics(
                            frameCount,
                            maxFrameBytes,
                            maxPayloadBytes,
                            maxConcurrentDispatches,
                            maxAccountedBytes);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    maxConcurrentDispatches = Math.Max(maxConcurrentDispatches, 1);
                    observer.Observe(frame);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (frame is TransferV3BatchEndFrame batchEnd)
                    {
                        observer.CommitBatch(batchEnd);
                    }
                }
                finally
                {
                    TransferV3FrameCodec.ClearDecodedPayload(frame);
                    CryptographicOperations.ZeroMemory(line);
                }
            }
        }
        catch (Exception primary)
        {
            try
            {
                observer.Abort(primary);
            }
            catch (Exception abortFailure)
            {
                throw new AggregateException(
                    "Transfer parsing failed and observer abort also failed.",
                    primary,
                    abortFailure);
            }

            throw;
        }
    }
}
