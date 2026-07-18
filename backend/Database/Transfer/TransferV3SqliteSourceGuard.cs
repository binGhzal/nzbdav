using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3SqliteSourceGuard : IDisposable
{
    private readonly SafeFileHandle _directory;
    private readonly SafeFileHandle _source;
    private readonly string _sourceName;
    private readonly TransferV3FileFingerprint _directoryFingerprint;
    private readonly TransferV3FileFingerprint _sourceFingerprint;
    private bool _disposed;

    private TransferV3SqliteSourceGuard(
        SafeFileHandle directory,
        SafeFileHandle source,
        string sourceName,
        TransferV3FileFingerprint directoryFingerprint,
        TransferV3FileFingerprint sourceFingerprint)
    {
        _directory = directory;
        _source = source;
        _sourceName = sourceName;
        _directoryFingerprint = directoryFingerprint;
        _sourceFingerprint = sourceFingerprint;
    }

    internal static TransferV3SqliteSourceGuard Open(string sourcePath)
    {
        if (!TransferV3Posix.IsSupported)
            throw new PlatformNotSupportedException(
                "Transfer-v3 source validation requires Linux x64/arm64 or macOS arm64 with a verified POSIX ABI.");
        var fullPath = Path.GetFullPath(sourcePath);
        var directoryPath = Path.GetDirectoryName(fullPath);
        var sourceName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(directoryPath) || string.IsNullOrEmpty(sourceName))
            throw Failure("invalid-source-path");

        SafeFileHandle? directory = null;
        SafeFileHandle? source = null;
        try
        {
            directory = TransferV3Posix.OpenDirectory(directoryPath);
            EnsureNoSidecars(directory, sourceName);
            source = TransferV3Posix.OpenReadOnlyRegularFileAt(directory, sourceName);
            var sourceFingerprint = TransferV3Posix.GetFingerprint(source);
            var directoryFingerprint = TransferV3Posix.GetFingerprint(directory);
            var guard = new TransferV3SqliteSourceGuard(
                directory, source, sourceName, directoryFingerprint, sourceFingerprint);
            directory = null;
            source = null;
            return guard;
        }
        catch (TransferV3SourceValidationException)
        {
            throw;
        }
        catch (IOException)
        {
            throw Failure("source-open");
        }
        finally
        {
            source?.Dispose();
            directory?.Dispose();
        }
    }

    internal TransferV3FileIdentity Identity
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sourceFingerprint.Identity;
        }
    }

    internal string ProbeSqliteDescriptorUri()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return TransferV3Posix.ProbeSqliteDescriptorRoute(_source);
        }
        catch (IOException)
        {
            throw Failure("descriptor-route");
        }
    }

    internal void VerifyUnchanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            EnsureNoSidecars(_directory, _sourceName);
            if (TransferV3Posix.GetFingerprint(_source) != _sourceFingerprint)
                throw Failure("source-mutated");
            using var current = TransferV3Posix.OpenReadOnlyRegularFileAt(_directory, _sourceName);
            if (TransferV3Posix.GetFingerprint(current) != _sourceFingerprint
                || TransferV3Posix.GetFingerprint(_directory) != _directoryFingerprint)
                throw Failure("source-entry-mutated");
        }
        catch (TransferV3SourceValidationException)
        {
            throw;
        }
        catch (IOException)
        {
            throw Failure("source-recheck");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.Dispose();
        _directory.Dispose();
    }

    private static void EnsureNoSidecars(SafeFileHandle directory, string sourceName)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (TransferV3Posix.EntryExistsNoFollow(directory, sourceName + suffix))
                throw Failure("source-sidecar");
        }
    }

    private static TransferV3SourceValidationException Failure(string reason) =>
        TransferV3SourceValidationException.Create(
            "source-stability",
            "<source>",
            "path",
            0,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reason)))
                .ToLowerInvariant()[..12]);
}
