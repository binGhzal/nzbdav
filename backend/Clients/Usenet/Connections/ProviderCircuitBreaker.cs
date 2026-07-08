using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Tracks consecutive connection failures for an NNTP provider and temporarily
/// disables it when a failure threshold is reached, preventing a single
/// misbehaving provider from blocking the entire download pipeline.
/// <para>
/// After tripping, the provider enters a cooldown period during which it is
/// skipped. When the cooldown expires, a single probe attempt is allowed.
/// If the probe succeeds, the breaker resets. If it fails, the cooldown
/// doubles (up to a cap) and the breaker re-trips.
/// </para>
/// </summary>
public class ProviderCircuitBreaker
{
    private const int FailureThreshold = 3;
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);

    private readonly string _providerName;
    private readonly object _lock = new();

    private int _consecutiveFailures;
    private long _trippedUntilMs;
    private int _probeInFlight;
    private int _hardTripped;
    private long _lastSuccessUnixMs;
    private long _lastFailureUnixMs;
    private string? _lastFailureKind;
    private TimeSpan _currentCooldown = InitialCooldown;

    public ProviderCircuitBreaker(string providerName)
    {
        _providerName = providerName;
    }

    public bool IsTripped
    {
        get
        {
            var trippedUntil = Volatile.Read(ref _trippedUntilMs);
            if (trippedUntil == 0) return false;
            return Environment.TickCount64 < trippedUntil;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_consecutiveFailures > 0 || _trippedUntilMs > 0)
                Log.Information("Provider {Provider} recovered — circuit breaker reset.", _providerName);

            _consecutiveFailures = 0;
            _trippedUntilMs = 0;
            _probeInFlight = 0;
            _hardTripped = 0;
            _lastSuccessUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _currentCooldown = InitialCooldown;
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            _probeInFlight = 0;
            _hardTripped = 0;
            _lastFailureUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastFailureKind = "retryable";

            if (_consecutiveFailures < FailureThreshold) return;

            var now = Environment.TickCount64;
            var alreadyTripped = _trippedUntilMs != 0 && now < _trippedUntilMs;
            _trippedUntilMs = now + (long)_currentCooldown.TotalMilliseconds;
            if (!alreadyTripped)
            {
                Log.Warning(
                    "Provider {Provider} tripped after {Failures} consecutive failures. " +
                    "Skipping for {Cooldown}s.",
                    _providerName, _consecutiveFailures, _currentCooldown.TotalSeconds);
            }

            _currentCooldown = TimeSpan.FromMilliseconds(
                Math.Min(_currentCooldown.TotalMilliseconds * 2, MaxCooldown.TotalMilliseconds));
        }
    }

    public void RecordHardFailure()
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;
            var alreadyTripped = _trippedUntilMs != 0 && now < _trippedUntilMs;
            _consecutiveFailures = FailureThreshold;
            _probeInFlight = 0;
            _hardTripped = 1;
            _lastFailureUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastFailureKind = "auth_or_config";
            _trippedUntilMs = now + (long)MaxCooldown.TotalMilliseconds;
            _currentCooldown = MaxCooldown;
            if (!alreadyTripped)
            {
                Log.Warning(
                    "Provider {Provider} tripped after a non-retryable connection failure. " +
                    "Skipping for {Cooldown}s.",
                    _providerName, MaxCooldown.TotalSeconds);
            }
        }
    }

    public AttemptLease? TryAcquireAttempt()
    {
        lock (_lock)
        {
            var trippedUntil = _trippedUntilMs;
            if (trippedUntil == 0) return new AttemptLease(null);

            var now = Environment.TickCount64;
            if (now < trippedUntil) return null;
            if (_probeInFlight != 0) return null;

            _probeInFlight = 1;
            return new AttemptLease(this);
        }
    }

    public ProviderCircuitBreakerSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var nowMs = Environment.TickCount64;
            var trippedUntil = _trippedUntilMs;
            var inCooldown = trippedUntil != 0 && nowMs < trippedUntil;
            var state = inCooldown
                ? (_hardTripped != 0 ? "hard_tripped" : "soft_cooldown")
                : _probeInFlight != 0
                    ? "probe"
                    : _consecutiveFailures >= FailureThreshold
                        ? "cooldown_elapsed"
                    : "healthy";

            return new ProviderCircuitBreakerSnapshot(
                ProviderName: _providerName,
                ConsecutiveFailures: _consecutiveFailures,
                CircuitState: state,
                CooldownUntil: inCooldown
                    ? DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(0, trippedUntil - nowMs))
                    : null,
                LastSuccessAt: FromUnixMilliseconds(_lastSuccessUnixMs),
                LastFailureAt: FromUnixMilliseconds(_lastFailureUnixMs),
                LastFailureKind: _lastFailureKind,
                ProbeInFlight: _probeInFlight != 0);
        }
    }

    private static DateTimeOffset? FromUnixMilliseconds(long value)
    {
        return value <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    private void ReleaseProbe()
    {
        lock (_lock)
        {
            _probeInFlight = 0;
        }
    }

    public sealed class AttemptLease : IDisposable
    {
        private readonly ProviderCircuitBreaker? _owner;
        private int _released;

        internal AttemptLease(ProviderCircuitBreaker? owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_owner == null) return;
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _owner.ReleaseProbe();
        }
    }
}

public sealed record ProviderCircuitBreakerSnapshot(
    string ProviderName,
    int ConsecutiveFailures,
    string CircuitState,
    DateTimeOffset? CooldownUntil,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastFailureKind,
    bool ProbeInFlight);
