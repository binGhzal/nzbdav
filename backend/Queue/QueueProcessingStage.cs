namespace NzbWebDAV.Queue;

public enum QueueProcessingStage
{
    Downloading,
    WaitingForVerify,
    Verifying,
    Moving
}
