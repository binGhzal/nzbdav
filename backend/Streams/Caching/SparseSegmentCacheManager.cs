using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Streams.Caching;

public sealed class SparseSegmentCacheManager : IDisposable
{
    private const string CacheFileSuffix = ".nzbdav-cache.tmp";
    private const int MaxFetchBufferBytes = 256 * 1024;
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(1);
    private static readonly int DefaultMaxReadAheadTasks = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    public static SparseSegmentCacheManager Shared { get; } = new();

    private readonly ConcurrentDictionary<string, CacheFile> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _cleanedDirectories = new(StringComparer.Ordinal);
    private readonly Timer _maintenanceTimer;
    private readonly int _maxReadAheadTasks;
    private long _bytes;
    private long _hits;
    private long _misses;
    private long _evictions;
    private int _activeReadAheadTasks;
    private SparseSegmentCacheOptions _lastOptions = new();
    private bool _disposed;

    public SparseSegmentCacheManager(int? maxReadAheadTasks = null)
    {
        _maxReadAheadTasks = Math.Max(1, maxReadAheadTasks ?? DefaultMaxReadAheadTasks);
        _maintenanceTimer = new Timer(
            _ => EvictIfNeeded(),
            null,
            MaintenanceInterval,
            MaintenanceInterval);
    }

