using Microsoft.Win32.SafeHandles;

namespace NzbWebDAV.Database.Transfer;

internal sealed class TransferV3Phase4StagingParent : IDisposable
{
    private const uint FileTypeMask = 0xf000;
    private const uint DirectoryType = 0x4000;
    private const uint OwnerReadWriteExecute = 0x1c0;
    private const uint GroupOrOtherWrite = 0x12;

    private readonly object _gate = new();
    private readonly SafeFileHandle _handle;
    private readonly TransferV3FileStat _openedStat;
    private bool _disposed;

    private TransferV3Phase4StagingParent(
        SafeFileHandle handle,
        TransferV3FileStat openedStat)
    {
        _handle = handle;
        _openedStat = openedStat;
        Identity = openedStat.Fingerprint.Identity;
    }

    internal static TransferV3Phase4StagingParent OpenOwned(string absolutePath)
    {
        if (!IsValidAbsolutePosixDirectoryPath(absolutePath))
            throw ArgumentFailure();

        try
        {
            SafeFileHandle? retained = null;
            try
            {
                retained = TransferV3Posix.OpenDirectory(absolutePath);
                var openedStat = TransferV3Posix.GetFileStat(retained);
                ValidateOwnedStat(
                    openedStat,
                    TransferV3Posix.GetEffectiveUserId());
                if (!TransferV3Posix.DescriptorHasCloseOnExec(retained))
                    throw new IOException();

                var result = new TransferV3Phase4StagingParent(
                    retained,
                    openedStat);
                retained = null;
                return result;
            }
            finally
            {
                retained?.Dispose();
            }
        }
        catch (TransferV3Phase4Exception)
        {
            throw;
        }
        catch (Exception raw)
        {
            throw MapFailure(raw);
        }
    }

    internal static void ValidateOwnedStat(
        TransferV3FileStat stat,
        uint effectiveUserId)
    {
        if ((stat.Mode & FileTypeMask) != DirectoryType
            || stat.OwnerUid != effectiveUserId
            || (stat.Mode & OwnerReadWriteExecute) != OwnerReadWriteExecute
            || (stat.Mode & GroupOrOtherWrite) != 0)
        {
            throw PosixFailure();
        }
    }

    internal static void ValidateRetainedStat(
        TransferV3FileStat opened,
        TransferV3FileStat current,
        uint effectiveUserId)
    {
        ValidateOwnedStat(opened, effectiveUserId);
        ValidateOwnedStat(current, effectiveUserId);
        if (opened.Fingerprint.Identity != current.Fingerprint.Identity
            || opened.OwnerUid != current.OwnerUid
            || opened.Mode != current.Mode)
        {
            throw PosixFailure();
        }
    }

    internal TransferV3FileIdentity Identity { get; }

    internal SafeFileHandle DuplicateHandle()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            SafeFileHandle? duplicate = null;
            try
            {
                ValidateRetainedStat(
                    _openedStat,
                    TransferV3Posix.GetFileStat(_handle),
                    TransferV3Posix.GetEffectiveUserId());
                duplicate = TransferV3Posix.DuplicateHandle(_handle);
                if (!TransferV3Posix.DescriptorHasCloseOnExec(duplicate))
                    throw new IOException();

                var result = duplicate;
                duplicate = null;
                return result;
            }
            catch (TransferV3Phase4Exception)
            {
                throw;
            }
            catch (Exception raw)
            {
                throw MapFailure(raw);
            }
            finally
            {
                duplicate?.Dispose();
            }
        }
    }

    internal long GetAvailableBytes()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                ValidateRetainedStat(
                    _openedStat,
                    TransferV3Posix.GetFileStat(_handle),
                    TransferV3Posix.GetEffectiveUserId());
                return TransferV3Posix.GetAvailableBytes(_handle);
            }
            catch (TransferV3Phase4Exception)
            {
                throw;
            }
            catch (Exception raw)
            {
                throw MapFailure(raw);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                _handle.Dispose();
            }
            catch (Exception raw)
            {
                throw MapFailure(raw);
            }
        }
    }

    private static bool IsValidAbsolutePosixDirectoryPath(string? path)
    {
        if (string.IsNullOrEmpty(path)
            || path[0] != '/'
            || path.IndexOf('\0') >= 0)
        {
            return false;
        }

        var hasComponent = false;
        var componentStart = 1;
        for (var index = 1; index <= path.Length; index++)
        {
            if (index < path.Length && path[index] != '/')
                continue;

            var componentLength = index - componentStart;
            if (componentLength > 0)
            {
                hasComponent = true;
                if (componentLength == 1 && path[componentStart] == '.'
                    || componentLength == 2
                    && path[componentStart] == '.'
                    && path[componentStart + 1] == '.')
                {
                    return false;
                }
            }

            componentStart = index + 1;
        }

        return hasComponent;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw ArgumentFailure();
    }

    private static TransferV3Phase4Exception ArgumentFailure() =>
        TransferV3Phase4Exception.Create(
            new ArgumentException(),
            TransferV3Phase4Boundary.Argument);

    private static TransferV3Phase4Exception PosixFailure() =>
        TransferV3Phase4Exception.Create(
            new InvalidDataException(),
            TransferV3Phase4Boundary.Posix);

    private static TransferV3Phase4Exception MapFailure(Exception raw) =>
        TransferV3Phase4Exception.Create(
            raw,
            raw is IOException
                or InvalidDataException
                or OverflowException
                or PlatformNotSupportedException
                ? TransferV3Phase4Boundary.Posix
                : TransferV3Phase4Boundary.Unexpected);
}
