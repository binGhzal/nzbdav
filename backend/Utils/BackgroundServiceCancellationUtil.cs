namespace NzbWebDAV.Utils;

public static class BackgroundServiceCancellationUtil
{
    public static bool IsExpectedCancellation(Exception exception, CancellationToken stoppingToken)
    {
        return exception is OperationCanceledException
               && (stoppingToken.IsCancellationRequested || SigtermUtil.IsSigtermTriggered());
    }
}
