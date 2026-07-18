using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace NzbWebDAV.Database.Transfer;

internal sealed partial class TransferV3PostgreSqlTargetDescriptor : IAsyncDisposable
{
    internal const string ApplicationName = "nzbdav-transfer-v3-phase4";
    internal const int ConnectionTimeoutSeconds = 5;
    internal const int CommandTimeoutSeconds = 300;
    internal const int CancellationTimeoutMilliseconds = 2000;

    private const string RequiredClientEncoding = "UTF8";
    private const string RequiredAuthentication = "ScramSHA256";
    private const string RequiredInformationalVersion =
        "10.0.3+d3768398c17877b3a916c3c4d87e8e11698991fc";

    private static readonly Version RequiredAssemblyVersion = new(10, 0, 3, 0);

    private static readonly HashSet<string> AllowedCanonicalKeys = new(StringComparer.Ordinal)
    {
        "Host",
        "Port",
        "Database",
        "Username",
        "Password",
        "Application Name",
        "Search Path",
        "Client Encoding",
        "Timezone",
        "SSL Mode",
        "SSL Negotiation",
        "GSS Encryption Mode",
        "Require Auth",
        "Channel Binding",
        "Persist Security Info",
        "Log Parameters",
        "Include Error Detail",
        "Include Failed Batched Command",
        "Pooling",
        "Enlist",
        "Load Balance Hosts",
        "Multiplexing",
        "Target Session Attributes",
        "Timeout",
        "Command Timeout",
        "Cancellation Timeout",
        "Options",
    };

    private static readonly string[] RequiredExplicitKeys =
    [
        "Host",
        "Port",
        "Database",
        "Username",
        "Password",
        "Application Name",
        "Search Path",
        "Client Encoding",
        "Timezone",
        "SSL Mode",
        "SSL Negotiation",
        "GSS Encryption Mode",
        "Require Auth",
        "Channel Binding",
    ];

    private static readonly string[] RejectedAmbientVariables =
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

    private readonly object _lifecycleGate = new();
    private NpgsqlDataSource? _dataSource;
    private ITransferV3PostgreSqlProviderOperations _operations;
    private DescriptorLifecycleState _lifecycleState;
    private int _activeLifecycleLeaseCount;
    private bool _isCreatingAttempt;
    private TransferV3Phase4Exception? _disposeFailure;

    private TransferV3PostgreSqlTargetDescriptor(
        NpgsqlDataSource dataSource,
        string targetSchema,
        string timeZoneId)
    {
        _dataSource = dataSource;
        _operations = TransferV3PostgreSqlProviderOperations.Instance;
        TargetSchema = targetSchema;
        TimeZoneId = timeZoneId;
    }

    internal string TargetSchema { get; }

    internal string TimeZoneId { get; }

    internal int ActiveLifecycleLeaseCount
    {
        get
        {
            lock (_lifecycleGate)
                return _activeLifecycleLeaseCount;
        }
    }

