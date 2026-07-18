using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Transfer;
using System.Threading.Tasks.Sources;

namespace backend.Tests.Database.Transfer.Phase4;

[Collection(nameof(SqliteMigrationContractEnvironmentCollection))]
public sealed class TransferV3PostgreSqlSessionTests
{
    private const string Schema = "transfer_v3";
    private const string SessionSourcePath =
        "backend/Database/Transfer/Phase4/TransferV3PostgreSqlSession.cs";
    private const string OpenAttemptSourcePath =
        "backend/Database/Transfer/Phase4/TransferV3PostgreSqlOpenAttempt.cs";
    private const string TargetDescriptorSourcePath =
        "backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetDescriptor.cs";
    private const string ProviderOperationsSourcePath =
        "backend/Database/Transfer/Phase4/TransferV3PostgreSqlProviderOperations.cs";

    public enum ProviderFailureMode
    {
        SynchronousThrow,
        NullTask,
        NullResult,
        CallerCancellation,
    }

    public enum ValidationStage
    {
        Server,
        Environment,
    }

    public enum ValidationMismatchKind
    {
        EnvironmentSchema,
        CapturedSchema,
        ExpectedIdentity,
    }

    private static readonly string[] NpgsqlAmbientVariables =
    [
        "PGUSER",
        "PGPASSWORD",
        "PGPASSFILE",
        "PGSSLCERT",
        "PGSSLKEY",
        "PGSSLROOTCERT",
        "PGCLIENTENCODING",
        "PGTZ",
        "PGOPTIONS",
        "PGTARGETSESSIONATTRS",
        "PGSSLNEGOTIATION",
        "PGGSSENCMODE",
        "PGREQUIREAUTH",
        "PGAPPNAME",
    ];

