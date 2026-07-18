using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using SQLitePCL;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3BlobInventoryScanner
{
    private const int BufferBytes = 16 * 1024;

    internal static async Task<TransferV3BlobInventory> ScanAsync(
        string blobRootPath,
        SqliteConnection connection,
        SqliteTransaction transaction,
        IProgress<TransferV3ValidationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!TransferV3Posix.IsSupported)
            throw new PlatformNotSupportedException(
                "Transfer-v3 blob validation requires Linux x64/arm64 or macOS arm64 with a verified POSIX ABI.");
        using var guard = TransferV3BlobSourceGuard.Open(blobRootPath);
        return await ScanAsync(
            guard, connection, transaction, progress, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<TransferV3BlobInventory> ScanAsync(
        TransferV3BlobSourceGuard guard,
        SqliteConnection connection,
        SqliteTransaction transaction,
        IProgress<TransferV3ValidationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guard);
        guard.VerifyUnchanged();
        progress?.Report(new TransferV3ValidationProgress("blob-root-opened", "@blob", 0));
        foreach (var firstName in TransferV3Posix.EnumerateDirectoryNames(guard.RootHandle))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsLowerHexPair(firstName)) throw LayoutFailure(firstName);
            using var first = OpenDirectoryOrLayout(guard.RootHandle, firstName, firstName);
            var firstFingerprint = TransferV3Posix.GetFingerprint(first);
            await InsertFirstShardAsync(
                connection, transaction, firstName, firstFingerprint, cancellationToken).ConfigureAwait(false);

            foreach (var secondName in TransferV3Posix.EnumerateDirectoryNames(first))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var secondRelative = firstName + "/" + secondName;
                if (!IsLowerHexPair(secondName)) throw LayoutFailure(secondRelative);
                using var second = OpenDirectoryOrLayout(first, secondName, secondRelative);
                var secondFingerprint = TransferV3Posix.GetFingerprint(second);
                await InsertSecondShardAsync(
                        connection, transaction, firstName, secondName, secondFingerprint, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var entryName in TransferV3Posix.EnumerateDirectoryNames(second))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = secondRelative + "/" + entryName;
                    var id = ParseCanonicalBlobId(firstName, secondName, entryName, relative);
                    progress?.Report(new TransferV3ValidationProgress(
                        "blob-entry-enumerated", "@blob", 0));
                    using var file = OpenFileOrLayout(second, entryName, relative);
                    var fileFingerprint = TransferV3Posix.GetFingerprint(file);
                    (long length, byte[] digest) value;
                    try
                    {
                        value = await HashFileAsync(file, progress, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        throw LayoutFailure(relative);
                    }
                    if (value.length < 0 || value.length != fileFingerprint.Size)
                        throw LayoutFailure("file-length");
                    await InsertBlobAsync(
                            connection,
                            transaction,
                            id,
                            firstName,
                            secondName,
                            value.length,
                            value.digest,
                            fileFingerprint,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                EnsureUnchanged(secondFingerprint, second, "second-directory-mutated");
            }
            EnsureUnchanged(firstFingerprint, first, "first-directory-mutated");
        }
        guard.VerifyUnchanged();
        await RejectDuplicateFileIdentitiesAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);
        return await ComputeAggregateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task VerifyRetainedAsync(
        TransferV3BlobSourceGuard guard,
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IProgress<TransferV3ValidationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guard);
        guard.VerifyUnchanged();
        long firstCount = 0;
        long secondCount = 0;
        long fileCount = 0;
        foreach (var firstName in TransferV3Posix.EnumerateDirectoryNames(guard.RootHandle))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsLowerHexPair(firstName)) throw LayoutFailure(firstName);
            using var first = OpenDirectoryOrLayout(guard.RootHandle, firstName, firstName);
            var firstFingerprint = TransferV3Posix.GetFingerprint(first);
            await RequireFingerprintAsync(
                    connection,
                    transaction,
                    "blob_first_shards",
                    "first_name = $first",
                    [("$first", NameBytes(firstName))],
                    firstFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
            firstCount++;

            foreach (var secondName in TransferV3Posix.EnumerateDirectoryNames(first))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var secondRelative = firstName + "/" + secondName;
                if (!IsLowerHexPair(secondName)) throw LayoutFailure(secondRelative);
                using var second = OpenDirectoryOrLayout(first, secondName, secondRelative);
                var secondFingerprint = TransferV3Posix.GetFingerprint(second);
                await RequireFingerprintAsync(
                        connection,
                        transaction,
                        "blob_second_shards",
                        "first_name = $first AND second_name = $second",
                        [("$first", NameBytes(firstName)), ("$second", NameBytes(secondName))],
                        secondFingerprint,
                        cancellationToken)
                    .ConfigureAwait(false);
                secondCount++;

                foreach (var entryName in TransferV3Posix.EnumerateDirectoryNames(second))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = secondRelative + "/" + entryName;
                    var id = ParseCanonicalBlobId(firstName, secondName, entryName, relative);
                    using var file = OpenFileOrLayout(second, entryName, relative);
                    var fileFingerprint = TransferV3Posix.GetFingerprint(file);
                    var value = await HashFileAsync(file, progress, cancellationToken).ConfigureAwait(false);
                    await RequireBlobAsync(
                            connection,
                            transaction,
                            id,
                            firstName,
                            secondName,
                            value.Length,
                            value.Digest,
                            fileFingerprint,
                            cancellationToken)
                        .ConfigureAwait(false);
                    fileCount++;
                }
                EnsureUnchanged(secondFingerprint, second, "second-directory-mutated");
            }
            EnsureUnchanged(firstFingerprint, first, "first-directory-mutated");
        }
        guard.VerifyUnchanged();
        await RequireCountsAsync(
                connection, transaction, firstCount, secondCount, fileCount, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<TransferV3BlobInventory> ComputeAggregateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        long totalBytes = 0;
        var lengthBytes = new byte[sizeof(long)];
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT normalized_uuid, length_bytes, content_sha256 "
            + "FROM scratch.blob_inventory ORDER BY normalized_uuid;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var uuid = raw.sqlite3_column_blob(reader.Handle, 0);
            var length = reader.GetInt64(1);
            var digest = raw.sqlite3_column_blob(reader.Handle, 2);
            aggregate.AppendData(uuid);
            BinaryPrimitives.WriteInt64BigEndian(lengthBytes, length);
            aggregate.AppendData(lengthBytes);
            aggregate.AppendData(digest);
            count++;
            totalBytes = checked(totalBytes + length);
        }
        return new TransferV3BlobInventory(
            count,
            totalBytes,
            Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task RejectDuplicateFileIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // The canonical 56-byte fingerprint begins with big-endian device and
        // inode values. Grouping that fixed 16-byte identity in the existing
        // unnamed scratch table avoids an unbounded managed identity set and
        // leaves the retained Task 2 scratch schema unchanged.
        command.CommandText =
            "SELECT 1 FROM scratch.blob_inventory "
            + "GROUP BY substr(file_fingerprint, 1, 16) "
            + "HAVING count(*) > 1 LIMIT 1;";
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw LayoutFailure("duplicate-blob-identity");
        }
    }

    private static async Task<(long Length, byte[] Digest)> HashFileAsync(
        SafeFileHandle handle,
        IProgress<TransferV3ValidationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var before = TransferV3Posix.GetFingerprint(handle);
        await using var stream = new FileStream(handle, FileAccess.Read, BufferBytes, isAsync: false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        long length = 0;
        var reportedFirstChunk = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            length = checked(length + read);
            if (!reportedFirstChunk)
            {
                reportedFirstChunk = true;
                progress?.Report(new TransferV3ValidationProgress(
                    "blob-first-chunk-read", "@blob", 0));
            }
        }
        if (TransferV3Posix.GetFingerprint(stream.SafeFileHandle) != before || length != before.Size)
            throw LayoutFailure("file-mutated");
        return (length, hash.GetHashAndReset());
    }

    private static async Task InsertFirstShardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string first,
        TransferV3FileFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO scratch.blob_first_shards(first_name, fingerprint) VALUES ($first, $fingerprint);";
        command.Parameters.Add("$first", SqliteType.Blob).Value = NameBytes(first);
        command.Parameters.Add("$fingerprint", SqliteType.Blob).Value = TransferV3Posix.EncodeFingerprint(fingerprint);
        await ExecuteUniqueInsertAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSecondShardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string first,
        string second,
        TransferV3FileFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO scratch.blob_second_shards(first_name, second_name, fingerprint) "
            + "VALUES ($first, $second, $fingerprint);";
        command.Parameters.Add("$first", SqliteType.Blob).Value = NameBytes(first);
        command.Parameters.Add("$second", SqliteType.Blob).Value = NameBytes(second);
        command.Parameters.Add("$fingerprint", SqliteType.Blob).Value = TransferV3Posix.EncodeFingerprint(fingerprint);
        await ExecuteUniqueInsertAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertBlobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        string first,
        string second,
        long length,
        byte[] digest,
        TransferV3FileFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO scratch.blob_inventory("
            + "normalized_uuid, first_name, second_name, length_bytes, content_sha256, file_fingerprint) "
            + "VALUES ($uuid, $first, $second, $length, $digest, $fingerprint);";
        command.Parameters.Add("$uuid", SqliteType.Blob).Value = GuidNetworkBytes(id);
        command.Parameters.Add("$first", SqliteType.Blob).Value = NameBytes(first);
        command.Parameters.Add("$second", SqliteType.Blob).Value = NameBytes(second);
        command.Parameters.AddWithValue("$length", length);
        command.Parameters.Add("$digest", SqliteType.Blob).Value = digest;
        command.Parameters.Add("$fingerprint", SqliteType.Blob).Value = TransferV3Posix.EncodeFingerprint(fingerprint);
        await ExecuteUniqueInsertAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteUniqueInsertAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw LayoutFailure("duplicate-blob-identity");
        }
    }

    private static async Task RequireFingerprintAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string predicate,
        IReadOnlyList<(string Name, byte[] Value)> parameters,
        TransferV3FileFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT fingerprint FROM scratch.{table} WHERE {predicate};";
        foreach (var parameter in parameters)
            command.Parameters.Add(parameter.Name, SqliteType.Blob).Value = parameter.Value;
        var expected = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
        if (expected is null
            || !CryptographicOperations.FixedTimeEquals(
                expected,
                TransferV3Posix.EncodeFingerprint(fingerprint)))
        {
            throw LayoutFailure("directory-replaced");
        }
    }

    private static async Task RequireBlobAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid id,
        string first,
        string second,
        long length,
        byte[] digest,
        TransferV3FileFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT first_name, second_name, length_bytes, content_sha256, file_fingerprint "
            + "FROM scratch.blob_inventory WHERE normalized_uuid = $uuid;";
        command.Parameters.Add("$uuid", SqliteType.Blob).Value = GuidNetworkBytes(id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !reader.GetFieldValue<byte[]>(0).AsSpan().SequenceEqual(NameBytes(first))
            || !reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(NameBytes(second))
            || reader.GetInt64(2) != length
            || !CryptographicOperations.FixedTimeEquals(reader.GetFieldValue<byte[]>(3), digest)
            || !CryptographicOperations.FixedTimeEquals(
                reader.GetFieldValue<byte[]>(4), TransferV3Posix.EncodeFingerprint(fingerprint))
            || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw LayoutFailure("file-replaced");
        }
    }

    private static async Task RequireCountsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long first,
        long second,
        long files,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT (SELECT count(*) FROM scratch.blob_first_shards), "
            + "(SELECT count(*) FROM scratch.blob_second_shards), "
            + "(SELECT count(*) FROM scratch.blob_inventory);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetInt64(0) != first
            || reader.GetInt64(1) != second
            || reader.GetInt64(2) != files)
        {
            throw LayoutFailure("inventory-count");
        }
    }

    private static Guid ParseCanonicalBlobId(
        string first,
        string second,
        string entry,
        string relative)
    {
        if (!Guid.TryParseExact(entry, "D", out var id)
            || entry != id.ToString("D")
            || id.ToString("N")[..2] != first
            || id.ToString("N").Substring(2, 2) != second)
        {
            throw LayoutFailure(relative);
        }
        return id;
    }

    private static SafeFileHandle OpenDirectoryOrLayout(
        SafeFileHandle parent,
        string name,
        string relative)
    {
        try
        {
            return TransferV3Posix.OpenDirectoryAt(parent, name);
        }
        catch (IOException)
        {
            throw LayoutFailure(relative);
        }
    }

    private static SafeFileHandle OpenFileOrLayout(
        SafeFileHandle parent,
        string name,
        string relative)
    {
        try
        {
            return TransferV3Posix.OpenReadOnlyRegularFileAt(parent, name);
        }
        catch (IOException)
        {
            throw LayoutFailure(relative);
        }
    }

    private static void EnsureUnchanged(
        TransferV3FileFingerprint before,
        SafeFileHandle handle,
        string failure)
    {
        if (TransferV3Posix.GetFingerprint(handle) != before)
            throw LayoutFailure(failure);
    }

    private static bool IsLowerHexPair(string value) =>
        value.Length == 2 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] NameBytes(string value) => Encoding.ASCII.GetBytes(value);

    private static byte[] GuidNetworkBytes(Guid value)
    {
        var bytes = new byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out var written) || written != bytes.Length)
            throw new InvalidOperationException("Could not encode canonical UUID network bytes.");
        return bytes;
    }

    private static TransferV3SourceValidationException LayoutFailure(string value) =>
        TransferV3SourceValidationException.Create(
            "blob-layout",
            "@blob",
            "path",
            0,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..12]);
}
