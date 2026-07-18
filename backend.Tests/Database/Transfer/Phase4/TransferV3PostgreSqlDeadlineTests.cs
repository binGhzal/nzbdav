using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NzbWebDAV.Database.Transfer;

namespace backend.Tests.Database.Transfer.Phase4;

public sealed class TransferV3PostgreSqlDeadlineTests
{
    [Fact]
    public void Start_RejectsNullProviderWithTheFixedArgumentCode()
    {
        AssertCode(
            "phase4-argument",
            () => TransferV3PostgreSqlDeadline.Start(null!, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [MemberData(nameof(InvalidDurations))]
    public void Start_RejectsInvalidDurationBeforeReadingTheProvider(TimeSpan duration)
    {
        var provider = new ManualTimeProvider();

        AssertCode(
            "phase4-argument",
            () => TransferV3PostgreSqlDeadline.Start(provider, duration));

        Assert.Equal(0, provider.TimestampReads);
        Assert.Equal(0, provider.FrequencyReads);
        Assert.Equal(0, provider.TimerCreations);
    }

    [Fact]
    public void Start_AcceptsTheExactMaximumDuration()
    {
        var provider = new ManualTimeProvider();

        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TransferV3PostgreSqlDeadline.MaximumDuration);

        Assert.Equal(
            TimeSpan.FromMilliseconds(0xfffffffeL),
            TransferV3PostgreSqlDeadline.MaximumDuration);
        Assert.Equal(1, provider.TimestampReads);
        Assert.Equal(TimeSpan.FromMilliseconds(0xfffffffeL), deadline.Remaining);
        Assert.False(deadline.IsExpired);
        Assert.Equal(3, provider.TimestampReads);
        Assert.True(provider.FrequencyReads >= 1);
        Assert.Equal(0, provider.TimerCreations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void CreateCommandFence_RejectsInvalidMaximumBeforeReadingTheProvider(
        int ordinaryMaximumSeconds)
    {
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(10));
        var timestampReads = provider.TimestampReads;
        var frequencyReads = provider.FrequencyReads;

        AssertCode(
            "phase4-argument",
            () => deadline.CreateCommandFence(ordinaryMaximumSeconds));

        Assert.Equal(timestampReads, provider.TimestampReads);
        Assert.Equal(frequencyReads, provider.FrequencyReads);
        Assert.Equal(0, provider.TimerCreations);
    }

    [Fact]
    public void Remaining_IsExactAndNeverIncreasesAcrossRegressionAndExpiry()
    {
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(10), deadline.Remaining);

        provider.SetElapsed(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(7), deadline.Remaining);

        provider.SetElapsed(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(7), deadline.Remaining);

        provider.SetElapsed(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
        Assert.True(deadline.IsExpired);

        provider.SetElapsed(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
        Assert.True(deadline.IsExpired);
        Assert.Equal(0, provider.TimerCreations);
    }

    [Fact]
    public async Task Remaining_AtomicallyRetainsTheLowerConcurrentStaleSample()
    {
        var provider = new StaleSampleRaceTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(10));
        var olderObservation = Task.Run(() => deadline.Remaining);

        Assert.True(
            provider.OlderSampleSelected.Wait(TimeSpan.FromSeconds(5)),
            "The older observation did not reach its deterministic barrier.");

        TimeSpan newerRemaining;
        try
        {
            newerRemaining = await Task.Run(() => deadline.Remaining);
        }
        finally
        {
            provider.ReleaseOlderSample.Set();
        }

        var olderRemaining = await olderObservation;

        Assert.Equal(TimeSpan.FromSeconds(2), newerRemaining);
        Assert.Equal(TimeSpan.FromSeconds(2), olderRemaining);
        Assert.Equal(TimeSpan.FromSeconds(2), deadline.Remaining);
    }

    [Fact]
    public void CreateCommandFence_RoundsSubsecondRemainingUpToOneSecond()
    {
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(10));
        provider.SetElapsed(TimeSpan.FromSeconds(9.5));

        using var fence = deadline.CreateCommandFence(300);

        Assert.Equal(1, fence.CommandTimeoutSeconds);
        Assert.Equal(TimeSpan.FromMilliseconds(500), provider.SingleTimer.DueTime);
        Assert.Equal(Timeout.InfiniteTimeSpan, provider.SingleTimer.Period);
    }

    [Fact]
    public void CreateCommandFence_RoundsOneTickOverASecondUpToTwoSeconds()
    {
        var provider = new ManualTimeProvider();
        var duration = TimeSpan.FromSeconds(10);
        var remaining = TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1);
        var deadline = TransferV3PostgreSqlDeadline.Start(provider, duration);
        provider.SetElapsed(duration - remaining);

        using var fence = deadline.CreateCommandFence(300);

        Assert.Equal(2, fence.CommandTimeoutSeconds);
        Assert.Equal(remaining, provider.SingleTimer.DueTime);
    }

    [Fact]
    public void CreateCommandFence_ClampsCommandTimeoutWithoutShorteningCancellation()
    {
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(10));

        using var fence = deadline.CreateCommandFence(3);

        Assert.Equal(3, fence.CommandTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(10), provider.SingleTimer.DueTime);
    }

