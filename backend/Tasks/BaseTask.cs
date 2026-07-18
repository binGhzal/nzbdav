using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tasks;

public abstract class BaseTask(
    WebsocketManager websocketManager,
    WebsocketTopic reportTopic,
    MaintenanceProgressReporter? progressReporter = null
)
{
    private readonly Action<Action> _debounce = DebounceUtil.CreateDebounce();

    protected CancellationToken ExecutionCancellationToken { get; private set; }

    protected abstract Task ExecuteInternal(CancellationToken cancellationToken);

    public Task Execute(CancellationToken cancellationToken = default)
    {
        ExecutionCancellationToken = cancellationToken;
        return ExecuteInternal(cancellationToken);
    }

    protected virtual void Report(string message, int? current = null, int? total = null)
    {
        progressReporter?.Invoke(new MaintenanceTaskProgress(message, current, total)).GetAwaiter().GetResult();
        _ = websocketManager.SendMessage(reportTopic, message);
    }

    protected void ReportDebounced(string message, int? current = null, int? total = null)
    {
        _debounce(() => Report(message, current, total));
    }
}
