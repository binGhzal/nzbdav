using System.Security.Cryptography;
using Npgsql;
using NzbWebDAV.Database;

namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3PostgreSqlAdmissionValidator
{
    internal static async Task ValidateFreshAndMarkImportingAsync(
        TransferV3PostgreSqlSession session,
        NpgsqlTransaction transaction,
        TransferV3Phase4Digest manifestDigest,
        TransferV3Phase4ManagedBudget managedBudget,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(manifestDigest);
        ArgumentNullException.ThrowIfNull(managedBudget);
        manifestDigest.ValidateOwner(managedBudget);
        var connection = session.BorrowConnection();
        TransferV3Phase4MemoryLease? expectedLease = null;
        TransferV3Phase4MemoryLease? nextLease = null;
        byte[]? expectedCanonicalUtf8 = null;
        byte[]? nextCanonicalUtf8 = null;

        try
        {
            var advisoryAcquired = await TransferV3PostgreSqlAdmissionLockSet.TryAcquireAdvisoryAsync(
                    connection,
                    transaction,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!advisoryAcquired)
                throw new InvalidOperationException();

            await TransferV3PostgreSqlAdmissionLockSet.AcquireRelationsAsync(
                    connection,
                    transaction,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);

            var capturedIdentity = await TransferV3PostgreSqlServerContract.ValidateAndCaptureAsync(
                    connection,
                    transaction,
                    session.TimeZoneId,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!Equals(capturedIdentity, session.Identity))
                throw new InvalidOperationException();

            var environmentSchema = await PostgreSqlEnvironmentContract.ValidateAsync(
                    connection,
                    transaction,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    environmentSchema,
                    session.Identity.SchemaName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException();
            }

            await PostgreSqlPhysicalCatalogContract.ValidateAsync(
                    connection,
                    transaction,
                    PostgreSqlCatalogState.Head,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            await PostgreSqlNativeMigrationContract.ValidateHeadAsync(
                    connection,
                    transaction,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            await PostgreSqlFreshBootstrapContract.ValidateAsync(
                    connection,
                    transaction,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);

            expectedLease = managedBudget.Reserve(
                TransferV3ImportStateCodec.FreshCanonicalUtf8Length,
                TransferV3Phase4MemoryKind.Copy);
            nextLease = managedBudget.Reserve(
                TransferV3ImportStateCodec.ImportingCanonicalUtf8Length,
                TransferV3Phase4MemoryKind.Copy);

            expectedCanonicalUtf8 =
                new byte[TransferV3ImportStateCodec.FreshCanonicalUtf8Length];
            expectedLease.MarkManagedElementStorageAllocated(expectedCanonicalUtf8.Length);
            nextCanonicalUtf8 =
                new byte[TransferV3ImportStateCodec.ImportingCanonicalUtf8Length];
            nextLease.MarkManagedElementStorageAllocated(nextCanonicalUtf8.Length);

            TransferV3ImportStateCodec.WriteFreshCanonical(expectedCanonicalUtf8);
            var manifestSha256Utf8 =
                TransferV3ImportStateCodec.InitializeImportingCanonical(nextCanonicalUtf8);
            manifestDigest.CopyLowerHexTo(manifestSha256Utf8);
            if (!TransferV3ImportStateCodec.IsCanonicalFreshToImportingTransition(
                    expectedCanonicalUtf8,
                    nextCanonicalUtf8))
            {
                throw new InvalidOperationException();
            }

            var changedRows = await TransferV3ImportStateStore.TryTransitionCanonicalFreshToImportingInPostgreSqlTransactionAsync(
                    connection,
                    transaction,
                    expectedCanonicalUtf8,
                    nextCanonicalUtf8,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (changedRows != 1)
                throw new InvalidOperationException();
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.PostgreSqlCommand,
                cancellationToken);
        }
        finally
        {
            if (expectedCanonicalUtf8 is not null)
            {
                CryptographicOperations.ZeroMemory(expectedCanonicalUtf8);
                expectedCanonicalUtf8 = null;
            }

            if (nextCanonicalUtf8 is not null)
            {
                CryptographicOperations.ZeroMemory(nextCanonicalUtf8);
                nextCanonicalUtf8 = null;
            }

            nextLease?.Dispose();
            expectedLease?.Dispose();
        }
    }
}
