namespace NzbWebDAV.Database.Models;

public class RcloneInvalidationItem
{
    public Guid Id { get; init; }
    public string Path { get; init; } = null!;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
