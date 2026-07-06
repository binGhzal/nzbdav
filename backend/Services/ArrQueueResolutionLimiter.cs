using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public sealed class ArrQueueResolutionLimiter
{
    public const int MaxActionsPerClientRun = 5;
    public static readonly TimeSpan DefaultStartupGrace = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultActionCooldown = TimeSpan.FromHours(6);

    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _startupGrace;
    private readonly TimeSpan _actionCooldown;
    private readonly DateTimeOffset _startedAt;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastActionByKey = new(StringComparer.Ordinal);

    public ArrQueueResolutionLimiter(
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? startupGrace = null,
        TimeSpan? actionCooldown = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _startupGrace = startupGrace ?? DefaultStartupGrace;
        _actionCooldown = actionCooldown ?? DefaultActionCooldown;
        _startedAt = _utcNow();
    }

    public bool IsStartupGraceActive => _utcNow() - _startedAt < _startupGrace;

    public bool TryAcquire(string instanceKey, string queueItemKey, ArrConfig.QueueAction action)
    {
        if (action == ArrConfig.QueueAction.DoNothing) return false;
        if (IsStartupGraceActive) return false;

        var now = _utcNow();
        var key = $"{instanceKey}|{queueItemKey}|{action}";
        lock (_lock)
        {
            if (_lastActionByKey.TryGetValue(key, out var lastActionAt)
                && now - lastActionAt < _actionCooldown)
            {
                return false;
            }

            _lastActionByKey[key] = now;
            return true;
        }
    }
}