    [Fact]
    public async Task CreateOpenAttempt_RegistersAnOwnerAndLeaseBeforeAnyOpenStarts()
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);

        var attempt = descriptor.CreateOpenAttempt();

        Assert.NotNull(attempt);
        Assert.Equal(1, operations.CreateConnectionCalls);
        Assert.Equal(0, operations.OpenCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        Assert.Same(operations.CreatedConnection, Assert.Single(operations.Connections));
        Assert.Equal(System.Data.ConnectionState.Closed, operations.CreatedConnection!.State);

        var result = await attempt.CloseWithinAsync(LiveDeadline());
        AssertCleanup(result);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
        await descriptor.DisposeAsync();
        Assert.Equal(1, operations.DisposeDataSourceCalls);
    }

    [Fact]
    public async Task ValidateFirst_TransfersTheSameConnectionAndLeaseExactlyOnce()
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();

        await attempt.OpenAsync(CancellationToken.None);
        var session = await attempt.ValidateFirstAsync(
            TimeZoneInfo.Local.Id,
            CancellationToken.None);

        Assert.Same(operations.CreatedConnection, session.BorrowConnection());
        Assert.Equal(Identity, session.Identity);
        Assert.Equal(TransferV3PostgreSqlTargetDescriptor.CommandTimeoutSeconds,
            session.OrdinaryCommandTimeoutSeconds);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        Assert.Equal(
            ["create", "open", "server", "environment"],
            operations.CallOrder);
        Assert.Equal([300], operations.ServerTimeouts);
        Assert.Equal([300], operations.EnvironmentTimeouts);

        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.ValidateFirstAsync(
                TimeZoneInfo.Local.Id,
                CancellationToken.None).AsTask());
        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.CloseWithinAsync(LiveDeadline()).AsTask());
        AssertCode("phase4-unexpected", attempt.AbandonForHelperExit);

        AssertCleanup(await session.CloseWithinAsync(LiveDeadline()));
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
    }

    [Fact]
    public async Task OpenFaultAndValidationFaultEachLeaveTheAttemptOwnedForCallerClose()
    {
        using var environment = StrictEnvironment();

        var openFailure = new InvalidOperationException("open-failure-CANARY");
        var openOperations = new FakeProviderOperations
        {
            OpenHandler = (_, _) => Task.FromException(openFailure),
        };
        var openDescriptor = CreateDescriptor(openOperations);
        var openAttempt = openDescriptor.CreateOpenAttempt();

        await AssertCodeAsync(
            "phase4-postgresql-open",
            () => openAttempt.OpenAsync(CancellationToken.None).AsTask());
        Assert.Equal(1, openDescriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await openAttempt.CloseWithinAsync(LiveDeadline()));
        Assert.Equal(0, openDescriptor.ActiveLifecycleLeaseCount);

        var validationFailure = new InvalidOperationException("validation-failure-CANARY");
        var validationOperations = new FakeProviderOperations
        {
            ValidateServerHandler = (_, _, _, _) =>
                Task.FromException<TransferV3PostgreSqlTargetIdentity>(validationFailure),
        };
        var validationDescriptor = CreateDescriptor(validationOperations);
        var validationAttempt = validationDescriptor.CreateOpenAttempt();
        await validationAttempt.OpenAsync(CancellationToken.None);

        await AssertCodeAsync(
            "phase4-postgresql-command",
            () => validationAttempt.ValidateFirstAsync(
                TimeZoneInfo.Local.Id,
                CancellationToken.None).AsTask());
        Assert.Equal(1, validationDescriptor.ActiveLifecycleLeaseCount);
        Assert.Equal(0, validationOperations.EnvironmentCalls);
        await AssertCodeAsync(
            "phase4-unexpected",
            () => validationAttempt.ValidateFirstAsync(
                TimeZoneInfo.Local.Id,
                CancellationToken.None).AsTask());
        AssertCleanup(await validationAttempt.CloseWithinAsync(LiveDeadline()));
        Assert.Equal(0, validationDescriptor.ActiveLifecycleLeaseCount);
    }

    [Theory]
    [InlineData(ProviderFailureMode.SynchronousThrow)]
    [InlineData(ProviderFailureMode.NullTask)]
    [InlineData(ProviderFailureMode.CallerCancellation)]
    public async Task OpenExceptionalBoundariesPreserveTheOwnedAttempt(
        ProviderFailureMode failureMode)
    {
        using var environment = StrictEnvironment();
        using var caller = new CancellationTokenSource();
        if (failureMode == ProviderFailureMode.CallerCancellation)
            caller.Cancel();

        var operations = new FakeProviderOperations
        {
            OpenHandler = failureMode switch
            {
                ProviderFailureMode.SynchronousThrow =>
                    static (_, _) => throw new InvalidOperationException(
                        "open-sync-CANARY"),
                ProviderFailureMode.NullTask => static (_, _) => null!,
                ProviderFailureMode.CallerCancellation =>
                    (_, _) => Task.FromCanceled(caller.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(failureMode)),
            },
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();

        var observed = await Record.ExceptionAsync(
            () => attempt.OpenAsync(caller.Token).AsTask());

        if (failureMode == ProviderFailureMode.CallerCancellation)
        {
            var cancellation = Assert.IsType<OperationCanceledException>(observed);
            Assert.Equal(caller.Token, cancellation.CancellationToken);
            Assert.Equal("Transfer-v3 Phase 4 was canceled.", cancellation.Message);
        }
        else
        {
            Assert.Equal(
                "phase4-postgresql-open",
                Assert.IsType<TransferV3Phase4Exception>(observed).Code);
        }

        Assert.Equal([caller.Token], operations.OpenTokens);
        Assert.Equal(1, operations.OpenCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
    }

    [Fact]
    public async Task ConcurrentOpenAndValidationTransitionsStartNoSecondProviderCall()
    {
        using var environment = StrictEnvironment();
        var openCompletion = NewCompletion();
        var validationCompletion =
            new TaskCompletionSource<TransferV3PostgreSqlTargetIdentity>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new FakeProviderOperations
        {
            OpenHandler = (_, _) => openCompletion.Task,
            ValidateServerHandler = (_, _, _, _) => validationCompletion.Task,
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();

        var firstOpen = attempt.OpenAsync(CancellationToken.None).AsTask();
        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.OpenAsync(CancellationToken.None).AsTask());
        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.CloseWithinAsync(LiveDeadline()).AsTask());
        Assert.Equal(1, operations.OpenCalls);

        openCompletion.SetResult(true);
        await firstOpen;

        var firstValidation = attempt.ValidateFirstAsync(
            TimeZoneInfo.Local.Id,
            CancellationToken.None).AsTask();
        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.ValidateFirstAsync(
                TimeZoneInfo.Local.Id,
                CancellationToken.None).AsTask());
        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.CloseWithinAsync(LiveDeadline()).AsTask());
        Assert.Equal(1, operations.ServerValidationCalls);
        Assert.Equal(0, operations.EnvironmentCalls);

        validationCompletion.SetResult(Identity);
        var session = await firstValidation;
        Assert.Equal(1, operations.EnvironmentCalls);
        AssertCleanup(await session.CloseWithinAsync(LiveDeadline()));
    }

    [Theory]
    [InlineData(ValidationStage.Server, ProviderFailureMode.SynchronousThrow)]
    [InlineData(ValidationStage.Server, ProviderFailureMode.NullTask)]
    [InlineData(ValidationStage.Server, ProviderFailureMode.NullResult)]
    [InlineData(ValidationStage.Server, ProviderFailureMode.CallerCancellation)]
    [InlineData(ValidationStage.Environment, ProviderFailureMode.SynchronousThrow)]
    [InlineData(ValidationStage.Environment, ProviderFailureMode.NullTask)]
    [InlineData(ValidationStage.Environment, ProviderFailureMode.NullResult)]
    [InlineData(ValidationStage.Environment, ProviderFailureMode.CallerCancellation)]
    public async Task ValidationExceptionalBoundariesPreserveTheOwnedAttempt(
        ValidationStage stage,
        ProviderFailureMode failureMode)
    {
        using var environment = StrictEnvironment();
        using var caller = new CancellationTokenSource();
        if (failureMode == ProviderFailureMode.CallerCancellation)
            caller.Cancel();

        Task<TransferV3PostgreSqlTargetIdentity> ServerFailure()
        {
            return failureMode switch
            {
                ProviderFailureMode.SynchronousThrow =>
                    throw new InvalidOperationException("server-sync-CANARY"),
                ProviderFailureMode.NullTask => null!,
                ProviderFailureMode.NullResult =>
                    Task.FromResult<TransferV3PostgreSqlTargetIdentity>(null!),
                ProviderFailureMode.CallerCancellation =>
                    Task.FromCanceled<TransferV3PostgreSqlTargetIdentity>(caller.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(failureMode)),
            };
        }

        Task<string> EnvironmentFailure()
        {
            return failureMode switch
            {
                ProviderFailureMode.SynchronousThrow =>
                    throw new InvalidOperationException("environment-sync-CANARY"),
                ProviderFailureMode.NullTask => null!,
                ProviderFailureMode.NullResult => Task.FromResult<string>(null!),
                ProviderFailureMode.CallerCancellation =>
                    Task.FromCanceled<string>(caller.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(failureMode)),
            };
        }

        var operations = new FakeProviderOperations
        {
            ValidateServerHandler = stage == ValidationStage.Server
                ? (_, _, _, _) => ServerFailure()
                : static (_, _, _, _) => Task.FromResult(Identity),
            ValidateEnvironmentHandler = stage == ValidationStage.Environment
                ? (_, _, _) => EnvironmentFailure()
                : static (_, _, _) => Task.FromResult(Schema),
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);

        var observed = await Record.ExceptionAsync(() => attempt.ValidateFirstAsync(
            TimeZoneInfo.Local.Id,
            caller.Token).AsTask());

        if (failureMode == ProviderFailureMode.CallerCancellation)
        {
            var cancellation = Assert.IsType<OperationCanceledException>(observed);
            Assert.Equal(caller.Token, cancellation.CancellationToken);
            Assert.Equal("Transfer-v3 Phase 4 was canceled.", cancellation.Message);
        }
        else
        {
            Assert.Equal(
                "phase4-postgresql-command",
                Assert.IsType<TransferV3Phase4Exception>(observed).Code);
        }

        Assert.Equal(1, operations.ServerValidationCalls);
        Assert.Equal(stage == ValidationStage.Server ? 0 : 1, operations.EnvironmentCalls);
        Assert.All(operations.ValidationTokens, token => Assert.Equal(caller.Token, token));
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourWayTimeZoneMismatchStopsBeforeSqlAndPreservesOwnership(
        bool mismatchEnvironment)
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        if (mismatchEnvironment)
        {
            Environment.SetEnvironmentVariable(
                PostgreSqlConnectionPolicy.LegacyTimezoneVariable,
                TimeZoneInfo.Local.Id + "-mismatch");
        }

        var attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);
        var sourceTimeZone = mismatchEnvironment
            ? TimeZoneInfo.Local.Id
            : TimeZoneInfo.Local.Id + "-mismatch";

        await AssertCodeAsync(
            "phase4-postgresql-command",
            () => attempt.ValidateFirstAsync(
                sourceTimeZone,
                CancellationToken.None).AsTask());

        Assert.Equal(0, operations.ServerValidationCalls);
        Assert.Equal(0, operations.EnvironmentCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
    }

    [Theory]
    [InlineData(ValidationMismatchKind.EnvironmentSchema)]
    [InlineData(ValidationMismatchKind.CapturedSchema)]
    [InlineData(ValidationMismatchKind.ExpectedIdentity)]
    public async Task SchemaAndExpectedIdentityMismatchRefuseBeforeTransfer(
        ValidationMismatchKind mismatch)
    {
        using var environment = StrictEnvironment();
        var captured = mismatch == ValidationMismatchKind.CapturedSchema
            ? Identity with { SchemaName = "other_schema" }
            : Identity;
        var environmentSchema = mismatch is ValidationMismatchKind.EnvironmentSchema
            ? "other_schema"
            : captured.SchemaName;
        var expected = mismatch == ValidationMismatchKind.ExpectedIdentity
            ? Identity with { RoleOid = Identity.RoleOid + 1 }
            : Identity;
        var operations = new FakeProviderOperations
        {
            ValidateServerHandler = (_, _, _, _) => Task.FromResult(captured),
            ValidateEnvironmentHandler = (_, _, _) => Task.FromResult(environmentSchema),
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);

        await AssertCodeAsync(
            "phase4-postgresql-command",
            () => attempt.ValidateMatchingAsync(
                TimeZoneInfo.Local.Id,
                expected,
                CancellationToken.None).AsTask());

        Assert.Equal(["create", "open", "server", "environment"], operations.CallOrder);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
    }

    [Fact]
    public async Task OrdinaryMatchingValidationUsesExactIdentityAndCallerTokenForBothCommands()
    {
        using var environment = StrictEnvironment();
        using var caller = new CancellationTokenSource();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);

        var session = await attempt.ValidateMatchingAsync(
            TimeZoneInfo.Local.Id,
            Identity,
            caller.Token);

        Assert.Equal([caller.Token, caller.Token], operations.ValidationTokens);
        Assert.Equal([300], operations.ServerTimeouts);
        Assert.Equal([300], operations.EnvironmentTimeouts);
        Assert.Equal(Identity, session.Identity);
        AssertCleanup(await session.CloseWithinAsync(LiveDeadline()));
    }

    [Fact]
    public async Task AbandonmentWinsPendingOpenAndLateCompletionCannotPublishOrCleanUp()
    {
        using var environment = StrictEnvironment();
        var openCompletion = NewCompletion();
        var operations = new FakeProviderOperations
        {
            OpenHandler = (_, _) => openCompletion.Task,
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        var opening = attempt.OpenAsync(CancellationToken.None).AsTask();

        attempt.AbandonForHelperExit();
        attempt.AbandonForHelperExit();

        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.ValidateFirstAsync(
                TimeZoneInfo.Local.Id,
                CancellationToken.None).AsTask());
        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.CloseWithinAsync(LiveDeadline()).AsTask());
        await AssertCodeAsync("phase4-cleanup", () => descriptor.DisposeAsync().AsTask());
        Assert.Equal(0, operations.DisposeConnectionCalls);
        Assert.Equal(0, operations.DisposeDataSourceCalls);

        openCompletion.SetResult(true);
        await AssertCodeAsync("phase4-unexpected", () => opening);
        Assert.Equal(0, operations.ServerValidationCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
    }

    [Theory]
    [InlineData(ValidationStage.Server, false)]
    [InlineData(ValidationStage.Server, true)]
    [InlineData(ValidationStage.Environment, false)]
    [InlineData(ValidationStage.Environment, true)]
    public async Task AbandonmentDuringValidationDropsLateOutcomeAndNeverPublishesSession(
        ValidationStage stage,
        bool lateFault)
    {
        using var environment = StrictEnvironment();
        var serverCompletion =
            new TaskCompletionSource<TransferV3PostgreSqlTargetIdentity>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var environmentCompletion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new FakeProviderOperations
        {
            ValidateServerHandler = stage == ValidationStage.Server
                ? (_, _, _, _) => serverCompletion.Task
                : static (_, _, _, _) => Task.FromResult(Identity),
            ValidateEnvironmentHandler = stage == ValidationStage.Environment
                ? (_, _, _) => environmentCompletion.Task
                : static (_, _, _) => Task.FromResult(Schema),
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);

        var validation = attempt.ValidateFirstAsync(
            TimeZoneInfo.Local.Id,
            CancellationToken.None).AsTask();
        Assert.Equal(1, operations.ServerValidationCalls);
        Assert.Equal(stage == ValidationStage.Server ? 0 : 1, operations.EnvironmentCalls);

        attempt.AbandonForHelperExit();
        attempt.AbandonForHelperExit();
        if (stage == ValidationStage.Server)
        {
            if (lateFault)
            {
                serverCompletion.SetException(
                    new InvalidOperationException("late-server-CANARY"));
            }
            else
            {
                serverCompletion.SetResult(Identity);
            }
        }
        else if (lateFault)
        {
            environmentCompletion.SetException(
                new InvalidOperationException("late-environment-CANARY"));
        }
        else
        {
            environmentCompletion.SetResult(Schema);
        }

        await AssertCodeAsync("phase4-unexpected", () => validation);
        Assert.Equal(stage == ValidationStage.Server ? 0 : 1, operations.EnvironmentCalls);
        Assert.Equal(0, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.ValidateFirstAsync(
                TimeZoneInfo.Local.Id,
                CancellationToken.None).AsTask());
        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.CloseWithinAsync(LiveDeadline()).AsTask());
        await AssertCodeAsync("phase4-cleanup", () => descriptor.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task AbandonmentDuringCloseFenceCreationStartsNoProviderCall()
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));
        provider.TimestampCallback = attempt.AbandonForHelperExit;

        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.CloseWithinAsync(deadline).AsTask());

        Assert.Equal(0, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        await AssertCodeAsync("phase4-cleanup", () => descriptor.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ProvenAttemptCloseRejectsReentrantAbandonmentDuringFenceDisposal()
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        Exception? abandonmentFailure = null;
        var provider = new ManualTimeProvider
        {
            TimerFactory = (_, _, _, _) => new CallbackDisposeTimer(
                () => abandonmentFailure = Record.Exception(
                    attempt.AbandonForHelperExit)),
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));

        var result = await attempt.CloseWithinAsync(deadline);

        AssertCleanup(result);
        var failure = Assert.IsType<TransferV3Phase4Exception>(abandonmentFailure);
        Assert.Equal("phase4-unexpected", failure.Code);
        Assert.Equal(1, operations.DisposeConnectionCalls);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
        await descriptor.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttemptAbandonmentDuringCloseRunningDropsProviderOutcomeAndRetainsLease(
        bool providerFault)
    {
        using var environment = StrictEnvironment();
        var closeCompletion = NewCompletion();
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ => new ValueTask(closeCompletion.Task),
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        var deadline = LiveDeadline();

        var closing = attempt.CloseWithinAsync(deadline).AsTask();
        Assert.Equal(1, operations.DisposeConnectionCalls);
        attempt.AbandonForHelperExit();
        attempt.AbandonForHelperExit();
        if (providerFault)
        {
            closeCompletion.SetException(
                new InvalidOperationException("abandoned-close-CANARY"));
        }
        else
        {
            closeCompletion.SetResult(true);
        }

        await AssertCodeAsync("phase4-unexpected", () => closing);
        Assert.Equal(1, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.CloseWithinAsync(deadline).AsTask());
        await AssertCodeAsync("phase4-cleanup", () => descriptor.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task QuarantineIsIdempotentAndImmediatelyRevokesFutureSessionBorrows()
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        var (_, session) = await CreateValidatedSessionAsync(descriptor);

        var retainedRawReference = session.BorrowConnection();
        session.Quarantine();
        session.Quarantine();

        Assert.True(session.IsQuarantined);
        AssertCode("phase4-unexpected", () => session.BorrowConnection());
        Assert.NotNull(retainedRawReference);
        AssertCleanup(await session.CloseWithinAsync(LiveDeadline()));
        Assert.Equal(1, operations.DisposeConnectionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseSuccessReleasesOneLeaseAndReplaysWithoutAnotherProviderCall(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);

        var first = await owner.Close(LiveDeadline());
        var expired = ExpiredDeadline();
        var replay = await owner.Close(expired);

        AssertCleanup(first);
        Assert.Equal(first, replay);
        Assert.Equal(1, operations.DisposeConnectionCalls);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
        if (owner.Session is not null)
        {
            Assert.True(owner.Session.IsQuarantined);
            AssertCode("phase4-unexpected", () => owner.Session.BorrowConnection());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FenceDisposalFaultAfterProvenCloseCannotCreateAProviderRetry(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var provider = new ManualTimeProvider
        {
            TimerFactory = (_, _, _, _) => new CallbackDisposeTimer(
                () => throw new InvalidOperationException("timer-dispose-CANARY")),
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));

        var result = await owner.Close(deadline);
        var replay = await owner.Close(ExpiredDeadline());

        AssertCleanup(result);
        Assert.Equal(result, replay);
        Assert.Equal(1, operations.DisposeConnectionCalls);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseFaultRetainsLeaseAndOnlySameLiveDeadlineCanRetry(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        var closeCalls = 0;
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ =>
                Interlocked.Increment(ref closeCalls) == 1
                    ? new ValueTask(Task.FromException(
                        new InvalidOperationException("close-failure-CANARY")))
                    : ValueTask.CompletedTask,
        };
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var deadline = LiveDeadline();

        var failed = await owner.Close(deadline);

        AssertCleanup(
            failed,
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        Assert.Equal(1, operations.DisposeConnectionCalls);
        await AssertCodeAsync(
            "phase4-unexpected",
            () => owner.Close(LiveDeadline()).AsTask());

        var retried = await owner.Close(deadline);
        AssertCleanup(retried);
        Assert.Equal(2, operations.DisposeConnectionCalls);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousCloseProviderFaultIsRetryableOnlyUnderSameDeadline(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        var providerCalls = 0;
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ =>
            {
                if (Interlocked.Increment(ref providerCalls) == 1)
                    throw new InvalidOperationException("close-sync-CANARY");
                return ValueTask.CompletedTask;
            },
        };
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var deadline = LiveDeadline();

        AssertCleanup(
            await owner.Close(deadline),
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await owner.Close(deadline));
        Assert.Equal(2, operations.DisposeConnectionCalls);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseValueTaskStatusFaultIsSanitizedAndRetryableUnderSameDeadline(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        var providerCalls = 0;
        const string canary = "value-task-status-CANARY";
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ =>
                Interlocked.Increment(ref providerCalls) == 1
                    ? new ValueTask(new ThrowingStatusValueTaskSource(canary), 0)
                    : ValueTask.CompletedTask,
        };
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var deadline = LiveDeadline();

        var failed = await owner.Close(deadline);

        AssertCleanup(
            failed,
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed);
        Assert.DoesNotContain(canary, failed.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await owner.Close(deadline));
        Assert.Equal(2, operations.DisposeConnectionCalls);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseValueTaskConversionFaultIsSanitizedAndRetryableUnderSameDeadline(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        const string canary = "value-task-conversion-CANARY";
        var providerCalls = 0;
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ =>
                Interlocked.Increment(ref providerCalls) == 1
                    ? new ValueTask(new ThrowingConversionValueTaskSource(canary), 0)
                    : ValueTask.CompletedTask,
        };
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var deadline = LiveDeadline();

        var failed = await owner.Close(deadline);

        AssertCleanup(
            failed,
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed);
        Assert.DoesNotContain(canary, failed.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        await AssertCodeAsync("phase4-cleanup", () => descriptor.DisposeAsync().AsTask());

        AssertCleanup(await owner.Close(deadline));
        Assert.Equal(2, operations.DisposeConnectionCalls);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
        await descriptor.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingCloseProviderFaultBeforeExpiryIsRetryableUnderSameDeadline(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        const string canary = "pending-close-fault-CANARY";
        var closeCompletion = NewCompletion();
        var providerCalls = 0;
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ =>
                Interlocked.Increment(ref providerCalls) == 1
                    ? new ValueTask(closeCompletion.Task)
                    : ValueTask.CompletedTask,
        };
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var deadline = LiveDeadline();

        var closing = owner.Close(deadline).AsTask();
        Assert.Equal(1, operations.DisposeConnectionCalls);
        closeCompletion.SetException(new InvalidOperationException(canary));
        var failed = await closing;

        AssertCleanup(
            failed,
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed);
        Assert.DoesNotContain(canary, failed.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        await AssertCodeAsync("phase4-cleanup", () => descriptor.DisposeAsync().AsTask());

        AssertCleanup(await owner.Close(deadline));
        Assert.Equal(2, operations.DisposeConnectionCalls);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
        await descriptor.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseRetryRefusesWhenTheSameDeadlineHasExpired(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ => new ValueTask(Task.FromException(
                new InvalidOperationException("close-failure-CANARY"))),
        };
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));
        AssertCleanup(
            await owner.Close(deadline),
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed);
        provider.SetElapsed(TimeSpan.FromSeconds(5));

        await AssertCodeAsync(
            "phase4-unexpected",
            () => owner.Close(deadline).AsTask());

        Assert.Equal(1, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryDeadlineSamplingFaultMapsToCloseFailedWithoutProviderCall(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        var closeCalls = 0;
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ =>
                Interlocked.Increment(ref closeCalls) == 1
                    ? new ValueTask(Task.FromException(new InvalidOperationException()))
                    : ValueTask.CompletedTask,
        };
        var descriptor = CreateDescriptor(operations);
        Func<TransferV3PostgreSqlDeadline, ValueTask<TransferV3Phase4CleanupResult>>
            close;
        if (useSession)
        {
            var (_, session) = await CreateValidatedSessionAsync(descriptor);
            close = session.CloseWithinAsync;
        }
        else
        {
            var attempt = descriptor.CreateOpenAttempt();
            close = attempt.CloseWithinAsync;
        }

        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));
        AssertCleanup(
            await close(deadline),
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed);
        provider.TimestampCallback = () =>
            throw new InvalidOperationException("retry-sampling-CANARY");

        var samplingFailure = await close(deadline);

        AssertCleanup(
            samplingFailure,
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed);
        Assert.Equal(1, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await close(deadline));
        Assert.Equal(2, operations.DisposeConnectionCalls);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
    }

    [Fact]
    public async Task AbandonmentDuringRetryDeadlineSamplingCannotResurrectAttempt()
    {
        using var environment = StrictEnvironment();
        var closeCalls = 0;
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ =>
                Interlocked.Increment(ref closeCalls) == 1
                    ? new ValueTask(Task.FromException(new InvalidOperationException()))
                    : ValueTask.CompletedTask,
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));
        AssertCleanup(
            await attempt.CloseWithinAsync(deadline),
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed);
        provider.TimestampCallback = attempt.AbandonForHelperExit;

        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.CloseWithinAsync(deadline).AsTask());

        Assert.Equal(1, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        await AssertCodeAsync("phase4-cleanup", () => descriptor.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ReentrantSessionRetryDuringDeadlineSamplingCannotStartTwoProviderCalls()
    {
        using var environment = StrictEnvironment();
        var closeCalls = 0;
        var closeCompletion = NewCompletion();
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ =>
                Interlocked.Increment(ref closeCalls) == 1
                    ? new ValueTask(Task.FromException(new InvalidOperationException()))
                    : new ValueTask(closeCompletion.Task),
        };
        var descriptor = CreateDescriptor(operations);
        var (_, session) = await CreateValidatedSessionAsync(descriptor);
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));
        AssertCleanup(
            await session.CloseWithinAsync(deadline),
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed);
        Task? nestedRetry = null;
        provider.TimestampCallback = () =>
            nestedRetry = session.CloseWithinAsync(deadline).AsTask();

        var retry = session.CloseWithinAsync(deadline).AsTask();
        try
        {
            Assert.Equal(2, operations.DisposeConnectionCalls);
        }
        finally
        {
            closeCompletion.TrySetResult(true);
        }

        var nested = Assert.IsAssignableFrom<Task>(nestedRetry);
        await AssertCodeAsync("phase4-unexpected", () => nested);
        AssertCleanup(await retry);
        Assert.Equal(2, operations.DisposeConnectionCalls);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseExpiredBeforeStartIsTerminalAndStartsNoProviderCall(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var deadline = ExpiredDeadline();

        var result = await owner.Close(deadline);
        var replay = await owner.Close(deadline);

        AssertCleanup(
            result,
            TransferV3Phase4SecondaryCode.CleanupDeadlineExceeded);
        Assert.Equal(result, replay);
        Assert.Equal(0, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        if (owner.Session is not null)
            Assert.True(owner.Session.IsQuarantined);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CloseAbandonmentIsTerminalAndLateOutcomeCannotChangeOrReleaseIt(
        bool useSession,
        bool lateFault)
    {
        using var environment = StrictEnvironment();
        var closeCompletion = NewCompletion();
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ => new ValueTask(closeCompletion.Task),
        };
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));

        var closing = owner.Close(deadline).AsTask();
        Assert.Equal(1, operations.DisposeConnectionCalls);
        if (owner.Session is not null)
            AssertCode("phase4-unexpected", () => owner.Session.BorrowConnection());
        await AssertCodeAsync(
            "phase4-unexpected",
            () => owner.Close(deadline).AsTask());

        provider.SetElapsed(TimeSpan.FromSeconds(5));
        Assert.True(provider.SingleTimer.Fire());
        var abandoned = await closing;
        AssertCleanup(
            abandoned,
            TransferV3Phase4SecondaryCode.DeadlineAbandonedProviderTask,
            TransferV3Phase4SecondaryCode.CleanupDeadlineExceeded);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);

        if (lateFault)
        {
            closeCompletion.SetException(
                new InvalidOperationException("late-close-CANARY"));
        }
        else
        {
            closeCompletion.SetResult(true);
        }
        await Task.Yield();
        var replay = await owner.Close(deadline);
        Assert.Equal(abandoned, replay);
        Assert.Equal(1, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyCompletedProviderDisposeWinsAnExactFenceTie(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        var provider = new ManualTimeProvider();
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ =>
            {
                Assert.True(provider.SingleTimer.Fire());
                return ValueTask.CompletedTask;
            },
        };
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));

        var result = await owner.Close(deadline);

        AssertCleanup(result);
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
        Assert.Equal(1, operations.DisposeConnectionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationFenceConstructionFaultReturnsCloseFailedWithoutProviderCall(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var provider = new ManualTimeProvider
        {
            TimerCreationFailure = new InvalidOperationException("timer-failure-CANARY"),
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));

        var result = await owner.Close(deadline);

        AssertCleanup(
            result,
            TransferV3Phase4SecondaryCode.ConnectionCloseFailed);
        Assert.Equal(0, operations.DisposeConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseWinningQuarantinesImmediatelyAndRejectsConcurrentClose(
        bool useSession)
    {
        using var environment = StrictEnvironment();
        var closeCompletion = NewCompletion();
        var operations = new FakeProviderOperations
        {
            DisposeConnectionHandler = _ => new ValueTask(closeCompletion.Task),
        };
        var descriptor = CreateDescriptor(operations);
        var owner = await CreateCloseOwnerAsync(descriptor, useSession);
        var deadline = LiveDeadline();

        var closing = owner.Close(deadline).AsTask();

        if (owner.Session is not null)
        {
            Assert.True(owner.Session.IsQuarantined);
            AssertCode("phase4-unexpected", () => owner.Session.BorrowConnection());
        }
        await AssertCodeAsync(
            "phase4-unexpected",
            () => owner.Close(deadline).AsTask());
        Assert.Equal(1, operations.DisposeConnectionCalls);

        closeCompletion.SetResult(true);
        AssertCleanup(await closing);
    }

    [Fact]
    public async Task ReconciliationValidationUsesFreshFencesForBothCommands()
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(10));

        var session = await attempt.ValidateMatchingWithinAsync(
            TimeZoneInfo.Local.Id,
            Identity,
            deadline);

        Assert.Equal([10], operations.ServerTimeouts);
        Assert.Equal([10], operations.EnvironmentTimeouts);
        Assert.Equal(2, operations.ValidationTokens.Length);
        Assert.NotEqual(operations.ValidationTokens[0], operations.ValidationTokens[1]);
        Assert.Equal(2, provider.TimerCreations);
        Assert.All(provider.Timers, timer => Assert.True(timer.IsDisposed));
        AssertCleanup(await session.CloseWithinAsync(LiveDeadline()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconciliationExpiryBeforeOrBetweenCommandsStopsLaterProviderWork(
        bool expiredBeforeServer)
    {
        using var environment = StrictEnvironment();
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(10));
        if (expiredBeforeServer)
            provider.SetElapsed(TimeSpan.FromSeconds(10));

        var operations = new FakeProviderOperations
        {
            ValidateServerHandler = (_, _, _, _) =>
            {
                if (!expiredBeforeServer)
                    provider.SetElapsed(TimeSpan.FromSeconds(10));
                return Task.FromResult(Identity);
            },
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);

        await AssertCodeAsync(
            "phase4-postgresql-command",
            () => attempt.ValidateMatchingWithinAsync(
                TimeZoneInfo.Local.Id,
                Identity,
                deadline).AsTask());

        Assert.Equal(expiredBeforeServer ? 0 : 1, operations.ServerValidationCalls);
        Assert.Equal(0, operations.EnvironmentCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciliationFenceOrAuthoritativeSamplingFaultStopsEnvironment(
        bool failSecondFenceCreation)
    {
        using var environment = StrictEnvironment();
        var timerCreations = 0;
        var provider = new ManualTimeProvider
        {
            TimerFactory = (callback, state, dueTime, period) =>
            {
                if (failSecondFenceCreation
                    && Interlocked.Increment(ref timerCreations) == 2)
                {
                    throw new InvalidOperationException("second-fence-CANARY");
                }

                return new ManualTimer(callback, state, dueTime, period);
            },
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(10));
        var operations = new FakeProviderOperations
        {
            ValidateServerHandler = (_, _, _, _) =>
            {
                if (!failSecondFenceCreation)
                {
                    provider.TimestampCallback = () =>
                        throw new InvalidOperationException("resample-CANARY");
                }

                return Task.FromResult(Identity);
            },
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);

        await AssertCodeAsync(
            "phase4-unexpected",
            () => attempt.ValidateMatchingWithinAsync(
                TimeZoneInfo.Local.Id,
                Identity,
                deadline).AsTask());

        Assert.Equal(1, operations.ServerValidationCalls);
        Assert.Equal(0, operations.EnvironmentCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
    }

    [Theory]
    [InlineData(ValidationStage.Server)]
    [InlineData(ValidationStage.Environment)]
    public async Task ReconciliationDoesNotSampleDeadlineAfterProviderPrimary(
        ValidationStage stage)
    {
        using var environment = StrictEnvironment();
        var provider = new ManualTimeProvider();
        TransferV3PostgreSqlOpenAttempt? attempt = null;
        var primary = TransferV3Phase4Exception.Create(
            new InvalidOperationException("validation-primary-CANARY"),
            TransferV3Phase4Boundary.PostgreSqlOpen);
        Task<TransferV3PostgreSqlTargetIdentity> FailServer()
        {
            provider.TimestampCallback = attempt!.AbandonForHelperExit;
            return Task.FromException<TransferV3PostgreSqlTargetIdentity>(primary);
        }

        Task<string> FailEnvironment()
        {
            provider.TimestampCallback = attempt!.AbandonForHelperExit;
            return Task.FromException<string>(primary);
        }

        var operations = new FakeProviderOperations
        {
            ValidateServerHandler = stage == ValidationStage.Server
                ? (_, _, _, _) => FailServer()
                : static (_, _, _, _) => Task.FromResult(Identity),
            ValidateEnvironmentHandler = stage == ValidationStage.Environment
                ? (_, _, _) => FailEnvironment()
                : static (_, _, _) => Task.FromResult(Schema),
        };
        var descriptor = CreateDescriptor(operations);
        attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));

        var observed = await AssertCodeAsync(
            "phase4-postgresql-open",
            () => attempt.ValidateMatchingWithinAsync(
                TimeZoneInfo.Local.Id,
                Identity,
                deadline).AsTask());

        Assert.Same(primary, observed);
        Assert.Equal(stage == ValidationStage.Server ? 0 : 1, operations.EnvironmentCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
    }

    [Fact]
    public async Task ServerReconciliationCleanupFaultCannotReplaceProviderPrimaryFailure()
    {
        using var environment = StrictEnvironment();
        var primary = TransferV3Phase4Exception.Create(
            new InvalidOperationException("server-primary-CANARY"),
            TransferV3Phase4Boundary.PostgreSqlOpen);
        var operations = new FakeProviderOperations
        {
            ValidateServerHandler = (_, _, _, _) =>
                Task.FromException<TransferV3PostgreSqlTargetIdentity>(primary),
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);
        var provider = new ManualTimeProvider
        {
            TimerFactory = (_, _, _, _) => new CallbackDisposeTimer(
                () => throw new InvalidOperationException("server-cleanup-CANARY")),
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));

        var observed = await AssertCodeAsync(
            "phase4-postgresql-open",
            () => attempt.ValidateMatchingWithinAsync(
                TimeZoneInfo.Local.Id,
                Identity,
                deadline).AsTask());

        Assert.Same(primary, observed);
        Assert.Equal(1, operations.ServerValidationCalls);
        Assert.Equal(0, operations.EnvironmentCalls);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
    }

    [Fact]
    public async Task EnvironmentReconciliationCleanupFaultCannotReplaceProviderPrimaryFailure()
    {
        using var environment = StrictEnvironment();
        var primary = TransferV3Phase4Exception.Create(
            new InvalidOperationException("environment-primary-CANARY"),
            TransferV3Phase4Boundary.PostgreSqlOpen);
        var operations = new FakeProviderOperations
        {
            ValidateEnvironmentHandler = (_, _, _) =>
                Task.FromException<string>(primary),
        };
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);
        var timerCreations = 0;
        var provider = new ManualTimeProvider
        {
            TimerFactory = (callback, state, dueTime, period) =>
                Interlocked.Increment(ref timerCreations) == 2
                    ? new CallbackDisposeTimer(
                        () => throw new InvalidOperationException(
                            "environment-cleanup-CANARY"))
                    : new ManualTimer(callback, state, dueTime, period),
        };
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));

        var observed = await AssertCodeAsync(
            "phase4-postgresql-open",
            () => attempt.ValidateMatchingWithinAsync(
                TimeZoneInfo.Local.Id,
                Identity,
                deadline).AsTask());

        Assert.Same(primary, observed);
        Assert.Equal(1, operations.ServerValidationCalls);
        Assert.Equal(1, operations.EnvironmentCalls);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
    }

    [Fact]
    public void ProviderAdapterAndConnectionFactorySourceOwnershipIsExact()
    {
        var phase4Directory = SqliteContractTestSupport.AbsolutePath(
            "backend/Database/Transfer/Phase4");
        var productionSources = Directory.EnumerateFiles(
                phase4Directory,
                "*.cs",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (Path: path, Root: ParseAbsoluteSource(path)))
            .ToArray();
        var productionInvocations = productionSources
            .SelectMany(source => source.Root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(invocation => (source.Path, Invocation: invocation)))
            .ToArray();

        Assert.DoesNotContain(
            productionInvocations,
            item => InvocationName(item.Invocation) is
                "OpenConnection" or "OpenConnectionAsync");

        var connectionFactories = productionInvocations
            .Where(item => InvocationName(item.Invocation) == "CreateConnection")
            .ToArray();
        Assert.Equal(2, connectionFactories.Length);
        Assert.Contains(
            connectionFactories,
            item => IsInvocationOwnedBy(
                item.Invocation,
                nameof(TransferV3PostgreSqlTargetDescriptor),
                "CreateOpenAttempt",
                "_operations"));
        Assert.Contains(
            connectionFactories,
            item => IsInvocationOwnedBy(
                item.Invocation,
                nameof(TransferV3PostgreSqlProviderOperations),
                "CreateConnection",
                "dataSource"));

        var providerRoot = ParseSource(ProviderOperationsSourcePath);
        var providerClass = Assert.Single(
            providerRoot.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            declaration => declaration.Identifier.ValueText
                == nameof(TransferV3PostgreSqlProviderOperations));
        AssertExactProviderDelegate(
            providerClass,
            "CreateConnection",
            "dataSource",
            "CreateConnection",
            []);
        AssertExactProviderDelegate(
            providerClass,
            "OpenAsync",
            "connection",
            "OpenAsync",
            ["cancellationToken"]);
        AssertExactProviderDelegate(
            providerClass,
            "ValidateServerAsync",
            nameof(TransferV3PostgreSqlServerContract),
            "ValidateAndCaptureAsync",
            [
                "connection",
                "expectedTimeZoneId",
                "commandTimeoutSeconds",
                "cancellationToken",
            ]);
        AssertExactProviderDelegate(
            providerClass,
            "ValidateEnvironmentAsync",
            nameof(PostgreSqlEnvironmentContract),
            "ValidateAsync",
            ["connection", "commandTimeoutSeconds", "cancellationToken"]);
        AssertExactProviderDelegate(
            providerClass,
            "DisposeConnectionAsync",
            "connection",
            "DisposeAsync",
            []);
        AssertExactProviderDelegate(
            providerClass,
            "DisposeDataSourceAsync",
            "dataSource",
            "DisposeAsync",
            []);

        var instance = Assert.Single(
            providerClass.Members.OfType<PropertyDeclarationSyntax>(),
            property => property.Identifier.ValueText == "Instance");
        Assert.Contains(instance.Modifiers, modifier => modifier.IsKind(
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword));
        var instanceAccessors = Assert.IsType<AccessorListSyntax>(
            instance.AccessorList).Accessors;
        Assert.DoesNotContain(
            instanceAccessors,
            accessor => accessor.IsKind(
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SetAccessorDeclaration)
                || accessor.IsKind(
                    Microsoft.CodeAnalysis.CSharp.SyntaxKind.InitAccessorDeclaration));
        var constructor = Assert.Single(
            providerClass.Members.OfType<ConstructorDeclarationSyntax>());
        Assert.Contains(constructor.Modifiers, modifier => modifier.IsKind(
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword));
        Assert.DoesNotContain(
            providerClass.Members.OfType<FieldDeclarationSyntax>(),
            field => field.Modifiers.Any(modifier => modifier.IsKind(
                         Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword))
                     && !field.Modifiers.Any(modifier => modifier.IsKind(
                         Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReadOnlyKeyword)));

        var descriptorRoot = ParseSource(TargetDescriptorSourcePath);
        var operationsField = Assert.Single(
            descriptorRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            variable => variable.Identifier.ValueText == "_operations");
        var fieldDeclaration = Assert.IsType<FieldDeclarationSyntax>(operationsField.Parent?.Parent);
        Assert.DoesNotContain(fieldDeclaration.Modifiers, modifier => modifier.IsKind(
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword));
        var createAttempt = Assert.Single(
            descriptorRoot.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "CreateOpenAttempt");
        Assert.DoesNotContain(
            createAttempt.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            access => access.Expression.ToString() == "connection"
                      && access.Name.Identifier.ValueText == "State");
        var createCall = Assert.Single(
            createAttempt.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "CreateConnection");
        var leaseIncrement = Assert.Single(
            createAttempt.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>(),
            expression => expression.Operand.ToString()
                == "_activeLifecycleLeaseCount");
        Assert.True(createCall.SpanStart < leaseIncrement.SpanStart);
    }

    [Fact]
    public void AttemptOwnerShellPrecedesConnectionCreationAndAttachmentTailCannotThrow()
    {
        var descriptorRoot = ParseSource(TargetDescriptorSourcePath);
        var createAttempt = Assert.Single(
            descriptorRoot.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "CreateOpenAttempt");
        var ownerAllocation = Assert.Single(
            createAttempt.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            creation => creation.Type.ToString()
                == nameof(TransferV3PostgreSqlOpenAttempt));
        var connectionFactory = Assert.Single(
            createAttempt.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "CreateConnection");
        var attachment = Assert.Single(
            createAttempt.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "AttachUnpublishedConnection");
        var leaseIncrement = Assert.Single(
            createAttempt.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>(),
            expression => expression.Operand.ToString()
                == "_activeLifecycleLeaseCount");
        var publication = Assert.Single(
            createAttempt.DescendantNodes().OfType<ReturnStatementSyntax>());

        Assert.True(ownerAllocation.SpanStart < connectionFactory.SpanStart);
        Assert.True(connectionFactory.SpanStart < attachment.SpanStart);
        Assert.True(attachment.SpanStart < leaseIncrement.SpanStart);
        Assert.True(leaseIncrement.SpanStart < publication.SpanStart);
        Assert.DoesNotContain(
            createAttempt.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            access => access.Expression.ToString() == "connection"
                      && access.Name.Identifier.ValueText == "State");
        Assert.DoesNotContain(
            createAttempt.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) is
                "Close" or "CloseAsync" or "Dispose" or "DisposeAsync"
                or "CreateCommandFence" or "CreateOperationFence");

        var attemptRoot = ParseSource(OpenAttemptSourcePath);
        var constructor = Assert.Single(
            attemptRoot.DescendantNodes().OfType<ConstructorDeclarationSyntax>(),
            candidate => candidate.Identifier.ValueText
                == nameof(TransferV3PostgreSqlOpenAttempt));
        Assert.Equal(
            ["descriptor", "operations"],
            constructor.ParameterList.Parameters
                .Select(parameter => parameter.Identifier.ValueText));
        var attachMethod = Assert.Single(
            attemptRoot.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "AttachUnpublishedConnection");
        Assert.Equal(
            ["connection"],
            attachMethod.ParameterList.Parameters
                .Select(parameter => parameter.Identifier.ValueText));
        var attachBody = Assert.IsType<BlockSyntax>(attachMethod.Body);
        var attachStatement = Assert.Single(attachBody.Statements);
        var attachExpression = Assert.IsType<ExpressionStatementSyntax>(attachStatement);
        var assignment = Assert.IsType<AssignmentExpressionSyntax>(attachExpression.Expression);
        Assert.Equal("_connection", assignment.Left.ToString());
        Assert.Equal("connection", assignment.Right.ToString());
        Assert.Empty(attachMethod.DescendantNodes().OfType<InvocationExpressionSyntax>());
        Assert.Empty(attachMethod.DescendantNodes().OfType<ObjectCreationExpressionSyntax>());
        Assert.Empty(attachMethod.DescendantNodes().OfType<ThrowStatementSyntax>());
        Assert.Empty(attachMethod.DescendantNodes().OfType<LockStatementSyntax>());
        Assert.Empty(attachMethod.DescendantNodes().OfType<IfStatementSyntax>());

        var phase4Roots = Directory.EnumerateFiles(
                SqliteContractTestSupport.AbsolutePath(
                    "backend/Database/Transfer/Phase4"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(ParseAbsoluteSource)
            .ToArray();
        var ownerAllocations = phase4Roots
            .SelectMany(root => root.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>())
            .Where(creation => creation.Type.ToString()
                == nameof(TransferV3PostgreSqlOpenAttempt))
            .ToArray();
        var onlyOwnerAllocation = Assert.Single(ownerAllocations);
        Assert.Equal(
            "CreateOpenAttempt",
            onlyOwnerAllocation.Ancestors().OfType<MethodDeclarationSyntax>()
                .First().Identifier.ValueText);
        Assert.Equal(
            nameof(TransferV3PostgreSqlTargetDescriptor),
            onlyOwnerAllocation.Ancestors().OfType<ClassDeclarationSyntax>()
                .First().Identifier.ValueText);
        var attachmentSites = phase4Roots
            .SelectMany(root => root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>())
            .Where(invocation => InvocationName(invocation)
                == "AttachUnpublishedConnection")
            .ToArray();
        var onlyAttachment = Assert.Single(attachmentSites);
        Assert.Equal(
            "CreateOpenAttempt",
            onlyAttachment.Ancestors().OfType<MethodDeclarationSyntax>()
                .First().Identifier.ValueText);
        Assert.Equal(
            nameof(TransferV3PostgreSqlTargetDescriptor),
            onlyAttachment.Ancestors().OfType<ClassDeclarationSyntax>()
                .First().Identifier.ValueText);

        var openMethod = Assert.Single(
            attemptRoot.DescendantNodes().OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "OpenAsync");
        var closedStateRead = Assert.Single(
            openMethod.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            access => access.Expression.ToString() == "_connection"
                      && access.Name.Identifier.ValueText == "State");
        var providerOpen = Assert.Single(
            openMethod.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation)
                == nameof(ITransferV3PostgreSqlProviderOperations.OpenAsync));
        Assert.True(closedStateRead.SpanStart < providerOpen.SpanStart);
    }

    [Fact]
    public void ProviderCloseInvocationsRemainOwnedByEachLifecycleGate()
    {
        var sources = new[]
        {
            (Path: SessionSourcePath, Owner: nameof(TransferV3PostgreSqlSession)),
            (Path: OpenAttemptSourcePath, Owner: nameof(TransferV3PostgreSqlOpenAttempt)),
        };

        foreach (var source in sources)
        {
            var root = ParseSource(source.Path);
            var invocation = Assert.Single(
                root.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                candidate => InvocationName(candidate)
                    == nameof(ITransferV3PostgreSqlProviderOperations.DisposeConnectionAsync));
            var member = Assert.IsType<MemberAccessExpressionSyntax>(
                invocation.Expression);
            Assert.Equal("_operations", member.Expression.ToString());
            Assert.Equal(
                source.Owner,
                invocation.Ancestors().OfType<ClassDeclarationSyntax>()
                    .First().Identifier.ValueText);
            Assert.Equal(
                "StartCloseProvider",
                invocation.Ancestors().OfType<MethodDeclarationSyntax>()
                    .First().Identifier.ValueText);
            Assert.Contains(
                invocation.Ancestors().OfType<LockStatementSyntax>(),
                ownerLock => ownerLock.Expression.ToString() == "_gate");
        }
    }

    [Fact]
    public async Task DescriptorCreationGateRejectsReentrantCreateAndDispose()
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        Exception? nestedCreate = null;
        Exception? nestedDispose = null;
        operations.CreateConnectionHandler = dataSource =>
        {
            nestedCreate = Record.Exception(() => descriptor.CreateOpenAttempt());
            nestedDispose = Record.Exception(
                () => descriptor.DisposeAsync().AsTask().GetAwaiter().GetResult());
            return dataSource.CreateConnection();
        };

        var attempt = descriptor.CreateOpenAttempt();

        Assert.Equal(
            "phase4-unexpected",
            Assert.IsType<TransferV3Phase4Exception>(nestedCreate).Code);
        Assert.Equal(
            "phase4-unexpected",
            Assert.IsType<TransferV3Phase4Exception>(nestedDispose).Code);
        Assert.Equal(1, operations.CreateConnectionCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
        await descriptor.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DescriptorFactorySyncThrowOrNullRegistersNoLeaseAndClearsGate(
        bool returnNull)
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations
        {
            CreateConnectionHandler = returnNull
                ? static _ => null
                : static _ => throw new InvalidOperationException(
                    "create-connection-CANARY"),
        };
        var descriptor = CreateDescriptor(operations);

        AssertCode("phase4-unexpected", () => descriptor.CreateOpenAttempt());
        Assert.Equal(0, descriptor.ActiveLifecycleLeaseCount);
        Assert.Equal(1, operations.CreateConnectionCalls);

        operations.CreateConnectionHandler = static dataSource =>
            dataSource.CreateConnection();
        var attempt = descriptor.CreateOpenAttempt();
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
        await descriptor.DisposeAsync();
    }

    [Fact]
    public async Task DescriptorDisposalGateRejectsSameThreadReentrantUse()
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        Exception? nestedCreate = null;
        Exception? nestedDispose = null;
        operations.DisposeDataSourceHandler = _ =>
        {
            nestedCreate = Record.Exception(() => descriptor.CreateOpenAttempt());
            nestedDispose = Record.Exception(
                () => descriptor.DisposeAsync().AsTask().GetAwaiter().GetResult());
            return ValueTask.CompletedTask;
        };

        await descriptor.DisposeAsync();

        Assert.Equal(
            "phase4-unexpected",
            Assert.IsType<TransferV3Phase4Exception>(nestedCreate).Code);
        Assert.Equal(
            "phase4-unexpected",
            Assert.IsType<TransferV3Phase4Exception>(nestedDispose).Code);
        Assert.Equal(1, operations.DisposeDataSourceCalls);
        await descriptor.DisposeAsync();
        Assert.Equal(1, operations.DisposeDataSourceCalls);
    }

    [Fact]
    public async Task DescriptorSynchronousDisposalFaultIsSanitizedCachedAndReplayed()
    {
        using var environment = StrictEnvironment();
        var raw = new InvalidOperationException("dispose-sync-CANARY");
        var operations = new FakeProviderOperations
        {
            DisposeDataSourceHandler = _ => throw raw,
        };
        var descriptor = CreateDescriptor(operations);

        var first = await AssertCodeAsync(
            "phase4-cleanup",
            () => descriptor.DisposeAsync().AsTask());
        var replay = await AssertCodeAsync(
            "phase4-cleanup",
            () => descriptor.DisposeAsync().AsTask());

        Assert.Same(first, replay);
        Assert.Equal(1, operations.DisposeDataSourceCalls);
        Assert.DoesNotContain(raw.Message, first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescriptorDisposalRefusesRetainedLeaseWithoutProviderAccess()
    {
        using var environment = StrictEnvironment();
        var operations = new FakeProviderOperations();
        var descriptor = CreateDescriptor(operations);
        var attempt = descriptor.CreateOpenAttempt();

        await AssertCodeAsync("phase4-cleanup", () => descriptor.DisposeAsync().AsTask());

        Assert.Equal(0, operations.DisposeDataSourceCalls);
        Assert.Equal(1, descriptor.ActiveLifecycleLeaseCount);
        AssertCleanup(await attempt.CloseWithinAsync(LiveDeadline()));
        await descriptor.DisposeAsync();
        Assert.Equal(1, operations.DisposeDataSourceCalls);
    }

    [Fact]
    public async Task DescriptorDisposalGateRejectsConcurrentUseAndSuccessReplaysAsNoOp()
    {
        using var environment = StrictEnvironment();
        var disposeCompletion = NewCompletion();
        var operations = new FakeProviderOperations
        {
            DisposeDataSourceHandler = _ => new ValueTask(disposeCompletion.Task),
        };
        var descriptor = CreateDescriptor(operations);

        var disposing = descriptor.DisposeAsync().AsTask();
        Assert.Equal(1, operations.DisposeDataSourceCalls);
        AssertCode("phase4-unexpected", () => descriptor.CreateOpenAttempt());
        await AssertCodeAsync("phase4-unexpected", () => descriptor.DisposeAsync().AsTask());

        disposeCompletion.SetResult(true);
        await disposing;
        await descriptor.DisposeAsync();
        Assert.Equal(1, operations.DisposeDataSourceCalls);
        AssertCode("phase4-unexpected", () => descriptor.CreateOpenAttempt());
    }

    [Fact]
    public async Task DescriptorDisposalFaultIsSanitizedCachedAndReplayedByReference()
    {
        using var environment = StrictEnvironment();
        var raw = new InvalidOperationException("data-source-dispose-CANARY");
        var operations = new FakeProviderOperations
        {
            DisposeDataSourceHandler = _ => new ValueTask(Task.FromException(raw)),
        };
        var descriptor = CreateDescriptor(operations);

        var first = await AssertCodeAsync(
            "phase4-cleanup",
            () => descriptor.DisposeAsync().AsTask());
        var replay = await AssertCodeAsync(
            "phase4-cleanup",
            () => descriptor.DisposeAsync().AsTask());

        Assert.Same(first, replay);
        Assert.Equal(1, operations.DisposeDataSourceCalls);
        Assert.DoesNotContain(raw.Message, first.ToString(), StringComparison.Ordinal);
        AssertCode("phase4-unexpected", () => descriptor.CreateOpenAttempt());
    }

    private static CompilationUnitSyntax ParseSource(string repositoryRelativePath)
    {
        var absolutePath = SqliteContractTestSupport.AbsolutePath(repositoryRelativePath);
        return ParseAbsoluteSource(absolutePath);
    }

    private static CompilationUnitSyntax ParseAbsoluteSource(string absolutePath)
    {
        var source = File.ReadAllText(absolutePath);
        var tree = CSharpSyntaxTree.ParseText(source, path: absolutePath);
        var errors = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(
            errors.Length == 0,
            string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        return Assert.IsType<CompilationUnitSyntax>(tree.GetRoot());
    }

    private static bool IsInvocationOwnedBy(
        InvocationExpressionSyntax invocation,
        string className,
        string methodName,
        string receiver)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member
            || member.Expression.ToString() != receiver)
        {
            return false;
        }

        return invocation.Ancestors().OfType<ClassDeclarationSyntax>()
                   .FirstOrDefault()?.Identifier.ValueText == className
               && invocation.Ancestors().OfType<MethodDeclarationSyntax>()
                   .FirstOrDefault()?.Identifier.ValueText == methodName;
    }

    private static void AssertExactProviderDelegate(
        ClassDeclarationSyntax providerClass,
        string methodName,
        string receiver,
        string invocationName,
        string[] arguments)
    {
        var method = Assert.Single(
            providerClass.Members.OfType<MethodDeclarationSyntax>(),
            candidate => candidate.Identifier.ValueText == methodName);
        var invocation = Assert.IsType<InvocationExpressionSyntax>(
            Assert.IsType<ArrowExpressionClauseSyntax>(method.ExpressionBody).Expression);
        var member = Assert.IsType<MemberAccessExpressionSyntax>(invocation.Expression);
        Assert.Equal(receiver, member.Expression.ToString());
        Assert.Equal(invocationName, member.Name.Identifier.ValueText);
        Assert.Equal(
            arguments,
            invocation.ArgumentList.Arguments
                .Select(argument => argument.Expression.ToString())
                .ToArray());
        Assert.Null(method.Body);
    }

    private static string? InvocationName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            _ => null,
        };

    private static readonly TransferV3PostgreSqlTargetIdentity Identity = new(
        "18446744073709551615",
        new DateTimeOffset(2026, 7, 14, 8, 30, 0, TimeSpan.Zero),
        "nzbdav",
        16384,
        Schema,
        16385,
        "nzbdav",
        16386,
        "16.14",
        160014,
        false,
        false,
        false,
        "127.0.0.1",
        5432);

    private static TransferV3PostgreSqlTargetDescriptor CreateDescriptor(
        ITransferV3PostgreSqlProviderOperations operations) =>
        TransferV3PostgreSqlTargetDescriptor.CreateForTesting(
            BuildConnectionString(),
            operations);

    private static async Task<CloseOwner> CreateCloseOwnerAsync(
        TransferV3PostgreSqlTargetDescriptor descriptor,
        bool useSession)
    {
        var attempt = descriptor.CreateOpenAttempt();
        if (!useSession)
            return new CloseOwner(attempt.CloseWithinAsync, attempt, null);

        await attempt.OpenAsync(CancellationToken.None);
        var session = await attempt.ValidateFirstAsync(
            TimeZoneInfo.Local.Id,
            CancellationToken.None);
        return new CloseOwner(session.CloseWithinAsync, attempt, session);
    }

    private static async Task<(
        TransferV3PostgreSqlOpenAttempt Attempt,
        TransferV3PostgreSqlSession Session)> CreateValidatedSessionAsync(
        TransferV3PostgreSqlTargetDescriptor descriptor)
    {
        var attempt = descriptor.CreateOpenAttempt();
        await attempt.OpenAsync(CancellationToken.None);
        var session = await attempt.ValidateFirstAsync(
            TimeZoneInfo.Local.Id,
            CancellationToken.None);
        return (attempt, session);
    }

    private sealed record CloseOwner(
        Func<TransferV3PostgreSqlDeadline, ValueTask<TransferV3Phase4CleanupResult>> Close,
        TransferV3PostgreSqlOpenAttempt Attempt,
        TransferV3PostgreSqlSession? Session);

    private static TransferV3PostgreSqlDeadline LiveDeadline() =>
        TransferV3PostgreSqlDeadline.Start(
            new ManualTimeProvider(),
            TimeSpan.FromSeconds(5));

    private static TransferV3PostgreSqlDeadline ExpiredDeadline()
    {
        var provider = new ManualTimeProvider();
        var deadline = TransferV3PostgreSqlDeadline.Start(
            provider,
            TimeSpan.FromSeconds(5));
        provider.SetElapsed(TimeSpan.FromSeconds(5));
        return deadline;
    }

    private static string BuildConnectionString() =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "unopened.invalid",
            Port = 5432,
            Database = "nzbdav",
            Username = "nzbdav",
            Password = "session-test-password-canary",
            ApplicationName = TransferV3PostgreSqlTargetDescriptor.ApplicationName,
            SearchPath = Schema,
            ClientEncoding = "UTF8",
            Timezone = TimeZoneInfo.Local.Id,
            SslMode = SslMode.Disable,
            SslNegotiation = SslNegotiation.Postgres,
            GssEncryptionMode = GssEncryptionMode.Disable,
            RequireAuth = "ScramSHA256",
            ChannelBinding = ChannelBinding.Disable,
            PersistSecurityInfo = false,
            LogParameters = false,
            IncludeErrorDetail = false,
            IncludeFailedBatchedCommand = false,
            Pooling = false,
            Enlist = false,
            LoadBalanceHosts = false,
            Multiplexing = false,
            TargetSessionAttributes = "Any",
            Timeout = 5,
            CommandTimeout = 300,
            CancellationTimeout = 2000,
            Options = string.Empty,
        }.ConnectionString;

    private static TaskCompletionSource<bool> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertCleanup(
        TransferV3Phase4CleanupResult result,
        TransferV3Phase4SecondaryCode first = TransferV3Phase4SecondaryCode.None,
        TransferV3Phase4SecondaryCode second = TransferV3Phase4SecondaryCode.None,
        TransferV3Phase4SecondaryCode third = TransferV3Phase4SecondaryCode.None,
        TransferV3Phase4SecondaryCode fourth = TransferV3Phase4SecondaryCode.None)
    {
        Assert.Equal(first, result.First);
        Assert.Equal(second, result.Second);
        Assert.Equal(third, result.Third);
        Assert.Equal(fourth, result.Fourth);
    }

    private static TransferV3Phase4Exception AssertCode(string code, Action action)
    {
        var exception = Assert.IsType<TransferV3Phase4Exception>(Record.Exception(action));
        Assert.Equal(code, exception.Code);
        Assert.Equal("Transfer-v3 Phase 4 failed.", exception.Message);
        Assert.Null(exception.InnerException);
        return exception;
    }

    private static async Task<TransferV3Phase4Exception> AssertCodeAsync(
        string code,
        Func<Task> action)
    {
        var exception = Assert.IsType<TransferV3Phase4Exception>(
            await Record.ExceptionAsync(action));
        Assert.Equal(code, exception.Code);
        Assert.Equal("Transfer-v3 Phase 4 failed.", exception.Message);
        Assert.Null(exception.InnerException);
        return exception;
    }

    private static EnvironmentScope StrictEnvironment()
    {
        var environment = new EnvironmentScope();
        foreach (var variable in NpgsqlAmbientVariables)
            environment.Set(variable, null);
        environment.Set(
            PostgreSqlConnectionPolicy.LegacyTimezoneVariable,
            TimeZoneInfo.Local.Id);
        return environment;
    }

    private sealed class FakeProviderOperations : ITransferV3PostgreSqlProviderOperations
    {
        private readonly object _gate = new();
        private readonly List<string> _callOrder = [];
        private readonly List<NpgsqlConnection> _connections = [];
        private readonly List<CancellationToken> _openTokens = [];
        private readonly List<int> _serverTimeouts = [];
        private readonly List<int> _environmentTimeouts = [];
        private readonly List<CancellationToken> _validationTokens = [];
        private int _createConnectionCalls;
        private int _openCalls;
        private int _serverValidationCalls;
        private int _environmentCalls;
        private int _disposeConnectionCalls;
        private int _disposeDataSourceCalls;

        internal Func<NpgsqlDataSource, NpgsqlConnection?> CreateConnectionHandler
        {
            get;
            set;
        } = static dataSource => dataSource.CreateConnection();

        internal Func<NpgsqlConnection, CancellationToken, Task> OpenHandler { get; init; } =
            static (_, _) => Task.CompletedTask;

        internal Func<
            NpgsqlConnection,
            string,
            int,
            CancellationToken,
            Task<TransferV3PostgreSqlTargetIdentity>> ValidateServerHandler
        { get; init; } =
            static (_, _, _, _) => Task.FromResult(Identity);

        internal Func<NpgsqlConnection, int, CancellationToken, Task<string>>
            ValidateEnvironmentHandler
        { get; init; } =
                static (_, _, _) => Task.FromResult(Schema);

        internal Func<NpgsqlConnection, ValueTask> DisposeConnectionHandler { get; init; } =
            static _ => ValueTask.CompletedTask;

        internal Func<NpgsqlDataSource, ValueTask> DisposeDataSourceHandler { get; set; } =
            static _ => ValueTask.CompletedTask;

        internal int CreateConnectionCalls => Volatile.Read(ref _createConnectionCalls);
        internal int OpenCalls => Volatile.Read(ref _openCalls);
        internal int ServerValidationCalls => Volatile.Read(ref _serverValidationCalls);
        internal int EnvironmentCalls => Volatile.Read(ref _environmentCalls);
        internal int DisposeConnectionCalls => Volatile.Read(ref _disposeConnectionCalls);
        internal int DisposeDataSourceCalls => Volatile.Read(ref _disposeDataSourceCalls);
        internal NpgsqlConnection? CreatedConnection { get; private set; }

        internal string[] CallOrder
        {
            get { lock (_gate) return [.. _callOrder]; }
        }

        internal NpgsqlConnection[] Connections
        {
            get { lock (_gate) return [.. _connections]; }
        }

        internal CancellationToken[] OpenTokens
        {
            get { lock (_gate) return [.. _openTokens]; }
        }

        internal int[] ServerTimeouts
        {
            get { lock (_gate) return [.. _serverTimeouts]; }
        }

        internal int[] EnvironmentTimeouts
        {
            get { lock (_gate) return [.. _environmentTimeouts]; }
        }

        internal CancellationToken[] ValidationTokens
        {
            get { lock (_gate) return [.. _validationTokens]; }
        }

        public NpgsqlConnection CreateConnection(NpgsqlDataSource dataSource)
        {
            ArgumentNullException.ThrowIfNull(dataSource);
            Interlocked.Increment(ref _createConnectionCalls);
            var connection = CreateConnectionHandler(dataSource);
            lock (_gate)
            {
                _callOrder.Add("create");
                if (connection is not null)
                    _connections.Add(connection);
                CreatedConnection = connection;
            }
            return connection!;
        }

        public Task OpenAsync(
            NpgsqlConnection connection,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _openCalls);
            lock (_gate)
            {
                _callOrder.Add("open");
                _openTokens.Add(cancellationToken);
            }
            return OpenHandler(connection, cancellationToken);
        }

        public Task<TransferV3PostgreSqlTargetIdentity> ValidateServerAsync(
            NpgsqlConnection connection,
            string expectedTimeZoneId,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _serverValidationCalls);
            lock (_gate)
            {
                _callOrder.Add("server");
                _serverTimeouts.Add(commandTimeoutSeconds);
                _validationTokens.Add(cancellationToken);
            }
            return ValidateServerHandler(
                connection,
                expectedTimeZoneId,
                commandTimeoutSeconds,
                cancellationToken);
        }

        public Task<string> ValidateEnvironmentAsync(
            NpgsqlConnection connection,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _environmentCalls);
            lock (_gate)
            {
                _callOrder.Add("environment");
                _environmentTimeouts.Add(commandTimeoutSeconds);
                _validationTokens.Add(cancellationToken);
            }
            return ValidateEnvironmentHandler(
                connection,
                commandTimeoutSeconds,
                cancellationToken);
        }

        public ValueTask DisposeConnectionAsync(NpgsqlConnection connection)
        {
            Interlocked.Increment(ref _disposeConnectionCalls);
            lock (_gate)
                _callOrder.Add("dispose-connection");
            return DisposeConnectionHandler(connection);
        }

        public ValueTask DisposeDataSourceAsync(NpgsqlDataSource dataSource)
        {
            Interlocked.Increment(ref _disposeDataSourceCalls);
            lock (_gate)
                _callOrder.Add("dispose-data-source");
            return DisposeDataSourceHandler(dataSource);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        private int _timerCreations;
        private Action? _timestampCallback;

        internal Exception? TimerCreationFailure { get; init; }
        internal Func<TimerCallback, object?, TimeSpan, TimeSpan, ITimer>? TimerFactory
        {
            get;
            init;
        }
        internal Action? TimestampCallback
        {
            set => Volatile.Write(ref _timestampCallback, value);
        }
        internal int TimerCreations => Volatile.Read(ref _timerCreations);

        internal ManualTimer[] Timers
        {
            get { lock (_gate) return [.. _timers]; }
        }

        internal ManualTimer SingleTimer => Assert.Single(Timers);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            Interlocked.Exchange(ref _timestampCallback, null)?.Invoke();
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
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }
    }

    private sealed class CallbackDisposeTimer(Action onDispose) : ITimer
    {
        private int _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            Volatile.Read(ref _disposed) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingStatusValueTaskSource(string canary) : IValueTaskSource
    {
        public void GetResult(short token)
        {
        }

        public ValueTaskSourceStatus GetStatus(short token) =>
            throw new InvalidOperationException(canary);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
            throw new InvalidOperationException(canary);
    }

    private sealed class ThrowingConversionValueTaskSource(string canary) : IValueTaskSource
    {
        public void GetResult(short token)
        {
        }

        public ValueTaskSourceStatus GetStatus(short token) =>
            ValueTaskSourceStatus.Pending;

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
            throw new InvalidOperationException(canary);
    }

    private sealed class ManualTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) : ITimer
    {
        private int _disposed;

        internal TimeSpan DueTime { get; } = dueTime;
        internal TimeSpan Period { get; } = period;
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal bool Fire()
        {
            if (IsDisposed)
                return false;
            callback(state);
            return true;
        }

        public bool Change(TimeSpan newDueTime, TimeSpan newPeriod) => !IsDisposed;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

        internal void Set(string name, string? value)
        {
            if (!_original.ContainsKey(name))
                _original.Add(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
