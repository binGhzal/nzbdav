using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Clients.Rclone;

/// <summary>
/// Client for interacting with rclone's remote control (RC) API.
/// See https://rclone.org/rc/ for API documentation.
/// </summary>
public class RcloneClient
{
    private const int MaxTransientRcAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private static HttpClient _httpClient = new() { Timeout = RequestTimeout };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string? Host { get; private set; }
    private static string? User { get; set; }
    private static string? Pass { get; set; }
    public static string? Fs { get; private set; }
    public static bool IsRemoteControlEnabled { get; private set; } = false;
    private static int _requiresVfsVisibilityFence;
    private static long _visibilityFenceGeneration;
    private static long _wholeCacheVisibilityFenceGeneration;
    private static long _materializedWholeCacheVisibilityFenceGeneration;
    public static bool RequiresVfsVisibilityFence => Volatile.Read(ref _requiresVfsVisibilityFence) != 0;
    public static long VisibilityFenceGeneration => Interlocked.Read(ref _visibilityFenceGeneration);
    public static bool WholeCacheVisibilityFencePending
    {
        get
        {
            lock (ConfigLock)
            {
                return RequiresVfsVisibilityFence
                       && _wholeCacheVisibilityFenceGeneration == _visibilityFenceGeneration;
            }
        }
    }
    private static ConfigManager? ConfigManager { get; set; }
    private static readonly object ConfigLock = new();
    private static readonly SemaphoreSlim VisibilityTopologyGate = new(1, 1);
    private static TimeProvider _runtimeTimeProvider = TimeProvider.System;
    private static long _runtimeConfigGeneration;
    private static DateTimeOffset? _lastAttemptAt;
    private static DateTimeOffset? _lastSuccessfulConfiguredCallAt;
    private static string? _lastRuntimeError;

    public static void Initialize(ConfigManager configManager)
    {
        Initialize(configManager, TimeProvider.System);
    }

    internal static void Initialize(ConfigManager configManager, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var shouldWake = false;
        VisibilityTopologyGate.Wait();
        try
        {
            lock (ConfigLock)
            {
                var previousState = (Host, User, Pass, Fs, IsRemoteControlEnabled, RequiresVfsVisibilityFence);
                if (ConfigManager != null)
                    ConfigManager.OnConfigChanged -= OnConfigChanged;

                ConfigManager = configManager;
                var host = configManager.GetRcloneHost();
                var fs = configManager.GetRcloneFs();
                var remoteControlEnabled = configManager.IsRcloneRemoteControlEnabled();
                var fenceRequired = configManager.GetMountType() == "rclone" && remoteControlEnabled;
                Host = host;
                User = configManager.GetRcloneUser();
                Pass = configManager.GetRclonePass();
                Fs = fs;
                IsRemoteControlEnabled = remoteControlEnabled;
                AdvanceVisibilityTopologyLocked(fenceRequired, armWholeCacheFence: fenceRequired);
                _runtimeTimeProvider = timeProvider;
                ResetRuntimeEvidenceLocked();
                configManager.OnConfigChanged += OnConfigChanged;
                shouldWake = fenceRequired
                             || previousState != (Host, User, Pass, Fs, IsRemoteControlEnabled, RequiresVfsVisibilityFence);
            }
        }
        finally
        {
            VisibilityTopologyGate.Release();
        }

        if (shouldWake)
            RcloneInvalidationWakeSignal.Pulse();
    }

