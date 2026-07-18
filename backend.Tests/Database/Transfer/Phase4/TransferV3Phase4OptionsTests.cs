using System.Reflection;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

#pragma warning disable CA1416 // Unix modes are used only under the verified POSIX gate.

public sealed class TransferV3Phase4OptionsTests
{
    [Fact]
    public void Constructor_RejectsNullParentAndNonpositiveCeilings()
    {
        AssertCode("phase4-argument", () => new TransferV3Phase4Options(
            null!,
            maxPostgreSqlTextPayloadBytes: 1,
            maxPhase4StagingBytes: 1));

        if (!IsSupportedOrAssertFailClosed()) return;

        using var fixture = new TemporaryDirectory();
        var first = TransferV3Phase4StagingParent.OpenOwned(fixture.Path);
        AssertCode("phase4-argument", () => new TransferV3Phase4Options(
            first,
            maxPostgreSqlTextPayloadBytes: 0,
            maxPhase4StagingBytes: 1));
        using (first.DuplicateHandle()) { }
        first.Dispose();

        var second = TransferV3Phase4StagingParent.OpenOwned(fixture.Path);
        AssertCode("phase4-argument", () => new TransferV3Phase4Options(
            second,
            maxPostgreSqlTextPayloadBytes: 1,
            maxPhase4StagingBytes: -1));
        using (second.DuplicateHandle()) { }
        second.Dispose();
    }

    [Fact]
    public void Consume_ReturnsTheExactPreallocatedOwnerAndTransfersOnlyOnce()
    {
        if (!IsSupportedOrAssertFailClosed()) return;

        using var fixture = new TemporaryDirectory();
        var parent = TransferV3Phase4StagingParent.OpenOwned(fixture.Path);
        using var options = new TransferV3Phase4Options(
            parent,
            maxPostgreSqlTextPayloadBytes: 1234,
            maxPhase4StagingBytes: 5678);
        var ownerField = Assert.Single(
            typeof(TransferV3Phase4Options)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(TransferV3Phase4ConsumedOptions));
        var preallocated = Assert.IsType<TransferV3Phase4ConsumedOptions>(
            ownerField.GetValue(options));

        var consumed = options.Consume();

        Assert.Same(preallocated, consumed);
        Assert.Same(parent, consumed.StagingParent);
        Assert.Equal(1234, consumed.MaxPostgreSqlTextPayloadBytes);
        Assert.Equal(5678, consumed.MaxPhase4StagingBytes);
        AssertCode("phase4-argument", () => options.Consume());
        options.Dispose();
        using (consumed.StagingParent.DuplicateHandle()) { }
        consumed.Dispose();
        consumed.Dispose();
    }

    [Fact]
    public void DisposeBeforeConsume_ClosesTheExactParentAndConsumeFails()
    {
        if (!IsSupportedOrAssertFailClosed()) return;

        using var fixture = new TemporaryDirectory();
        var parent = TransferV3Phase4StagingParent.OpenOwned(fixture.Path);
        var identity = parent.Identity;
        var options = new TransferV3Phase4Options(parent, 1, 1);

        options.Dispose();
        options.Dispose();

        Assert.Equal(identity, parent.Identity);
        AssertCode("phase4-argument", () => parent.DuplicateHandle());
        AssertCode("phase4-argument", () => options.Consume());
    }

    [Fact]
    public void ConsumedDispose_IsIdempotentAndEveryPropertyFailsAfterward()
    {
        if (!IsSupportedOrAssertFailClosed()) return;

        using var fixture = new TemporaryDirectory();
        var options = new TransferV3Phase4Options(
            TransferV3Phase4StagingParent.OpenOwned(fixture.Path),
            100,
            200);
        var consumed = options.Consume();
        options.Dispose();

        consumed.Dispose();
        consumed.Dispose();

        AssertCode("phase4-argument", () => _ = consumed.StagingParent);
        AssertCode("phase4-argument", () => _ = consumed.MaxPostgreSqlTextPayloadBytes);
        AssertCode("phase4-argument", () => _ = consumed.MaxPhase4StagingBytes);
    }