    public IFileRangeReader Open(
        string key,
        IFileRangeReader inner,
        SparseSegmentCacheOptions options,
        long? readLimitExclusive = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!options.Enabled) return inner;
        if (options.ChunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.ChunkBytes));
        if (options.MaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxBytes));

        _lastOptions = options;
        try
        {
            Directory.CreateDirectory(options.Directory);
            CleanupOrphanFilesOnce(options.Directory);
        }
        catch (Exception ex) when (IsCacheDirectoryUnavailable(ex))
        {
            return inner;
        }

        var cacheKey = CreateNamespacedKey(key, options);
        while (true)
        {
            CacheFile file;
            try
            {
                file = _files.GetOrAdd(cacheKey, _ =>
                {
                    var path = Path.Join(options.Directory, $"{cacheKey}{CacheFileSuffix}");
                    return new CacheFile(this, cacheKey, path, inner.Length, options);
                });
            }
            catch (Exception ex) when (IsCacheDirectoryUnavailable(ex))
            {
                _files.TryRemove(cacheKey, out _);
                return inner;
            }

            try
            {
                var lease = file.Open(inner, readLimitExclusive);
                EvictIfNeeded();
                return lease;
            }
            catch (ObjectDisposedException)
            {
                _files.TryRemove(new KeyValuePair<string, CacheFile>(file.Key, file));
            }
            catch (Exception ex) when (IsCacheDirectoryUnavailable(ex))
            {
                _files.TryRemove(new KeyValuePair<string, CacheFile>(file.Key, file));
                return inner;
            }
        }
    }

    public SparseSegmentCacheSnapshot GetSnapshot(SparseSegmentCacheOptions? currentOptions = null)
    {
        return new SparseSegmentCacheSnapshot(
            Bytes: Interlocked.Read(ref _bytes),
            MaxBytes: currentOptions?.MaxBytes ?? _lastOptions.MaxBytes,
            Hits: Interlocked.Read(ref _hits),
            Misses: Interlocked.Read(ref _misses),
            Evictions: Interlocked.Read(ref _evictions),
            Files: _files.Count,
            ActiveReaders: _files.Values.Sum(x => x.ActiveReaders),
            ReadAheadActive: Volatile.Read(ref _activeReadAheadTasks),
            PendingFetches: _files.Values.Sum(x => x.PendingFetches)
        );
    }

    public static string CreateKey(IEnumerable<string> segmentIds, long fileSize)
    {
        using var sha256 = SHA256.Create();
        var sizeBytes = BitConverter.GetBytes(fileSize);
        sha256.TransformBlock(sizeBytes, 0, sizeBytes.Length, null, 0);

        foreach (var segmentId in segmentIds)
        {
            var bytes = Encoding.UTF8.GetBytes(segmentId);
            sha256.TransformBlock(bytes, 0, bytes.Length, null, 0);
            sha256.TransformBlock([0], 0, 1, null, 0);
        }

        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _maintenanceTimer.Dispose();
        foreach (var file in _files.Values)
        {
            if (!_files.TryRemove(new KeyValuePair<string, CacheFile>(file.Key, file))) continue;
            file.ForceEvict(out var evictedBytes);
            Interlocked.Add(ref _bytes, -evictedBytes);
        }
    }

    private static string CreateNamespacedKey(string contentKey, SparseSegmentCacheOptions options)
    {
        var namespaceKey = string.Join('|', [
            Path.GetFullPath(options.Directory),
            options.ChunkBytes.ToString(),
            options.ReadAheadBytes.ToString(),
            options.NoProgressTimeout.Ticks.ToString(),
            contentKey
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(namespaceKey))).ToLowerInvariant();
    }

    private void CleanupOrphanFilesOnce(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!_cleanedDirectories.TryAdd(fullPath, 0)) return;

        foreach (var file in EnumerateCacheFiles(fullPath))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (IsCacheDirectoryUnavailable(ex) || ex is FileNotFoundException)
            {
            }
        }
    }

    private static IEnumerable<string> EnumerateCacheFiles(string directory)
    {
        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory
                .EnumerateFiles(directory, $"*{CacheFileSuffix}")
                .GetEnumerator();
        }
        catch (Exception ex) when (IsCacheDirectoryUnavailable(ex))
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string file;
                try
                {
                    if (!enumerator.MoveNext()) yield break;
                    file = enumerator.Current;
                }
                catch (Exception ex) when (IsCacheDirectoryUnavailable(ex))
                {
                    yield break;
                }

                yield return file;
            }
        }
    }

    private static bool IsCacheDirectoryUnavailable(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException
            or NotSupportedException
            or ArgumentException;
    }

    private void RecordHit()
    {
        Interlocked.Increment(ref _hits);
    }

    private void RecordMiss()
    {
        Interlocked.Increment(ref _misses);
    }

    private void AddBytes(long bytes)
    {
        Interlocked.Add(ref _bytes, bytes);
        EvictIfNeeded();
    }

    private bool TryEnterReadAhead()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activeReadAheadTasks);
            if (current >= _maxReadAheadTasks) return false;
            if (Interlocked.CompareExchange(ref _activeReadAheadTasks, current + 1, current) == current)
                return true;
        }
    }

    private void ExitReadAhead()
    {
        Interlocked.Decrement(ref _activeReadAheadTasks);
    }

    private void EvictIfNeeded()
    {
        var options = _lastOptions;
        var now = DateTimeOffset.UtcNow;
        var candidates = _files.Values
            .Where(x => x.ActiveReaders == 0 && x.PendingFetches == 0)
            .OrderBy(x => x.LastAccessUtc)
            .ToList();

        foreach (var file in candidates.Where(x => now - x.LastAccessUtc >= options.IdleTtl))
            TryEvict(file);

        foreach (var file in candidates)
        {
            if (Interlocked.Read(ref _bytes) <= options.MaxBytes) break;
            TryEvict(file);
        }
    }

    private void TryEvict(CacheFile file)
    {
        if (!_files.TryRemove(new KeyValuePair<string, CacheFile>(file.Key, file))) return;
        if (!file.TryEvict(out var evictedBytes))
        {
            _files.TryAdd(file.Key, file);
            return;
        }

        Interlocked.Add(ref _bytes, -evictedBytes);
        Interlocked.Increment(ref _evictions);
    }

    private sealed class CacheFile : IDisposable
    {
        private readonly SparseSegmentCacheManager _manager;
        private readonly string _path;
        private readonly long _length;
        private readonly SparseSegmentCacheOptions _options;
        private readonly FileStream _stream;
        private readonly SafeFileHandle _handle;
        private readonly CancellationTokenSource _disposeCts = new();
        private readonly object _stateLock = new();
        private readonly HashSet<long> _completedChunks = [];
        private readonly ConcurrentDictionary<long, byte> _scheduledReadAheadChunks = new();
        private readonly ConcurrentDictionary<long, Lazy<Task>> _inFlightFetches = new();
        private long _cachedBytes;
        private int _activeReaders;
        private int _activeReadAheadTasks;
        private bool _disposed;

        public CacheFile(
            SparseSegmentCacheManager manager,
            string key,
            string path,
            long length,
            SparseSegmentCacheOptions options)
        {
            _manager = manager;
            Key = key;
            _path = path;
            _length = length;
            _options = options;
            LastAccessUtc = DateTimeOffset.UtcNow;
            _stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            _stream.SetLength(length);
            _handle = _stream.SafeFileHandle;
        }

        public string Key { get; }
        public DateTimeOffset LastAccessUtc { get; private set; }
        public int ActiveReaders => Volatile.Read(ref _activeReaders);
        public int PendingFetches => _inFlightFetches.Count;

        public IFileRangeReader Open(IFileRangeReader inner, long? readLimitExclusive)
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _activeReaders++;
            }

            Touch();
            return new Lease(this, inner, Math.Clamp(readLimitExclusive ?? _length, 0, _length));
        }

        public async ValueTask<int> ReadAtAsync(
            IFileRangeReader inner,
            long offset,
            Memory<byte> buffer,
            long readLimitExclusive,
            CancellationToken readAheadToken,
            CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (offset >= readLimitExclusive || buffer.Length == 0) return 0;

            Touch();
            var remaining = (int)Math.Min(buffer.Length, readLimitExclusive - offset);
            var totalRead = 0;
            while (totalRead < remaining)
            {
                var absoluteOffset = offset + totalRead;
                var chunkIndex = absoluteOffset / _options.ChunkBytes;
                var chunkOffset = (int)(absoluteOffset % _options.ChunkBytes);
                var bytesFromChunk = Math.Min(remaining - totalRead, _options.ChunkBytes - chunkOffset);
                var chunkStart = chunkIndex * _options.ChunkBytes;
                var chunkEndExclusive = Math.Min(_length, chunkStart + _options.ChunkBytes);

                if (readLimitExclusive < chunkEndExclusive && !IsChunkCompleted(chunkIndex))
                {
                    var directRead = await ReadDirectAsync(
                            inner,
                            absoluteOffset,
                            buffer.Slice(totalRead, bytesFromChunk),
                            ct)
                        .ConfigureAwait(false);
                    if (directRead <= 0) break;
                    totalRead += directRead;
                    if (directRead < bytesFromChunk) break;
                    continue;
                }

                await EnsureChunkAsync(chunkIndex, inner, ct).ConfigureAwait(false);
                StartReadAhead(chunkIndex + 1, inner, readLimitExclusive, readAheadToken);
                var read = await ReadFromCacheAsync(
                    absoluteOffset,
                    buffer.Slice(totalRead, bytesFromChunk),
                    ct).ConfigureAwait(false);
                if (read < bytesFromChunk)
                {
                    await RefetchCompletedChunkAsync(chunkIndex, inner, ct).ConfigureAwait(false);
                    read = await ReadFromCacheAsync(
                        absoluteOffset,
                        buffer.Slice(totalRead, bytesFromChunk),
                        ct).ConfigureAwait(false);
                }

                if (read < bytesFromChunk)
                    throw new IOException($"Cached chunk {chunkIndex} could not satisfy read at offset {absoluteOffset}.");
                totalRead += read;
            }

            return totalRead;
        }

        private bool IsChunkCompleted(long chunkIndex)
        {
            lock (_stateLock)
                return _completedChunks.Contains(chunkIndex);
        }

        private async ValueTask<int> ReadDirectAsync(
            IFileRangeReader inner,
            long offset,
            Memory<byte> buffer,
            CancellationToken ct)
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _disposeCts.Token,
                    ct);
                timeoutCts.CancelAfter(_options.NoProgressTimeout);
                int read;
                try
                {
                    read = await inner
                        .ReadAtAsync(offset + totalRead, buffer[totalRead..], timeoutCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException e)
                    when (timeoutCts.IsCancellationRequested
                          && !ct.IsCancellationRequested
                          && !_disposeCts.IsCancellationRequested)
                {
                    throw new RetryableDownloadException(
                        $"No progress reading cache direct range at offset {offset + totalRead} " +
                        $"within {_options.NoProgressTimeout}.",
                        e);
                }

                if (read <= 0)
                {
                    throw new IOException($"No progress reading cache direct range at offset {offset + totalRead}.");
                }

                totalRead += read;
            }

            return totalRead;
        }

        public bool TryEvict(out long evictedBytes)
        {
            lock (_stateLock)
            {
                if (_disposed || _activeReaders > 0 || _inFlightFetches.Count > 0)
                {
                    evictedBytes = 0;
                    return false;
                }

                _disposed = true;
                evictedBytes = _cachedBytes;
            }

            _disposeCts.Cancel();
            Dispose();
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return true;
        }

        public void ForceEvict(out long evictedBytes)
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    evictedBytes = 0;
                    return;
                }

                _disposed = true;
                evictedBytes = _cachedBytes;
            }

            _disposeCts.Cancel();
            Dispose();
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        public void Dispose()
        {
            _disposeCts.Dispose();
            _stream.Dispose();
        }

        private async ValueTask<int> ReadFromCacheAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await RandomAccess
                    .ReadAsync(_handle, buffer[totalRead..], offset + totalRead, ct)
                    .ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            return totalRead;
        }

        private Task EnsureChunkAsync(long chunkIndex, IFileRangeReader inner, CancellationToken ct)
        {
            while (true)
            {
                lock (_stateLock)
                {
                    if (_completedChunks.Contains(chunkIndex))
                    {
                        _manager.RecordHit();
                        return Task.CompletedTask;
                    }
                }

                if (_inFlightFetches.TryGetValue(chunkIndex, out var existing))
                    return existing.Value.WaitAsync(ct);

                var lazy = CreateFetch(chunkIndex, inner);
                if (!_inFlightFetches.TryAdd(chunkIndex, lazy)) continue;

                _manager.RecordMiss();
                return lazy.Value.WaitAsync(ct);
            }
        }

        private async Task RefetchCompletedChunkAsync(long chunkIndex, IFileRangeReader inner, CancellationToken ct)
        {
            var chunkBytes = GetChunkByteCount(chunkIndex);
            if (chunkBytes <= 0) return;
            var removedCompletedChunk = false;

            lock (_stateLock)
            {
                if (_completedChunks.Remove(chunkIndex))
                {
                    removedCompletedChunk = true;
                    _cachedBytes = Math.Max(0, _cachedBytes - chunkBytes);
                    _stream.SetLength(_length);
                }
            }

            if (removedCompletedChunk)
                _manager.AddBytes(-chunkBytes);

            await EnsureChunkAsync(chunkIndex, inner, ct).ConfigureAwait(false);
        }

        private void StartReadAhead(
            long firstChunkIndex,
            IFileRangeReader inner,
            long readLimitExclusive,
            CancellationToken readAheadToken)
        {
            var readAheadBytes = _options.ReadAheadBytes;
            if (readAheadBytes <= 0 || readAheadToken.IsCancellationRequested) return;
            if (Volatile.Read(ref _activeReadAheadTasks) >= 1) return;

            var chunkCount = (readAheadBytes + _options.ChunkBytes - 1) / _options.ChunkBytes;
            var maxChunkIndex = (readLimitExclusive + _options.ChunkBytes - 1) / _options.ChunkBytes;
            var chunks = new List<long>();
            for (var chunkIndex = firstChunkIndex;
                 chunkIndex < maxChunkIndex && chunkIndex < firstChunkIndex + chunkCount;
                 chunkIndex++)
            {
                lock (_stateLock)
                {
                    if (_completedChunks.Contains(chunkIndex)) continue;
                }

                if (_inFlightFetches.ContainsKey(chunkIndex)) continue;
                if (!_scheduledReadAheadChunks.TryAdd(chunkIndex, 0)) continue;
                chunks.Add(chunkIndex);
            }

            if (chunks.Count == 0) return;
            if (Interlocked.Increment(ref _activeReadAheadTasks) > 1)
            {
                Interlocked.Decrement(ref _activeReadAheadTasks);
                foreach (var chunkIndex in chunks)
                    _scheduledReadAheadChunks.TryRemove(chunkIndex, out _);
                return;
            }

            if (!_manager.TryEnterReadAhead())
            {
                Interlocked.Decrement(ref _activeReadAheadTasks);
                foreach (var chunkIndex in chunks)
                    _scheduledReadAheadChunks.TryRemove(chunkIndex, out _);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var chunkIndex in chunks)
                    {
                        readAheadToken.ThrowIfCancellationRequested();
                        await EnsureChunkAsync(chunkIndex, inner, readAheadToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Read-ahead is opportunistic; foreground reads will retry and surface errors.
                }
                finally
                {
                    foreach (var chunkIndex in chunks)
                        _scheduledReadAheadChunks.TryRemove(chunkIndex, out _);
                    Interlocked.Decrement(ref _activeReadAheadTasks);
                    _manager.ExitReadAhead();
                }
            });
        }

        private Lazy<Task> CreateFetch(long chunkIndex, IFileRangeReader inner)
        {
            Lazy<Task>? lazy = null;
            lazy = new Lazy<Task>(
                () => FetchAndRemoveAsync(chunkIndex, inner, lazy!),
                LazyThreadSafetyMode.ExecutionAndPublication);
            return lazy;
        }

        private async Task FetchAndRemoveAsync(long chunkIndex, IFileRangeReader inner, Lazy<Task> lazy)
        {
            try
            {
                await FetchChunkAsync(chunkIndex, inner).ConfigureAwait(false);
            }
            finally
            {
                _inFlightFetches.TryRemove(new KeyValuePair<long, Lazy<Task>>(chunkIndex, lazy));
            }
        }

        private async Task FetchChunkAsync(long chunkIndex, IFileRangeReader inner)
        {
            var chunkStart = chunkIndex * _options.ChunkBytes;
            var bytesToRead = GetChunkByteCount(chunkIndex);
            if (bytesToRead <= 0) return;

            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(bytesToRead, MaxFetchBufferBytes));
            try
            {
                var totalRead = 0;
                while (totalRead < bytesToRead)
                {
                    var bytesRemaining = bytesToRead - totalRead;
                    var requestBytes = Math.Min(buffer.Length, bytesRemaining);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                    timeoutCts.CancelAfter(_options.NoProgressTimeout);
                    int read;
                    try
                    {
                        read = await inner
                            .ReadAtAsync(
                                chunkStart + totalRead,
                                buffer.AsMemory(0, requestBytes),
                                timeoutCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException e)
                        when (timeoutCts.IsCancellationRequested && !_disposeCts.IsCancellationRequested)
                    {
                        throw new RetryableDownloadException(
                            $"No progress reading cache chunk {chunkIndex} at offset {chunkStart + totalRead} " +
                            $"within {_options.NoProgressTimeout}.",
                            e);
                    }

                    if (read <= 0)
                    {
                        throw new IOException($"No progress reading cache chunk {chunkIndex} at offset {chunkStart + totalRead}.");
                    }

                    await WriteChunkAsync(
                            chunkStart + totalRead,
                            buffer.AsMemory(0, read),
                            _disposeCts.Token)
                        .ConfigureAwait(false);

                    totalRead += read;
                }

                lock (_stateLock)
                {
                    if (_completedChunks.Add(chunkIndex))
                    {
                        _cachedBytes += bytesToRead;
                        _manager.AddBytes(bytesToRead);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private int GetChunkByteCount(long chunkIndex)
        {
            var chunkStart = chunkIndex * _options.ChunkBytes;
            return (int)Math.Min(_options.ChunkBytes, _length - chunkStart);
        }

        private async Task WriteChunkAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            var written = 0;
            while (written < buffer.Length)
            {
                await RandomAccess
                    .WriteAsync(_handle, buffer[written..], offset + written, ct)
                    .ConfigureAwait(false);
                written = buffer.Length;
            }
        }

        private void Release()
        {
            Interlocked.Decrement(ref _activeReaders);
            Touch();
            _manager.EvictIfNeeded();
        }

        private void Touch()
        {
            LastAccessUtc = DateTimeOffset.UtcNow;
        }

        private sealed class Lease(
            CacheFile cacheFile,
            IFileRangeReader inner,
            long readLimitExclusive) : IFileRangeReader, IDisposable, IAsyncDisposable
        {
            private readonly CancellationTokenSource _readAheadCts = new();
            private bool _disposed;

            public long Length => readLimitExclusive;

            public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    return await cacheFile.ReadAtAsync(inner, offset, buffer, readLimitExclusive, _readAheadCts.Token, ct)
                        .ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (!ct.IsCancellationRequested)
                {
                    return await ReadDirectFromSourceAsync(inner, offset, buffer, readLimitExclusive, ct)
                        .ConfigureAwait(false);
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _readAheadCts.Cancel();
                _readAheadCts.Dispose();
                cacheFile.Release();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            private static async ValueTask<int> ReadDirectFromSourceAsync(
                IFileRangeReader inner,
                long offset,
                Memory<byte> buffer,
                long limitExclusive,
                CancellationToken ct)
            {
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
                if (offset >= limitExclusive || buffer.Length == 0) return 0;

                var remaining = (int)Math.Min(buffer.Length, limitExclusive - offset);
                var totalRead = 0;
                while (totalRead < remaining)
                {
                    var read = await inner
                        .ReadAtAsync(offset + totalRead, buffer[totalRead..remaining], ct)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new IOException(
                            $"Source reader ended before satisfying cache fallback read at offset {offset + totalRead}.");
                    }

                    totalRead += read;
                }

                return totalRead;
            }
        }
    }
}