    internal static TransferV3PostgreSqlTargetDescriptor Create(
        string privateConnectionString)
    {
        if (string.IsNullOrWhiteSpace(privateConnectionString))
            throw ArgumentFailure();

        ValidateRuntimeAssemblyIdentity();
        ValidateAmbientEnvironment();

        NpgsqlConnectionStringBuilder supplied;
        try
        {
            supplied = new NpgsqlConnectionStringBuilder(privateConnectionString);
        }
        catch (Exception raw) when (IsCallerSettingFailure(raw))
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Argument,
                default);
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Unexpected,
                default);
        }

        var explicitKeys = new HashSet<string>(supplied.Keys, StringComparer.Ordinal);
        ValidateCanonicalKeys(explicitKeys);
        ValidateRequiredSettings(supplied, explicitKeys);

        string targetSchema;
        try
        {
            PostgreSqlConnectionPolicy.ValidateConnectionString(supplied.ConnectionString);
            targetSchema = PostgreSqlEnvironmentContract.GetRequiredTargetSchema(
                supplied.ConnectionString);
        }
        catch (Exception raw) when (raw is ArgumentException or InvalidOperationException)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Argument,
                default);
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Unexpected,
                default);
        }

        if (!string.Equals(supplied.SearchPath, targetSchema, StringComparison.Ordinal))
            throw ArgumentFailure();

        var timeZoneId = TimeZoneInfo.Local.Id;
        var normalized = new NpgsqlConnectionStringBuilder
        {
            Host = supplied.Host,
            Port = supplied.Port,
            Database = supplied.Database,
            Username = supplied.Username,
            Password = supplied.Password,
            ApplicationName = ApplicationName,
            SearchPath = targetSchema,
            ClientEncoding = RequiredClientEncoding,
            Timezone = timeZoneId,
            SslMode = SslMode.Disable,
            SslNegotiation = SslNegotiation.Postgres,
            GssEncryptionMode = GssEncryptionMode.Disable,
            RequireAuth = RequiredAuthentication,
            ChannelBinding = ChannelBinding.Disable,
            PersistSecurityInfo = false,
            LogParameters = false,
            IncludeErrorDetail = false,
            IncludeFailedBatchedCommand = false,
            Pooling = false,
            Enlist = false,
            LoadBalanceHosts = false,
            Multiplexing = false,
            TargetSessionAttributes = "any",
            Timeout = ConnectionTimeoutSeconds,
            CommandTimeout = CommandTimeoutSeconds,
            CancellationTimeout = CancellationTimeoutMilliseconds,
            Options = string.Empty,
        };

        try
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(normalized.ConnectionString);
            dataSourceBuilder.Name = ApplicationName;
            dataSourceBuilder.UseLoggerFactory(NullLoggerFactory.Instance);
            dataSourceBuilder.EnableParameterLogging(false);
            dataSourceBuilder.ConfigureTracing(static tracing =>
            {
                tracing.ConfigureCommandFilter(static _ => false);
                tracing.ConfigureBatchFilter(static _ => false);
                tracing.ConfigureCopyOperationFilter(static _ => false);
                tracing.EnablePhysicalOpenTracing(false);
            });
            var dataSource = dataSourceBuilder.Build();
            return new TransferV3PostgreSqlTargetDescriptor(
                dataSource,
                targetSchema,
                timeZoneId);
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Unexpected,
                default);
        }
    }

    internal static TransferV3PostgreSqlTargetDescriptor CreateForTesting(
        string privateConnectionString,
        ITransferV3PostgreSqlProviderOperations operations)
    {
        if (operations is null)
        {
            throw TransferV3Phase4Exception.Create(
                new ArgumentNullException(nameof(operations)),
                TransferV3Phase4Boundary.Argument);
        }

        var descriptor = Create(privateConnectionString);
        lock (descriptor._lifecycleGate)
            descriptor._operations = operations;
        return descriptor;
    }

    internal TransferV3PostgreSqlOpenAttempt CreateOpenAttempt()
    {
        lock (_lifecycleGate)
        {
            if (_lifecycleState != DescriptorLifecycleState.Ready
                || _isCreatingAttempt
                || _activeLifecycleLeaseCount == int.MaxValue)
            {
                throw UnexpectedFailure();
            }

            var attempt = new TransferV3PostgreSqlOpenAttempt(
                this,
                _operations);
            NpgsqlConnection connection;
            _isCreatingAttempt = true;
            try
            {
                connection = _operations.CreateConnection(_dataSource!);
                if (connection is null)
                    throw new InvalidOperationException();

                attempt.AttachUnpublishedConnection(connection);
                _activeLifecycleLeaseCount++;
                return attempt;
            }
            catch (Exception raw)
            {
                throw TransferV3Phase4FailureMapper.Sanitize(
                    raw,
                    TransferV3Phase4Boundary.Unexpected,
                    default);
            }
            finally
            {
                _isCreatingAttempt = false;
            }
        }
    }

    internal static void ValidateNpgsqlAssemblyIdentity(
        Version? assemblyVersion,
        string? informationalVersion)
    {
        if (assemblyVersion != RequiredAssemblyVersion
            || !string.Equals(
                informationalVersion,
                RequiredInformationalVersion,
                StringComparison.Ordinal))
        {
            throw TransferV3Phase4Exception.Create(
                new InvalidOperationException(),
                TransferV3Phase4Boundary.Unexpected);
        }
    }

    public ValueTask DisposeAsync()
    {
        NpgsqlDataSource dataSource;
        ITransferV3PostgreSqlProviderOperations operations;
        lock (_lifecycleGate)
        {
            if (_isCreatingAttempt)
                return ValueTask.FromException(UnexpectedFailure());

            switch (_lifecycleState)
            {
                case DescriptorLifecycleState.Disposed:
                    return ValueTask.CompletedTask;
                case DescriptorLifecycleState.DisposeFailed:
                    return ValueTask.FromException(_disposeFailure!);
                case DescriptorLifecycleState.Disposing:
                    return ValueTask.FromException(UnexpectedFailure());
                case DescriptorLifecycleState.Ready:
                    if (_activeLifecycleLeaseCount != 0)
                        return ValueTask.FromException(CleanupFailure());
                    _lifecycleState = DescriptorLifecycleState.Disposing;
                    dataSource = _dataSource!;
                    operations = _operations;
                    break;
                default:
                    return ValueTask.FromException(UnexpectedFailure());
            }
        }

        return DisposeOwnedDataSourceAsync(dataSource, operations);
    }

    internal void ReleaseLifecycleLease()
    {
        lock (_lifecycleGate)
        {
            if (_lifecycleState != DescriptorLifecycleState.Ready
                || _activeLifecycleLeaseCount <= 0)
            {
                throw UnexpectedFailure();
            }

            _activeLifecycleLeaseCount--;
        }
    }

    private async ValueTask DisposeOwnedDataSourceAsync(
        NpgsqlDataSource dataSource,
        ITransferV3PostgreSqlProviderOperations operations)
    {
        try
        {
            await operations.DisposeDataSourceAsync(dataSource).ConfigureAwait(false);
            lock (_lifecycleGate)
            {
                if (_lifecycleState != DescriptorLifecycleState.Disposing)
                    throw UnexpectedFailure();
                _lifecycleState = DescriptorLifecycleState.Disposed;
            }
        }
        catch (Exception raw)
        {
            var failure = AssertCleanupFailure(
                TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Cleanup,
                default));
            lock (_lifecycleGate)
            {
                if (_lifecycleState == DescriptorLifecycleState.Disposing)
                {
                    _disposeFailure = failure;
                    _lifecycleState = DescriptorLifecycleState.DisposeFailed;
                }
                else if (_lifecycleState == DescriptorLifecycleState.DisposeFailed)
                {
                    failure = _disposeFailure!;
                }
            }

            throw failure;
        }
    }

    private static void ValidateRuntimeAssemblyIdentity()
    {
        try
        {
            var assembly = typeof(NpgsqlDataSource).Assembly;
            ValidateNpgsqlAssemblyIdentity(
                assembly.GetName().Version,
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion);
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Unexpected,
                default);
        }
    }

    private static void ValidateAmbientEnvironment()
    {
        try
        {
            foreach (var variable in RejectedAmbientVariables)
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
                    throw ArgumentFailure();
            }
        }
        catch (Exception raw)
        {
            throw TransferV3Phase4FailureMapper.Sanitize(
                raw,
                TransferV3Phase4Boundary.Unexpected,
                default);
        }
    }

    private static void ValidateCanonicalKeys(IReadOnlySet<string> explicitKeys)
    {
        foreach (var key in explicitKeys)
        {
            if (!AllowedCanonicalKeys.Contains(key))
                throw ArgumentFailure();
        }

        foreach (var key in RequiredExplicitKeys)
        {
            if (!explicitKeys.Contains(key))
                throw ArgumentFailure();
        }
    }

    private static void ValidateRequiredSettings(
        NpgsqlConnectionStringBuilder supplied,
        IReadOnlySet<string> explicitKeys)
    {
        var host = supplied.Host;
        if (!IsAcceptedSingleHost(host)
            || HasEmbeddedPort(host!)
            || supplied.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(supplied.Database)
            || string.IsNullOrWhiteSpace(supplied.Username)
            || string.IsNullOrWhiteSpace(supplied.Password)
            || !string.Equals(supplied.ApplicationName, ApplicationName, StringComparison.Ordinal)
            || !string.Equals(supplied.ClientEncoding, RequiredClientEncoding, StringComparison.Ordinal)
            || !string.Equals(supplied.Timezone, TimeZoneInfo.Local.Id, StringComparison.Ordinal)
            || supplied.SslMode != SslMode.Disable
            || supplied.SslNegotiation != SslNegotiation.Postgres
            || supplied.GssEncryptionMode != GssEncryptionMode.Disable
            || !string.Equals(supplied.RequireAuth, RequiredAuthentication, StringComparison.Ordinal)
            || supplied.ChannelBinding != ChannelBinding.Disable
            || supplied.PersistSecurityInfo
            || supplied.LogParameters
            || supplied.IncludeErrorDetail
            || supplied.IncludeFailedBatchedCommand
            || supplied.LoadBalanceHosts
            || supplied.Multiplexing
            || (explicitKeys.Contains("Pooling") && supplied.Pooling)
            || (explicitKeys.Contains("Enlist") && supplied.Enlist)
            || (supplied.TargetSessionAttributes is not null
                && !string.Equals(
                    supplied.TargetSessionAttributes,
                    "any",
                    StringComparison.Ordinal))
            || !string.IsNullOrEmpty(supplied.Options)
            || (explicitKeys.Contains("Timeout")
                && supplied.Timeout != ConnectionTimeoutSeconds)
            || (explicitKeys.Contains("Command Timeout")
                && supplied.CommandTimeout != CommandTimeoutSeconds)
            || (explicitKeys.Contains("Cancellation Timeout")
                && supplied.CancellationTimeout != CancellationTimeoutMilliseconds))
        {
            throw ArgumentFailure();
        }
    }

    private static bool IsAcceptedSingleHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)
            || host.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        if (host[0] == '/')
            return true;
        if (host[0] == '@')
            return host.Length > 1;

        if (host[0] == '[' || host[^1] == ']')
        {
            return host.Length > 2
                   && host[0] == '['
                   && host[^1] == ']'
                   && IPAddress.TryParse(host.AsSpan(1, host.Length - 2), out var bracketed)
                   && bracketed.AddressFamily
                       == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }

        if (IPAddress.TryParse(host, out _))
            return true;

        return IsDnsName(host);
    }

    private static bool IsDnsName(string host)
    {
        if (host.Length > 253 || host[0] == '.' || host[^1] == '.')
            return false;

        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63
                || !IsAsciiLetterOrDigit(label[0])
                || !IsAsciiLetterOrDigit(label[^1]))
            {
                return false;
            }

            foreach (var character in label)
            {
                if (!IsAsciiLetterOrDigit(character) && character != '-')
                    return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';

    private static bool HasEmbeddedPort(string host)
    {
        if (host[0] is '/' or '@')
            return false;

        var span = host.AsSpan();
        var portSeparator = span.LastIndexOf(':');
        if (portSeparator < 0)
            return false;

        var otherColon = span[..portSeparator].LastIndexOf(':');
        var ipv6End = span.LastIndexOf(']');
        return otherColon < 0
               || (portSeparator > ipv6End && otherColon < ipv6End);
    }

    private static bool IsCallerSettingFailure(Exception raw) =>
        raw is ArgumentException
            or FormatException
            or NotSupportedException
            or OverflowException;

    private static TransferV3Phase4Exception ArgumentFailure() =>
        TransferV3Phase4Exception.Create(
            new ArgumentException(),
            TransferV3Phase4Boundary.Argument);

    private static TransferV3Phase4Exception CleanupFailure() =>
        TransferV3Phase4Exception.Create(
            new InvalidOperationException(),
            TransferV3Phase4Boundary.Cleanup);

    private static TransferV3Phase4Exception UnexpectedFailure() =>
        TransferV3Phase4Exception.Create(
            new InvalidOperationException(),
            TransferV3Phase4Boundary.Unexpected);

    private static TransferV3Phase4Exception AssertCleanupFailure(Exception failure) =>
        failure is TransferV3Phase4Exception sanitized
            && string.Equals(
                sanitized.Code,
                "phase4-cleanup",
                StringComparison.Ordinal)
            ? sanitized
            : CleanupFailure();

    private enum DescriptorLifecycleState
    {
        Ready,
        Disposing,
        Disposed,
        DisposeFailed,
    }
}