    [Fact]
    public async Task ConsumeAndDisposeRace_HasAtMostOneTransferWinnerAndNoSplitOwnership()
    {
        if (!IsSupportedOrAssertFailClosed()) return;

        using var fixture = new TemporaryDirectory();
        var parent = TransferV3Phase4StagingParent.OpenOwned(fixture.Path);
        var options = new TransferV3Phase4Options(parent, 100, 200);
        using var start = new ManualResetEventSlim();
        var consumeTasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            start.Wait();
            TransferV3Phase4ConsumedOptions? value = null;
            var failure = Record.Exception(() =>
            {
                value = options.Consume();
            });
            return (value, failure);
        })).ToArray();
        var disposeTasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            start.Wait();
            options.Dispose();
        })).ToArray();

        start.Set();
        await Task.WhenAll(disposeTasks);
        var outcomes = await Task.WhenAll(consumeTasks);

        var winners = outcomes.Where(outcome => outcome.value is not null).ToArray();
        Assert.InRange(winners.Length, 0, 1);
        Assert.All(
            outcomes.Where(outcome => outcome.failure is not null),
            outcome => Assert.Equal(
                "phase4-argument",
                Assert.IsType<TransferV3Phase4Exception>(outcome.failure).Code));
        if (winners.Length == 1)
        {
            var consumed = Assert.IsType<TransferV3Phase4ConsumedOptions>(winners[0].value);
            Assert.Same(parent, consumed.StagingParent);
            using (consumed.StagingParent.DuplicateHandle()) { }
            consumed.Dispose();
        }
        else
        {
            AssertCode("phase4-argument", () => parent.DuplicateHandle());
        }
    }

    [Fact]
    public void SourceContract_PreallocatesBeforeTransferAndUsesOneAtomicExchange()
    {
        var source = File.ReadAllText(RepositoryPath(
            "backend/Database/Transfer/Phase4/TransferV3Phase4Options.cs"));
        var constructorStart = source.IndexOf(
            "internal TransferV3Phase4Options(",
            StringComparison.Ordinal);
        var consumeStart = source.IndexOf(
            "internal TransferV3Phase4ConsumedOptions Consume()",
            constructorStart,
            StringComparison.Ordinal);
        Assert.True(constructorStart >= 0 && consumeStart > constructorStart);
        var constructor = source[constructorStart..consumeStart];
        var validation = constructor.IndexOf("if (stagingParent is null", StringComparison.Ordinal);
        var allocation = constructor.IndexOf(
            "new TransferV3Phase4ConsumedOptions(",
            StringComparison.Ordinal);
        Assert.True(validation >= 0 && allocation > validation);
        var allocationEnd = constructor.IndexOf(';', allocation);
        Assert.True(allocationEnd > allocation);
        var afterAllocation = constructor[(allocationEnd + 1)..];
        Assert.DoesNotContain("new ", afterAllocation, StringComparison.Ordinal);
        Assert.DoesNotContain("throw", afterAllocation, StringComparison.Ordinal);
        Assert.DoesNotContain("=", afterAllocation, StringComparison.Ordinal);
        Assert.DoesNotContain("(", afterAllocation, StringComparison.Ordinal);

        var consumeEnd = source.IndexOf("public void Dispose()", consumeStart, StringComparison.Ordinal);
        var consume = source[consumeStart..consumeEnd];
        Assert.Contains("Interlocked.Exchange", consume, StringComparison.Ordinal);
        Assert.DoesNotContain("new ", consume, StringComparison.Ordinal);
    }

    private static void AssertCode(string expected, Action action)
    {
        var failure = Assert.IsType<TransferV3Phase4Exception>(Record.Exception(action));
        Assert.Equal(expected, failure.Code);
        Assert.Equal("Transfer-v3 Phase 4 failed.", failure.Message);
    }

    private static bool IsSupportedOrAssertFailClosed()
    {
        if (TransferV3Posix.IsSupported) return true;
        AssertCode("phase4-posix", () =>
            TransferV3Phase4StagingParent.OpenOwned(
                "/nzbdav-transfer-v3-phase4-unsupported-options-probe"));
        return false;
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

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                Environment.CurrentDirectory,
                "nzbdav-transfer-v3-phase4-options-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            File.SetUnixFileMode(
                Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            File.SetUnixFileMode(
                Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(Path, recursive: true);
        }
    }
}

#pragma warning restore CA1416
