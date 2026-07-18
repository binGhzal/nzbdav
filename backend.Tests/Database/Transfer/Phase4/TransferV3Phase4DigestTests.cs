using System.Reflection;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

public sealed class TransferV3Phase4DigestTests
{
    [Fact]
    public void Create_OwnsOneExactChargedCopyAndWritesCanonicalLowerHex()
    {
        var budget = new TransferV3Phase4ManagedBudget();
        var input = Enumerable.Range(0, TransferV3Phase4Digest.SizeBytes)
            .Select(index => (byte)(index * 7))
            .ToArray();
        var expected = (byte[])input.Clone();
        using var digest = TransferV3Phase4Digest.Create(budget, input);
        input.AsSpan().Fill(0xff);
        Span<byte> lowerHex = stackalloc byte[64];

        digest.CopyLowerHexTo(lowerHex);

        Assert.Equal(32, TransferV3Phase4Digest.SizeBytes);
        Assert.True(digest.Bytes.SequenceEqual(expected));
        Assert.Equal(
            Convert.ToHexStringLower(expected),
            System.Text.Encoding.ASCII.GetString(lowerHex));
        Assert.Equal(
            TransferV3Phase4ManagedBudget.RuntimeReserveBytes + 32,
            budget.CurrentBytes);
        Assert.Equal(32, budget.CurrentAllocatedManagedElementStorageBytes);
        Assert.Equal(32, budget.CumulativeAllocatedManagedElementStorageBytes);

        var arrayFields = typeof(TransferV3Phase4Digest)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(byte[]))
            .ToArray();
        Assert.Single(arrayFields);
        Assert.Equal(32, Assert.IsType<byte[]>(arrayFields[0].GetValue(digest)).Length);
        Assert.DoesNotContain(
            typeof(TransferV3Phase4Digest).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(string));
    }

    [Fact]
    public void Dispose_ZeroesSensitiveStorageBeforeReleasingItsLeaseAndIsIdempotent()
    {
        var budget = new TransferV3Phase4ManagedBudget();
        var digest = TransferV3Phase4Digest.Create(budget, Enumerable.Repeat((byte)0xa5, 32).ToArray());
        var bytesField = Assert.Single(
            typeof(TransferV3Phase4Digest)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(byte[]));
        var retained = Assert.IsType<byte[]>(bytesField.GetValue(digest));
        Assert.Contains(retained, value => value != 0);

        digest.Dispose();
        digest.Dispose();

        Assert.All(retained, value => Assert.Equal(0, value));
        Assert.Equal(TransferV3Phase4ManagedBudget.RuntimeReserveBytes, budget.CurrentBytes);
        Assert.Equal(0, budget.CurrentAllocatedManagedElementStorageBytes);
        Assert.Equal(32, budget.CumulativeAllocatedManagedElementStorageBytes);
        AssertCode("phase4-argument", () => _ = digest.Bytes.Length);
        AssertCode("phase4-argument", () =>
            digest.CopyLowerHexTo(new byte[64]));
    }

    [Fact]
    public void CreateAndCopy_RejectInvalidArgumentsWithoutBudgetDrift()
    {
        var budget = new TransferV3Phase4ManagedBudget();

        AssertCode("phase4-argument", () =>
            TransferV3Phase4Digest.Create(null!, new byte[32]));
        AssertCode("phase4-argument", () =>
            TransferV3Phase4Digest.Create(budget, new byte[31]));
        AssertCode("phase4-argument", () =>
            TransferV3Phase4Digest.Create(budget, new byte[33]));
        Assert.Equal(TransferV3Phase4ManagedBudget.RuntimeReserveBytes, budget.CurrentBytes);
        Assert.Equal(0, budget.CurrentAllocatedManagedElementStorageBytes);

        using var digest = TransferV3Phase4Digest.Create(budget, new byte[32]);
        AssertCode("phase4-argument", () =>
            digest.CopyLowerHexTo(new byte[63]));
        AssertCode("phase4-argument", () =>
            digest.CopyLowerHexTo(new byte[65]));
    }

    [Fact]
    public void ValidateOwner_AcceptsOnlyTheLiveCreatingBudgetWithoutBudgetDrift()
    {
        var owner = new TransferV3Phase4ManagedBudget();
        var other = new TransferV3Phase4ManagedBudget();
        var digest = TransferV3Phase4Digest.Create(
            owner,
            new byte[TransferV3Phase4Digest.SizeBytes]);

        digest.ValidateOwner(owner);
        AssertCode("phase4-argument", () => digest.ValidateOwner(null!));
        AssertCode("phase4-argument", () => digest.ValidateOwner(other));
        Assert.Equal(
            TransferV3Phase4ManagedBudget.RuntimeReserveBytes
            + TransferV3Phase4Digest.SizeBytes,
            owner.CurrentBytes);
        Assert.Equal(
            TransferV3Phase4ManagedBudget.RuntimeReserveBytes,
            other.CurrentBytes);

        digest.Dispose();

        AssertCode("phase4-argument", () => digest.ValidateOwner(owner));
        Assert.Equal(
            TransferV3Phase4ManagedBudget.RuntimeReserveBytes,
            owner.CurrentBytes);
    }

    [Fact]
    public void SourceContract_AvoidsAllocatingHexAndCopyHelpersAndHasExceptionalCleanup()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/Phase4/TransferV3Phase4Budgets.cs"));

        Assert.DoesNotContain("Convert.ToHex", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Encoding.", source, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory", source, StringComparison.Ordinal);
        Assert.Contains("catch", source, StringComparison.Ordinal);
        Assert.Contains("lease.Dispose()", source, StringComparison.Ordinal);
    }

    private static void AssertCode(string expected, Action action)
    {
        var failure = Assert.IsType<TransferV3Phase4Exception>(Record.Exception(action));
        Assert.Equal(expected, failure.Code);
        Assert.Equal("Transfer-v3 Phase 4 failed.", failure.Message);
    }

    private static string RepositoryPath(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(relativePath);
    }
}
