using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3BlobSourceGuard : IDisposable
{
    private readonly SafeFileHandle _parent;
    private readonly SafeFileHandle _root;
    private readonly string _rootName;
    private readonly TransferV3FileFingerprint _parentFingerprint;
    private readonly TransferV3FileFingerprint _rootFingerprint;
    private bool _disposed;

    private TransferV3BlobSourceGuard(
        SafeFileHandle parent,
        SafeFileHandle root,
        string rootName,
        TransferV3FileFingerprint parentFingerprint,
        TransferV3FileFingerprint rootFingerprint)
    {
        _parent = parent;
        _root = root;
        _rootName = rootName;
        _parentFingerprint = parentFingerprint;
        _rootFingerprint = rootFingerprint;
    }

    internal static TransferV3BlobSourceGuard Open(string blobRootPath)
    {
        if (!TransferV3Posix.IsSupported)
            throw new PlatformNotSupportedException(
                "Transfer-v3 blob validation requires Linux x64/arm64 or macOS arm64 with a verified POSIX ABI.");
        ArgumentException.ThrowIfNullOrWhiteSpace(blobRootPath);
        var fullPath = Path.GetFullPath(blobRootPath);
        var parentPath = Path.GetDirectoryName(fullPath);
        var rootName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(rootName))
            throw Failure("invalid-root");

        SafeFileHandle? parent = null;
        SafeFileHandle? root = null;
        try
        {
            parent = TransferV3Posix.OpenDirectory(parentPath);
            root = TransferV3Posix.OpenDirectoryAt(parent, rootName);
            var guard = new TransferV3BlobSourceGuard(
                parent,
                root,
                rootName,
                TransferV3Posix.GetFingerprint(parent),
                TransferV3Posix.GetFingerprint(root));
            parent = null;
            root = null;
            return guard;
        }
        catch (TransferV3SourceValidationException)
        {
            throw;
        }
        catch (IOException)
        {
            throw Failure("root-open");
        }
        finally
        {
            root?.Dispose();
            parent?.Dispose();
        }
    }

    internal TransferV3FileIdentity Identity
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _rootFingerprint.Identity;
        }
    }

    internal SafeFileHandle RootHandle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _root;
        }
    }

    internal void VerifyUnchanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            if (TransferV3Posix.GetFingerprint(_root) != _rootFingerprint
                || TransferV3Posix.GetFingerprint(_parent) != _parentFingerprint)
            {
                throw Failure("root-mutated");
            }

            using var current = TransferV3Posix.OpenDirectoryAt(_parent, _rootName);
            if (TransferV3Posix.GetFingerprint(current) != _rootFingerprint)
                throw Failure("root-entry-replaced");
        }
        catch (TransferV3SourceValidationException)
        {
            throw;
        }
        catch (IOException)
        {
            throw Failure("root-recheck");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _root.Dispose();
        _parent.Dispose();
    }

    internal static TransferV3SourceValidationException Failure(string reason) =>
        TransferV3SourceValidationException.Create(
            "blob-layout",
            "@blob",
            "path",
            0,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reason)))
                .ToLowerInvariant()[..12]);
}
