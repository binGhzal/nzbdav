namespace NzbWebDAV.Database.Models;

public class RcloneInvalidationItem
{
    public static readonly Guid WholeCacheVisibilityFenceId =
        Guid.Parse("9f84795a-88de-4e1d-92a6-eec29d60ba23");
    public const string WholeCacheVisibilityFencePath = "$nzbdav:whole-cache-visibility-fence$";

    public Guid Id { get; init; }
    public string Path { get; init; } = null!;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