    [Fact]
    public void CreateCommandFence_UsesOneSampleAndTheExactRemainingTimerDueTime()
    {
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(10));
        provider.SetElapsed(TimeSpan.FromSeconds(3));
        var timestampReads = provider.TimestampReads;

        using var fence = deadline.CreateCommandFence(300);

        Assert.Equal(timestampReads + 1, provider.TimestampReads);
        Assert.Equal(7, fence.CommandTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(7), provider.SingleTimer.DueTime);
        Assert.True(fence.CancellationToken.CanBeCanceled);
        Assert.False(fence.CancellationToken.IsCancellationRequested);
        Assert.False(fence.IsExpired);

        Assert.True(provider.SingleTimer.Fire());
        Assert.True(fence.CancellationToken.IsCancellationRequested);
        Assert.True(fence.IsExpired);
    }

    [Fact]
    public void CreateCommandFence_ProducesProgressivelySmallerIndependentFences()
    {
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));

        var first = deadline.CreateCommandFence(300);
        Assert.Equal(5, first.CommandTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(5), provider.Timers[0].DueTime);
        first.Dispose();

        provider.SetElapsed(TimeSpan.FromSeconds(2.4));
        var second = deadline.CreateCommandFence(300);
        Assert.Equal(3, second.CommandTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(2.6), provider.Timers[1].DueTime);
        second.Dispose();

        provider.SetElapsed(TimeSpan.FromSeconds(4.75));
        var third = deadline.CreateCommandFence(300);
        Assert.Equal(1, third.CommandTimeoutSeconds);
        Assert.Equal(TimeSpan.FromMilliseconds(250), provider.Timers[2].DueTime);
        third.Dispose();

        Assert.Equal(3, provider.TimerCreations);
        Assert.All(provider.Timers, timer => Assert.True(timer.IsDisposed));
    }

    [Fact]
    public void CommandFence_DisposalIsIdempotentAndDisposesItsOwnedTimerOnce()
    {
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));
        var fence = deadline.CreateCommandFence(300);
        var timer = provider.SingleTimer;

        fence.Dispose();
        fence.Dispose();

        Assert.True(timer.IsDisposed);
        Assert.Equal(1, timer.DisposeCalls);
    }

    [Fact]
    public void CreateCommandFence_AtExactExpiryUsesNoTimerAndIsSynchronouslyCanceled()
    {
        var provider = new ManualTimeProvider();
        var duration = TimeSpan.FromSeconds(5);
        var deadline = TransferV3PostgreSqlDeadline.Start(provider, duration);
        provider.SetElapsed(duration);

        using var fence = deadline.CreateCommandFence(300);

        Assert.Equal(1, fence.CommandTimeoutSeconds);
        Assert.True(fence.CancellationToken.CanBeCanceled);
        Assert.True(fence.CancellationToken.IsCancellationRequested);
        Assert.True(fence.IsExpired);
        Assert.Equal(0, provider.TimerCreations);
        Assert.True(deadline.IsExpired);
    }

    [Fact]
    public void CreateOperationFence_UsesOneAuthoritativeSampleAndExactRemainingTimer()
    {
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(10));
        provider.SetElapsed(TimeSpan.FromSeconds(3));
        var timestampReads = provider.TimestampReads;

        using var fence = deadline.CreateOperationFence();

        Assert.Equal(timestampReads + 1, provider.TimestampReads);
        Assert.Equal(TimeSpan.FromSeconds(7), provider.SingleTimer.DueTime);
        Assert.Equal(Timeout.InfiniteTimeSpan, provider.SingleTimer.Period);
        Assert.True(fence.CancellationToken.CanBeCanceled);
        Assert.False(fence.CancellationToken.IsCancellationRequested);
        Assert.False(fence.IsExpired);

        Assert.True(provider.SingleTimer.Fire());
        Assert.True(fence.CancellationToken.IsCancellationRequested);
        Assert.True(fence.IsExpired);
    }

    [Fact]
    public void CreateOperationFence_AtExactExpiryUsesNoTimerAndIsSynchronouslyCanceled()
    {
        var provider = new ManualTimeProvider();
        var duration = TimeSpan.FromSeconds(5);
        var deadline = TransferV3PostgreSqlDeadline.Start(provider, duration);
        provider.SetElapsed(duration);

        using var fence = deadline.CreateOperationFence();

        Assert.True(fence.CancellationToken.CanBeCanceled);
        Assert.True(fence.CancellationToken.IsCancellationRequested);
        Assert.True(fence.IsExpired);
        Assert.Equal(0, provider.TimerCreations);
    }

    [Fact]
    public void OperationFence_DisposalIsIdempotentAndDisposesItsOwnedTimerOnce()
    {
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));
        var fence = deadline.CreateOperationFence();
        var timer = provider.SingleTimer;

        fence.Dispose();
        fence.Dispose();

        Assert.True(timer.IsDisposed);
        Assert.Equal(1, timer.DisposeCalls);
    }

    [Fact]
    public void OperationFence_IsOpaqueAndCanOnlyBeCreatedByAParameterlessDeadlineMethod()
    {
        var method = Assert.Single(
            typeof(TransferV3PostgreSqlDeadline).GetMethods(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic),
            candidate => candidate.Name == "CreateOperationFence");
        var fenceType = typeof(TransferV3PostgreSqlDeadline).GetNestedType(
            "TransferV3PostgreSqlOperationFence",
            System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(fenceType);
        Assert.Equal(typeof(TransferV3PostgreSqlDeadline), fenceType.DeclaringType);
        Assert.True(fenceType.IsNestedPrivate);
        Assert.Empty(method.GetParameters());
        Assert.Equal(typeof(ITransferV3PostgreSqlOperationFence), method.ReturnType);
        Assert.Contains(
            typeof(ITransferV3PostgreSqlOperationFence),
            fenceType.GetInterfaces());
        Assert.DoesNotContain(
            typeof(ITransferV3PostgreSqlOperationFence).GetMethods(),
            candidate => candidate.IsStatic);
        var constructor = Assert.Single(
            fenceType.GetConstructors(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic));
        Assert.True(constructor.IsAssembly);
        Assert.DoesNotContain(
            fenceType.GetMethods(
                System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly),
            candidate => candidate.ReturnType == fenceType);
        Assert.DoesNotContain(
            fenceType.GetMethods(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly),
            candidate => candidate.Name.Contains("Reset", StringComparison.OrdinalIgnoreCase)
                         || candidate.Name.Contains("Rearm", StringComparison.OrdinalIgnoreCase)
                         || candidate.Name.Contains("Change", StringComparison.OrdinalIgnoreCase)
                         || candidate.Name.Contains("Restart", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(ITransferV3PostgreSqlOperationFence).GetMethods(),
            candidate => candidate.Name.Contains("Reset", StringComparison.OrdinalIgnoreCase)
                         || candidate.Name.Contains("Rearm", StringComparison.OrdinalIgnoreCase)
                         || candidate.Name.Contains("Change", StringComparison.OrdinalIgnoreCase)
                         || candidate.Name.Contains("Restart", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OperationFence_SourceAuditAllowsOnlyDeadlineOwnedConstructionSites()
    {
        const string fenceTypeName = "TransferV3PostgreSqlOperationFence";
        const string deadlineSourcePath =
            "backend/Database/Transfer/Phase4/TransferV3PostgreSqlDeadline.cs";
        var absoluteDeadlinePath = SqliteContractTestSupport.AbsolutePath(deadlineSourcePath);
        var deadlineRoot = CSharpSyntaxTree.ParseText(
                File.ReadAllText(absoluteDeadlinePath),
                path: deadlineSourcePath)
            .GetRoot();
        var deadlineClass = Assert.Single(
            deadlineRoot.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            declaration => declaration.Identifier.ValueText
                == nameof(TransferV3PostgreSqlDeadline));
        var fenceClass = Assert.Single(
            deadlineClass.Members.OfType<ClassDeclarationSyntax>(),
            declaration => declaration.Identifier.ValueText == fenceTypeName);
        Assert.Contains(
            fenceClass.Modifiers,
            modifier => modifier.IsKind(
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword));
        Assert.Empty(
            deadlineClass.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>());

        var sourcePaths = EnumerateSourceFiles("backend")
            .Concat(EnumerateSourceFiles("backend.Tests"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var constructionSites = sourcePaths
            .SelectMany(path => CSharpSyntaxTree.ParseText(
                    File.ReadAllText(path),
                    path: path)
                .GetRoot()
                .DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(creation => creation.Type
                    .DescendantTokens()
                    .LastOrDefault(token => token.IsKind(
                        Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken))
                    .ValueText == fenceTypeName)
                .Select(creation => (Path: path, Creation: creation)))
            .ToArray();

        Assert.Equal(2, constructionSites.Length);
        Assert.All(
            constructionSites,
            site =>
            {
                Assert.Equal(absoluteDeadlinePath, site.Path);
                var method = Assert.IsType<MethodDeclarationSyntax>(
                    site.Creation.Ancestors().First(
                        ancestor => ancestor is MethodDeclarationSyntax));
                Assert.Equal("CreateOperationFence", method.Identifier.ValueText);
                Assert.Empty(method.ParameterList.Parameters);
            });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Start_InvalidTimestampFrequencyMapsToFixedUnexpected(long frequency)
    {
        var provider = new ManualTimeProvider
        {
            TimestampFrequencyValue = frequency,
        };

        AssertCode(
            "phase4-unexpected",
            () => TransferV3PostgreSqlDeadline.Start(
                provider,
                TimeSpan.FromSeconds(1)));

        Assert.Equal(0, provider.TimestampReads);
        Assert.Equal(0, provider.TimerCreations);
    }

    [Fact]
    public void Start_FrequencyFailureMapsToFixedUnexpectedWithoutRawDetails()
    {
        var raw = new InvalidOperationException("frequency-failure-CANARY");
        var provider = new ManualTimeProvider
        {
            FrequencyFailure = read => read == 1 ? raw : null,
        };

        AssertSanitizedUnexpected(
            raw,
            () => TransferV3PostgreSqlDeadline.Start(
                provider,
                TimeSpan.FromSeconds(1)));

        Assert.Equal(0, provider.TimestampReads);
        Assert.Equal(0, provider.TimerCreations);
    }

    [Fact]
    public void Start_TimestampFailureMapsToFixedUnexpectedWithoutRawDetails()
    {
        var raw = new InvalidOperationException("timestamp-start-failure-CANARY");
        var provider = new ManualTimeProvider
        {
            TimestampFailure = read => read == 1 ? raw : null,
        };

        AssertSanitizedUnexpected(
            raw,
            () => TransferV3PostgreSqlDeadline.Start(
                provider,
                TimeSpan.FromSeconds(1)));

        Assert.Equal(0, provider.TimerCreations);
    }

    [Fact]
    public void Remaining_TimestampFailureMapsToFixedUnexpectedWithoutRawDetails()
    {
        var raw = new InvalidOperationException("timestamp-elapsed-failure-CANARY");
        var provider = new ManualTimeProvider
        {
            TimestampFailure = read => read == 2 ? raw : null,
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(1));

        AssertSanitizedUnexpected(raw, () => _ = deadline.Remaining);
    }

    [Fact]
    public void Remaining_ElapsedFrequencyFailureMapsToFixedUnexpectedWithoutRawDetails()
    {
        var raw = new InvalidOperationException("elapsed-frequency-failure-CANARY");
        var provider = new ManualTimeProvider
        {
            FrequencyFailure = read => read == 2 ? raw : null,
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(1));

        AssertSanitizedUnexpected(raw, () => _ = deadline.Remaining);
    }

    [Fact]
    public void CreateCommandFence_TimerCreationFailureMapsToFixedUnexpected()
    {
        var raw = new InvalidOperationException("timer-create-failure-CANARY");
        var provider = new ManualTimeProvider
        {
            TimerCreationFailure = raw,
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(1));

        AssertSanitizedUnexpected(raw, () => deadline.CreateCommandFence(300));

        Assert.Equal(1, provider.TimerCreations);
    }

    [Fact]
    public void CreateOperationFence_TimerCreationFailureMapsToFixedUnexpected()
    {
        var raw = new InvalidOperationException("operation-timer-create-failure-CANARY");
        var provider = new ManualTimeProvider
        {
            TimerCreationFailure = raw,
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(1));

        AssertSanitizedUnexpected(raw, () => deadline.CreateOperationFence());

        Assert.Equal(1, provider.TimerCreations);
    }

    [Fact]
    public void CreateOperationFence_TimestampSamplingFailureMapsToFixedUnexpected()
    {
        var raw = new InvalidOperationException(
            "operation-timestamp-failure-CANARY");
        var provider = new ManualTimeProvider
        {
            TimestampFailure = read => read == 2 ? raw : null,
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(1));

        AssertSanitizedUnexpected(raw, () => deadline.CreateOperationFence());

        Assert.Equal(0, provider.TimerCreations);
    }

    [Fact]
    public void CommandFence_TimerDisposalFailureMapsToFixedUnexpected()
    {
        var raw = new InvalidOperationException("timer-dispose-failure-CANARY");
        var timer = new ThrowingDisposeTimer(raw);
        var provider = new ManualTimeProvider
        {
            TimerFactory = (_, _, _, _) => timer,
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(1));
        var fence = deadline.CreateCommandFence(300);

        AssertSanitizedUnexpected(raw, fence.Dispose);

        Assert.Equal(1, timer.DisposeCalls);
    }

    [Fact]
    public void OperationFence_TimerDisposalFailureIsSanitizedAndSecondDisposeIsANoOp()
    {
        var raw = new InvalidOperationException(
            "operation-timer-dispose-failure-CANARY");
        var timer = new ThrowingDisposeTimer(raw);
        var provider = new ManualTimeProvider
        {
            TimerFactory = (_, _, _, _) => timer,
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(1));
        var fence = deadline.CreateOperationFence();

        AssertSanitizedUnexpected(raw, fence.Dispose);
        fence.Dispose();

        Assert.Equal(1, timer.DisposeCalls);
    }

    [Fact]
    public void ProviderBoundary_PreservesAnAlreadySanitizedFailureByReference()
    {
        var preserved = TransferV3Phase4Exception.Create(
            new InvalidOperationException("already-sanitized-CANARY"),
            TransferV3Phase4Boundary.Argument);
        var provider = new ManualTimeProvider
        {
            TimerCreationFailure = preserved,
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(1));

        var returned = Assert.IsType<TransferV3Phase4Exception>(
            Record.Exception(() =>
            {
                _ = deadline.CreateCommandFence(300);
            }));

        Assert.Same(preserved, returned);
        Assert.Equal("phase4-argument", returned.Code);
    }

    public static TheoryData<TimeSpan> InvalidDurations => new()
    {
        TimeSpan.Zero,
        TimeSpan.FromTicks(-1),
        Timeout.InfiniteTimeSpan,
        TimeSpan.MaxValue,
        TimeSpan.FromTicks(
            TransferV3PostgreSqlDeadline.MaximumDuration.Ticks + 1),
    };

    private static IEnumerable<string> EnumerateSourceFiles(string repositoryRelativeRoot)
    {
        var root = SqliteContractTestSupport.AbsolutePath(repositoryRelativeRoot);
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"));
    }

    private static TransferV3Phase4Exception AssertCode(string expected, Action action)
    {
        var failure = Assert.IsType<TransferV3Phase4Exception>(Record.Exception(action));
        Assert.Equal(expected, failure.Code);
        Assert.Equal("Transfer-v3 Phase 4 failed.", failure.Message);
        Assert.Null(failure.InnerException);
        return failure;
    }

    private static void AssertSanitizedUnexpected(Exception raw, Action action)
    {
        var failure = AssertCode("phase4-unexpected", action);

        Assert.DoesNotContain(raw.Message, failure.ToString(), StringComparison.Ordinal);
    }

    private class ManualTimeProvider : TimeProvider
    {
        private readonly object _timerLock = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        private int _timestampReads;
        private int _frequencyReads;
        private int _timerCreations;

        internal long TimestampFrequencyValue { get; init; } = TimeSpan.TicksPerSecond;

        internal Func<int, Exception?>? TimestampFailure { get; init; }

        internal Func<int, Exception?>? FrequencyFailure { get; init; }

        internal Exception? TimerCreationFailure { get; init; }

        internal Func<TimerCallback, object?, TimeSpan, TimeSpan, ITimer>? TimerFactory
        {
            get;
            init;
        }

        internal int TimestampReads => Volatile.Read(ref _timestampReads);

        internal int FrequencyReads => Volatile.Read(ref _frequencyReads);

        internal int TimerCreations => Volatile.Read(ref _timerCreations);

        internal ManualTimer[] Timers
        {
            get
            {
                lock (_timerLock)
                    return [.. _timers];
            }
        }

        internal ManualTimer SingleTimer => Assert.Single(Timers);

        public override long TimestampFrequency
        {
            get
            {
                var read = Interlocked.Increment(ref _frequencyReads);
                var failure = FrequencyFailure?.Invoke(read);
                if (failure is not null)
                    throw failure;

                return TimestampFrequencyValue;
            }
        }

        public override long GetTimestamp()
        {
            var read = Interlocked.Increment(ref _timestampReads);
            var failure = TimestampFailure?.Invoke(read);
            if (failure is not null)
                throw failure;

            return Interlocked.Read(ref _timestamp);
        }

        internal void SetElapsed(TimeSpan elapsed) =>
            Interlocked.Exchange(ref _timestamp, elapsed.Ticks);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            Interlocked.Increment(ref _timerCreations);
            if (TimerCreationFailure is not null)
                throw TimerCreationFailure;

            if (TimerFactory is not null)
                return TimerFactory(callback, state, dueTime, period);

            var timer = new ManualTimer(callback, state, dueTime, period);
            lock (_timerLock)
                _timers.Add(timer);
            return timer;
        }
    }

    private sealed class StaleSampleRaceTimeProvider : TimeProvider
    {
        private int _timestampReads;

        internal ManualResetEventSlim OlderSampleSelected { get; } = new(false);

        internal ManualResetEventSlim ReleaseOlderSample { get; } = new(false);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return Interlocked.Increment(ref _timestampReads) switch
            {
                1 => 0,
                2 => ReturnOlderSampleAfterBarrier(),
                _ => TimeSpan.FromSeconds(8).Ticks,
            };
        }

        private long ReturnOlderSampleAfterBarrier()
        {
            OlderSampleSelected.Set();
            ReleaseOlderSample.Wait();
            return TimeSpan.FromSeconds(3).Ticks;
        }
    }

    private sealed class ManualTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) : ITimer
    {
        private int _disposed;
        private int _disposeCalls;

        internal TimeSpan DueTime { get; private set; } = dueTime;

        internal TimeSpan Period { get; private set; } = period;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        internal bool Fire()
        {
            if (IsDisposed)
                return false;

            callback(state);
            return true;
        }

        public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
        {
            if (IsDisposed)
                return false;

            DueTime = newDueTime;
            Period = newPeriod;
            return true;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            Interlocked.Exchange(ref _disposed, 1);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingDisposeTimer(Exception failure) : ITimer
    {
        private int _disposeCalls;

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            throw failure;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            throw failure;
        }
    }
}