    private static void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs configEventArgs)
    {
        var shouldWake = false;
        var topologyChange = configEventArgs.ChangedConfig.Keys.Any(IsVisibilityTopologyConfig);
        if (topologyChange)
            VisibilityTopologyGate.Wait();
        try
        {
            lock (ConfigLock)
            {
                var activeConfigManager = ConfigManager;
                if (activeConfigManager is null || !ReferenceEquals(sender, activeConfigManager)) return;

                var changedConfig = configEventArgs.ChangedConfig;
                var previousTopology = new VisibilityTopologyIdentity(
                    RequiresVfsVisibilityFence,
                    Host,
                    Fs);
                if (changedConfig.ContainsKey("rclone.host")) Host = activeConfigManager.GetRcloneHost();
                if (changedConfig.ContainsKey("rclone.user")) User = activeConfigManager.GetRcloneUser();
                if (changedConfig.ContainsKey("rclone.pass")) Pass = activeConfigManager.GetRclonePass();
                if (changedConfig.ContainsKey("rclone.fs")) Fs = activeConfigManager.GetRcloneFs();
                if (changedConfig.ContainsKey("rclone.rc-enabled"))
                    IsRemoteControlEnabled = activeConfigManager.IsRcloneRemoteControlEnabled();
                if (topologyChange)
                {
                    var nextTopology = new VisibilityTopologyIdentity(
                        activeConfigManager.GetMountType() == "rclone" && IsRemoteControlEnabled,
                        Host,
                        Fs);
                    if (previousTopology != nextTopology)
                        AdvanceVisibilityTopologyLocked(
                            nextTopology.Required,
                            armWholeCacheFence: nextTopology.Required);
                }
                shouldWake = topologyChange || changedConfig.Keys.Any(IsRcloneRuntimeConfig);
                if (changedConfig.Keys.Any(IsRcloneRuntimeConfig))
                    ResetRuntimeEvidenceLocked();
            }

            if (shouldWake)
                RcloneInvalidationWakeSignal.Pulse();
            if (topologyChange)
                ArrImportCommandWakeSignal.Pulse();
        }
        finally
        {
            if (topologyChange)
                VisibilityTopologyGate.Release();
        }
    }

    private static bool IsVisibilityTopologyConfig(string configName)
    {
        return configName is "Mount:Type"
            or "mount.type"
            or "rclone.host"
            or "rclone.fs"
            or "rclone.rc-enabled";
    }

    private static bool IsRcloneRuntimeConfig(string configName)
    {
        return configName is "rclone.host"
            or "rclone.user"
            or "rclone.pass"
            or "rclone.fs"
            or "rclone.rc-enabled";
    }

    private static void AdvanceVisibilityTopologyLocked(bool required, bool armWholeCacheFence)
    {
        var value = required ? 1 : 0;
        Volatile.Write(ref _requiresVfsVisibilityFence, value);
        var generation = Interlocked.Increment(ref _visibilityFenceGeneration);
        _materializedWholeCacheVisibilityFenceGeneration = 0;
        _wholeCacheVisibilityFenceGeneration = armWholeCacheFence ? generation : 0;
    }

    internal static async ValueTask<VisibilityFenceTopologyLease> AcquireVisibilityFenceTopologyLeaseAsync(
        CancellationToken ct = default)
    {
        await VisibilityTopologyGate.WaitAsync(ct).ConfigureAwait(false);
        lock (ConfigLock)
        {
            var generation = _visibilityFenceGeneration;
            return new VisibilityFenceTopologyLease(
                RequiresVfsVisibilityFence,
                generation,
                _wholeCacheVisibilityFenceGeneration == generation,
                _materializedWholeCacheVisibilityFenceGeneration == generation,
                VisibilityTopologyGate);
        }
    }

    public static RcloneRuntimeSnapshot GetRuntimeSnapshot()
    {
        lock (ConfigLock)
        {
            return new RcloneRuntimeSnapshot(
                VisibilityFenceRequired: RequiresVfsVisibilityFence,
                WholeCacheVisibilityFencePending:
                    RequiresVfsVisibilityFence
                    && _wholeCacheVisibilityFenceGeneration == _visibilityFenceGeneration,
                VisibilityFenceGeneration: _visibilityFenceGeneration,
                RemoteControlEnabled: IsRemoteControlEnabled,
                HostConfigured: !string.IsNullOrWhiteSpace(Host),
                LastAttemptAt: _lastAttemptAt,
                LastSuccessfulConfiguredCallAt: _lastSuccessfulConfiguredCallAt,
                LastError: _lastRuntimeError);
        }
    }

    /// <summary>
    /// Refresh the VFS directory cache for multiple paths in a single request.
    /// </summary>
    /// <param name="paths">The paths to refresh</param>
    /// <param name="recursive">Whether to refresh recursively</param>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public static async Task<RcloneResponse> RefreshVfsPaths
    (
        IEnumerable<string> paths,
        bool recursive = false,
        CancellationToken ct = default
    )
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return new RcloneResponse { Success = true };

        var configuredCall = CaptureConfiguredCall();
        var request = new Dictionary<string, object?>();

        // Add paths using numbered keys: dir, dir2, dir3, etc.
        for (int i = 0; i < pathList.Count; i++)
        {
            var key = i == 0 ? "dir" : $"dir{i + 1}";
            request[key] = pathList[i];
        }

        if (recursive)
            request["recursive"] = true;
        if (configuredCall.Fs != null)
            request["fs"] = configuredCall.Fs;

        Log.Debug(
            "Rclone VFS refresh requested for {PathCount} path(s) (recursive={Recursive}).",
            pathList.Count,
            recursive);
        return await PostConfigured<RcloneResponse>(configuredCall, "vfs/refresh", request, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Forget (clear) VFS directory cache entries for multiple paths in a single request.
    /// </summary>
    /// <param name="paths">The paths to forget</param>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public static async Task<VfsForgetResponse> ForgetVfsPaths
    (
        IEnumerable<string> paths,
        CancellationToken ct = default
    )
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
            return new VfsForgetResponse { Success = true, Forgotten = new List<string>() };

        var configuredCall = CaptureConfiguredCall();
        var request = new Dictionary<string, object?>();

        // Add paths using numbered keys: dir, dir2, dir3, etc.
        for (int i = 0; i < pathList.Count; i++)
        {
            var key = i == 0 ? "dir" : $"dir{i + 1}";
            request[key] = pathList[i];
        }

        if (configuredCall.Fs != null)
            request["fs"] = configuredCall.Fs;

        Log.Debug("Rclone VFS forget requested for {PathCount} path(s).", pathList.Count);
        return await PostConfigured<VfsForgetResponse>(configuredCall, "vfs/forget", request, ct)
            .ConfigureAwait(false);
    }

    internal static async Task<VfsForgetResponse> ForgetWholeVfsCache(
        long expectedTopologyGeneration,
        CancellationToken ct = default)
    {
        lock (ConfigLock)
        {
            if (!RequiresVfsVisibilityFence
                || expectedTopologyGeneration != _visibilityFenceGeneration)
            {
                return CreateFailure<VfsForgetResponse>(RcloneFailureKind.ConfigurationChanged);
            }
        }

        var configuredCall = CaptureConfiguredCall();
        var request = new Dictionary<string, object?>();
        if (configuredCall.Fs is not null)
            request["fs"] = configuredCall.Fs;

        Log.Debug("Rclone whole VFS cache forget requested.");
        var result = await PostConfigured<VfsForgetResponse>(
                configuredCall,
                "vfs/forget",
                request,
                ct,
                response => response.Forgotten is { Count: 0 })
            .ConfigureAwait(false);
        if (!result.Success) return result;

        lock (ConfigLock)
        {
            return RequiresVfsVisibilityFence
                   && expectedTopologyGeneration == _visibilityFenceGeneration
                ? result
                : CreateFailure<VfsForgetResponse>(RcloneFailureKind.ConfigurationChanged);
        }
    }

    internal static bool TryMarkWholeCacheVisibilityFenceMaterialized(long expectedTopologyGeneration)
    {
        lock (ConfigLock)
        {
            if (!RequiresVfsVisibilityFence
                || expectedTopologyGeneration != _visibilityFenceGeneration
                || _wholeCacheVisibilityFenceGeneration != expectedTopologyGeneration)
            {
                return false;
            }

            _materializedWholeCacheVisibilityFenceGeneration = expectedTopologyGeneration;
            return true;
        }
    }

    internal static bool TryClearWholeCacheVisibilityFence(long expectedTopologyGeneration)
    {
        lock (ConfigLock)
        {
            if (!RequiresVfsVisibilityFence
                || expectedTopologyGeneration != _visibilityFenceGeneration
                || _wholeCacheVisibilityFenceGeneration != expectedTopologyGeneration)
            {
                return false;
            }

            _wholeCacheVisibilityFenceGeneration = 0;
            _materializedWholeCacheVisibilityFenceGeneration = 0;
            return true;
        }
    }

    /// <summary>
    /// Get VFS statistics including cache information.
    /// </summary>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public static async Task<VfsStatsResponse> GetVfsStats(string? fs = null, CancellationToken ct = default)
    {
        var configuredCall = CaptureConfiguredCall();
        var selectedFs = fs ?? configuredCall.Fs;
        var request = selectedFs != null ? new { fs = selectedFs } : null;
        return await PostConfigured<VfsStatsResponse>(configuredCall, "vfs/stats", request, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Get rclone version information.
    /// </summary>
    public static async Task<CoreVersionResponse> GetVersion(CancellationToken ct = default)
    {
        return await PostConfigured<CoreVersionResponse>(
                CaptureConfiguredCall(),
                "core/version",
                null,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Test connectivity - a no-operation call.
    /// </summary>
    public static async Task<RcloneResponse> NoOp(CancellationToken ct = default)
    {
        return await PostConfigured<RcloneResponse>(CaptureConfiguredCall(), "rc/noop", null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Check if the rclone RC server is reachable and authenticated.
    /// </summary>
    public static async Task<bool> IsAvailable(CancellationToken ct = default)
    {
        try
        {
            var response = await NoOp(ct).ConfigureAwait(false);
            return response.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Test connectivity to a rclone RC server with the given credentials.
    /// </summary>
    public static async Task<RcloneResponse> TestConnection
    (
        string host,
        string? user,
        string? pass,
        string? fs = null,
        CancellationToken ct = default
    )
    {
        var result = await Post<CoreVersionResponse>(host, user, pass, "core/version", null, ct)
            .ConfigureAwait(false);
        if (result.Success && string.IsNullOrWhiteSpace(result.Version))
            return CreateFailure<RcloneResponse>(RcloneFailureKind.InvalidResponse);
        if (!result.Success || string.IsNullOrWhiteSpace(fs)) return result;

        var stats = await Post<VfsStatsResponse>(
                host,
                user,
                pass,
                "vfs/stats",
                new { fs = fs.Trim() },
                ct)
            .ConfigureAwait(false);
        if (!stats.Success) return stats;
        if (stats.MetadataCache is null || stats.Options is null)
            return CreateFailure<RcloneResponse>(RcloneFailureKind.InvalidResponse);
        return stats;
    }

    private static async Task<T> Post<T>
    (
        string host,
        string? user,
        string? pass,
        string endpoint,
        object? body,
        CancellationToken ct = default
    ) where T : RcloneResponse, new()
    {
        if (!TryCreateEndpointUri(host, endpoint, out var requestUri))
            return CreateFailure<T>(RcloneFailureKind.InvalidEndpoint);

        var httpClient = Volatile.Read(ref _httpClient);
        for (var attempt = 1; attempt <= MaxTransientRcAttempts; attempt++)
        {
            try
            {
                using var request = CreateRequest(requestUri, user, pass, body);
                using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var failureKind = response.StatusCode == HttpStatusCode.Unauthorized
                        ? RcloneFailureKind.Authentication
                        : RcloneFailureKind.Http;
                    Log.Warning(
                        "Rclone RC request failed: category={FailureCategory}, status={StatusCode}.",
                        GetSafeFailureCategory(failureKind, (int)response.StatusCode),
                        response.StatusCode);

                    return CreateFailure<T>(failureKind, (int)response.StatusCode);
                }

                if (string.IsNullOrWhiteSpace(content) || content == "{}")
                {
                    return new T { Success = true };
                }

                var result = TryDeserialize<T>(content);
                if (result == null)
                {
                    Log.Warning(
                        "Rclone RC request failed: category={FailureCategory}.",
                        "rclone_rc_malformed_response");
                    return CreateFailure<T>(RcloneFailureKind.MalformedResponse);
                }

                result.Success = true;
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                Log.Warning(
                    "Rclone RC request failed: category={FailureCategory}, exception_type={ExceptionType}.",
                    "rclone_rc_timeout",
                    GetSafeExceptionType(ex));
                return CreateFailure<T>(RcloneFailureKind.Timeout);
            }
            catch (Exception ex) when (IsTransientRcConnectionFailure(ex) && attempt < MaxTransientRcAttempts)
            {
                Log.Debug(
                    "Rclone RC retry scheduled: category={FailureCategory}, exception_type={ExceptionType}, attempt={Attempt}/{MaxAttempts}.",
                    "rclone_rc_connection_failure",
                    GetSafeExceptionType(ex),
                    attempt,
                    MaxTransientRcAttempts);
                await Task.Delay(GetTransientRcRetryDelay(attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientRcConnectionFailure(ex))
            {
                Log.Warning(
                    "Rclone RC request failed: category={FailureCategory}, exception_type={ExceptionType}.",
                    "rclone_rc_connection_failure",
                    GetSafeExceptionType(ex));
                return CreateFailure<T>(RcloneFailureKind.Connection);
            }
            catch (Exception ex) when (IsInvalidEndpointFailure(ex))
            {
                Log.Warning(
                    "Rclone RC request failed: category={FailureCategory}, exception_type={ExceptionType}.",
                    "rclone_rc_invalid_endpoint",
                    GetSafeExceptionType(ex));
                return CreateFailure<T>(RcloneFailureKind.InvalidEndpoint);
            }
            catch (Exception ex)
            {
                Log.Warning(
                    "Rclone RC request failed: category={FailureCategory}, exception_type={ExceptionType}.",
                    "rclone_rc_request_failed",
                    GetSafeExceptionType(ex));
                return CreateFailure<T>(RcloneFailureKind.RequestFailed);
            }
        }

        return CreateFailure<T>(RcloneFailureKind.RequestFailed);
    }

    private static bool TryCreateEndpointUri(string host, string endpoint, out Uri requestUri)
    {
        requestUri = null!;
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (!Uri.TryCreate(
                $"{host.TrimEnd('/')}/{endpoint.TrimStart('/')}",
                UriKind.Absolute,
                out var candidate))
        {
            return false;
        }

        if (!candidate.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        requestUri = candidate;
        return true;
    }

    private static HttpRequestMessage CreateRequest(Uri requestUri, string? user, string? pass, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        if (body != null)
        {
            var jsonBody = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        else
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        AddAuthHeader(request, user, pass);
        return request;
    }

    private static TimeSpan GetTransientRcRetryDelay(int attempt)
    {
        return TimeSpan.FromMilliseconds(100 * attempt);
    }

    private static bool IsTransientRcConnectionFailure(Exception exception)
    {
        if (exception is HttpRequestException or IOException) return true;
        if (exception is System.Net.Sockets.SocketException) return true;
        return exception.InnerException is not null && IsTransientRcConnectionFailure(exception.InnerException);
    }

    private static bool IsInvalidEndpointFailure(Exception exception)
    {
        return exception is UriFormatException;
    }

    internal static string GetSafeExceptionType(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.GetType().FullName ?? exception.GetType().Name;
    }

    internal static string GetSafeFailureCategory(RcloneResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return GetSafeFailureCategory(response.FailureKind, response.ResponseStatusCode);
    }

    private static string GetSafeFailureCategory(RcloneFailureKind failureKind, int? statusCode)
    {
        return failureKind switch
        {
            RcloneFailureKind.Authentication when statusCode is >= 100 and <= 599 =>
                $"rclone_rc_authentication_failed_http_{statusCode.Value}",
            RcloneFailureKind.Authentication => "rclone_rc_authentication_failed",
            RcloneFailureKind.Http when statusCode is >= 100 and <= 599 =>
                $"rclone_rc_http_{statusCode.Value}",
            RcloneFailureKind.Http => "rclone_rc_http_error",
            RcloneFailureKind.MalformedResponse => "rclone_rc_malformed_response",
            RcloneFailureKind.Timeout => "rclone_rc_timeout",
            RcloneFailureKind.Connection => "rclone_rc_connection_failure",
            RcloneFailureKind.RemoteControlDisabled => "rclone_rc_disabled",
            RcloneFailureKind.HostNotConfigured => "rclone_rc_host_not_configured",
            RcloneFailureKind.ConfigurationChanged => "rclone_rc_configuration_changed",
            RcloneFailureKind.InvalidResponse => "rclone_rc_invalid_response",
            RcloneFailureKind.InvalidEndpoint => "rclone_rc_invalid_endpoint",
            _ => "rclone_rc_request_failed"
        };
    }

    private static T CreateFailure<T>(RcloneFailureKind failureKind, int? statusCode = null)
        where T : RcloneResponse, new()
    {
        return new T
        {
            Success = false,
            Error = GetSafeFailureMessage(failureKind, statusCode),
            FailureKind = failureKind,
            ResponseStatusCode = statusCode
        };
    }

    private static string GetSafeFailureMessage(RcloneFailureKind failureKind, int? statusCode)
    {
        return failureKind switch
        {
            RcloneFailureKind.Authentication => "Rclone RC authentication failed.",
            RcloneFailureKind.Http when statusCode is >= 100 and <= 599 =>
                $"Rclone RC returned HTTP {statusCode.Value}.",
            RcloneFailureKind.Http => "Rclone RC returned an HTTP error.",
            RcloneFailureKind.MalformedResponse => "Rclone RC returned a malformed response.",
            RcloneFailureKind.Timeout => "Rclone RC request timed out.",
            RcloneFailureKind.Connection => "Could not connect to rclone RC.",
            RcloneFailureKind.RemoteControlDisabled => "Rclone RC remote control is disabled.",
            RcloneFailureKind.HostNotConfigured => "Rclone RC host is not configured.",
            RcloneFailureKind.ConfigurationChanged => "Rclone configuration changed during request.",
            RcloneFailureKind.InvalidResponse => "Rclone RC returned an invalid response.",
            RcloneFailureKind.InvalidEndpoint => "Rclone RC endpoint is invalid.",
            _ => "Rclone RC request failed."
        };
    }

    internal static IDisposable OverrideHttpClientForTests(HttpClient replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var previous = Interlocked.Exchange(ref _httpClient, replacement);
        return new HttpClientOverride(previous, replacement);
    }

    private static ConfiguredCallSnapshot CaptureConfiguredCall()
    {
        lock (ConfigLock)
        {
            _lastAttemptAt = _runtimeTimeProvider.GetUtcNow();
            return new ConfiguredCallSnapshot(
                Host,
                User,
                Pass,
                Fs,
                IsRemoteControlEnabled,
                _runtimeConfigGeneration);
        }
    }

    private static async Task<T> PostConfigured<T>(
        ConfiguredCallSnapshot configuredCall,
        string endpoint,
        object? body,
        CancellationToken ct = default,
        Func<T, bool>? successShapeValidator = null)
        where T : RcloneResponse, new()
    {
        try
        {
            var result = !configuredCall.RemoteControlEnabled
                ? CreateFailure<T>(RcloneFailureKind.RemoteControlDisabled)
                : string.IsNullOrWhiteSpace(configuredCall.Host)
                ? CreateFailure<T>(RcloneFailureKind.HostNotConfigured)
                : await Post<T>(
                        configuredCall.Host,
                        configuredCall.User,
                        configuredCall.Pass,
                        endpoint,
                        body,
                        ct)
                    .ConfigureAwait(false);
            if (result.Success
                && successShapeValidator is not null
                && !successShapeValidator(result))
            {
                result = CreateFailure<T>(RcloneFailureKind.InvalidResponse);
            }

            if (!RecordConfiguredCallResult(
                    configuredCall.Generation,
                    result.Success,
                    result.FailureKind))
            {
                return CreateFailure<T>(RcloneFailureKind.ConfigurationChanged);
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!RecordConfiguredCallResult(
                    configuredCall.Generation,
                    success: false,
                    RcloneFailureKind.RequestFailed))
            {
                return CreateFailure<T>(RcloneFailureKind.ConfigurationChanged);
            }

            Log.Warning(
                "Configured rclone RC request failed: category={FailureCategory}, exception_type={ExceptionType}.",
                "rclone_rc_request_failed",
                GetSafeExceptionType(exception));
            return CreateFailure<T>(RcloneFailureKind.RequestFailed);
        }
    }

    private static bool RecordConfiguredCallResult(
        long generation,
        bool success,
        RcloneFailureKind failureKind)
    {
        lock (ConfigLock)
        {
            if (generation != _runtimeConfigGeneration) return false;
            if (success)
            {
                _lastSuccessfulConfiguredCallAt = _runtimeTimeProvider.GetUtcNow();
                _lastRuntimeError = null;
                return true;
            }

            _lastRuntimeError = ClassifyRuntimeError(failureKind);
            return true;
        }
    }

    private static void ResetRuntimeEvidenceLocked()
    {
        _runtimeConfigGeneration++;
        _lastAttemptAt = null;
        _lastSuccessfulConfiguredCallAt = null;
        _lastRuntimeError = null;
    }

    private static string ClassifyRuntimeError(RcloneFailureKind failureKind)
    {
        return failureKind switch
        {
            RcloneFailureKind.Authentication => "authentication failed",
            RcloneFailureKind.Timeout => "request timed out",
            RcloneFailureKind.HostNotConfigured => "host not configured",
            RcloneFailureKind.RemoteControlDisabled => "remote control disabled",
            RcloneFailureKind.MalformedResponse => "malformed response",
            RcloneFailureKind.ConfigurationChanged => "configuration changed",
            RcloneFailureKind.InvalidResponse => "invalid response",
            RcloneFailureKind.InvalidEndpoint => "invalid endpoint",
            _ => "remote-control request failed"
        };
    }

    private static T? TryDeserialize<T>(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(content, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return default;
        }
    }

    private static void AddAuthHeader(HttpRequestMessage request, string? user, string? pass)
    {
        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(pass))
            return;

        var credentials = $"{user ?? ""}:{pass ?? ""}";
        var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredentials);
    }

    private sealed record ConfiguredCallSnapshot(
        string? Host,
        string? User,
        string? Pass,
        string? Fs,
        bool RemoteControlEnabled,
        long Generation);

    private sealed record VisibilityTopologyIdentity(
        bool Required,
        string? Host,
        string? Fs);

    private sealed class HttpClientOverride(HttpClient previous, HttpClient replacement) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.CompareExchange(ref _httpClient, previous, replacement);
            replacement.Dispose();
        }
    }
}

public sealed record RcloneRuntimeSnapshot(
    bool VisibilityFenceRequired,
    bool WholeCacheVisibilityFencePending,
    long VisibilityFenceGeneration,
    bool RemoteControlEnabled,
    bool HostConfigured,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessfulConfiguredCallAt,
    string? LastError);

internal sealed class VisibilityFenceTopologyLease(
    bool required,
    long generation,
    bool wholeCacheVisibilityFencePending,
    bool wholeCacheVisibilityFenceMaterialized,
    SemaphoreSlim gate) : IAsyncDisposable
{
    private int _disposed;

    public bool Required { get; } = required;
    public long Generation { get; } = generation;
    public bool WholeCacheVisibilityFencePending { get; } = wholeCacheVisibilityFencePending;
    public bool WholeCacheVisibilityFenceMaterialized { get; } = wholeCacheVisibilityFenceMaterialized;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            gate.Release();
        return ValueTask.CompletedTask;
    }
}
