using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Clients.Rclone.Models;
using NzbWebDAV.Config;
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

    private static readonly HttpClient HttpClient = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string? Host { get; private set; }
    private static string? User { get; set; }
    private static string? Pass { get; set; }
    public static bool IsRemoteControlEnabled { get; private set; } = false;
    private static ConfigManager? ConfigManager { get; set; }
    private static readonly object ConfigLock = new();

    public static void Initialize(ConfigManager configManager)
    {
        lock (ConfigLock)
        {
            if (ConfigManager != null)
                ConfigManager.OnConfigChanged -= OnConfigChanged;

            ConfigManager = configManager;
            Host = configManager.GetRcloneHost();
            User = configManager.GetRcloneUser();
            Pass = configManager.GetRclonePass();
            IsRemoteControlEnabled = configManager.IsRcloneRemoteControlEnabled();
            configManager.OnConfigChanged += OnConfigChanged;
        }
    }

    private static void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs configEventArgs)
    {
        lock (ConfigLock)
        {
            var activeConfigManager = ConfigManager;
            if (activeConfigManager is null || !ReferenceEquals(sender, activeConfigManager)) return;

            var changedConfig = configEventArgs.ChangedConfig;
            if (changedConfig.ContainsKey("rclone.host")) Host = activeConfigManager.GetRcloneHost();
            if (changedConfig.ContainsKey("rclone.user")) User = activeConfigManager.GetRcloneUser();
            if (changedConfig.ContainsKey("rclone.pass")) Pass = activeConfigManager.GetRclonePass();
            if (changedConfig.ContainsKey("rclone.rc-enabled"))
                IsRemoteControlEnabled = activeConfigManager.IsRcloneRemoteControlEnabled();
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

        var request = new Dictionary<string, object?>();

        // Add paths using numbered keys: dir, dir2, dir3, etc.
        for (int i = 0; i < pathList.Count; i++)
        {
            var key = i == 0 ? "dir" : $"dir{i + 1}";
            request[key] = pathList[i];
        }

        if (recursive)
            request["recursive"] = true;

        Log.Debug("Rclone vfs/refresh: {0}", paths.ToIndentedJson());
        return await Post<RcloneResponse>("vfs/refresh", request, ct).ConfigureAwait(false);
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

        var request = new Dictionary<string, object?>();

        // Add paths using numbered keys: dir, dir2, dir3, etc.
        for (int i = 0; i < pathList.Count; i++)
        {
            var key = i == 0 ? "dir" : $"dir{i + 1}";
            request[key] = pathList[i];
        }

        Log.Debug("Rclone vfs/forget: {0}", paths.ToIndentedJson());
        return await Post<VfsForgetResponse>("vfs/forget", request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Get VFS statistics including cache information.
    /// </summary>
    /// <param name="fs">Optional VFS name if multiple VFS instances exist</param>
    public static async Task<VfsStatsResponse> GetVfsStats(string? fs = null, CancellationToken ct = default)
    {
        var request = fs != null ? new { fs } : null;
        return await Post<VfsStatsResponse>("vfs/stats", request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Get rclone version information.
    /// </summary>
    public static async Task<CoreVersionResponse> GetVersion(CancellationToken ct = default)
    {
        return await Post<CoreVersionResponse>("core/version", null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Test connectivity - a no-operation call.
    /// </summary>
    public static async Task<RcloneResponse> NoOp(CancellationToken ct = default)
    {
        return await Post<RcloneResponse>("rc/noop", null, ct).ConfigureAwait(false);
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
        CancellationToken ct = default
    )
    {
        var result = await Post<CoreVersionResponse>(host, user, pass, "core/version", null, ct)
            .ConfigureAwait(false);
        if (result.Success && string.IsNullOrEmpty(result.Version))
            return new RcloneResponse { Success = false, Error = "Connected but received empty version" };
        return result;
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
        var url = $"{host}/{endpoint}";
        for (var attempt = 1; attempt <= MaxTransientRcAttempts; attempt++)
        {
            using var request = CreateRequest(url, user, pass, body);
            try
            {
                using var response = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("Rclone RC request to {Endpoint} failed with status {StatusCode}: {Content}",
                        endpoint, response.StatusCode, content);

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        return new T { Success = false, Error = "Authentication failed" };
                    }

                    var errorResponse = TryDeserialize<RcloneErrorResponse>(content, out var errorParseError);
                    return new T
                    {
                        Success = false,
                        Error = errorResponse?.Error
                                ?? $"HTTP {response.StatusCode}: {FormatResponsePreview(content, errorParseError)}"
                    };
                }

                if (string.IsNullOrWhiteSpace(content) || content == "{}")
                {
                    return new T { Success = true };
                }

                var result = TryDeserialize<T>(content, out var parseError);
                if (result == null)
                {
                    Log.Warning(
                        parseError,
                        "Rclone RC request to {Endpoint} returned malformed success content: {Content}",
                        endpoint,
                        content);
                    return new T
                    {
                        Success = false,
                        Error = $"Malformed rclone RC response: {FormatResponsePreview(content, parseError)}"
                    };
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
                Log.Warning(ex, "Rclone RC request to {Endpoint} timed out", endpoint);
                return new T { Success = false, Error = "Request timed out" };
            }
            catch (Exception ex) when (IsTransientRcConnectionFailure(ex) && attempt < MaxTransientRcAttempts)
            {
                Log.Debug(
                    ex,
                    "Transient rclone RC request failure to {Endpoint} on attempt {Attempt}/{MaxAttempts}.",
                    endpoint,
                    attempt,
                    MaxTransientRcAttempts);
                await Task.Delay(GetTransientRcRetryDelay(attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientRcConnectionFailure(ex))
            {
                Log.Warning(ex, "Rclone RC request to {Endpoint} failed", endpoint);
                return new T { Success = false, Error = ex.Message };
            }
        }

        return new T { Success = false, Error = "rclone RC request failed" };
    }

    private static HttpRequestMessage CreateRequest(string url, string? user, string? pass, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
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

    private static Task<T> Post<T>(string endpoint, object? body, CancellationToken ct = default) where T : RcloneResponse, new()
        => Post<T>(Host!, User, Pass, endpoint, body, ct);

    private static T? TryDeserialize<T>(string content, out Exception? error)
    {
        try
        {
            error = null;
            return JsonSerializer.Deserialize<T>(content, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            error = ex;
            return default;
        }
    }

    private static string FormatResponsePreview(string content, Exception? parseError)
    {
        var preview = content.Length > 256 ? content[..256] : content;
        return parseError == null
            ? preview
            : $"{parseError.Message} Preview: {preview}";
    }

    private static void AddAuthHeader(HttpRequestMessage request, string? user, string? pass)
    {
        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(pass))
            return;

        var credentials = $"{user ?? ""}:{pass ?? ""}";
        var encodedCredentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedCredentials);
    }
}
