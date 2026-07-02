namespace NzbWebDAV.Database.Models;

public class RepairBrokenFile
{
    public Guid Id { get; set; }
    public Guid RepairRunId { get; set; }
    public Guid DavItemId { get; set; }
    public string Path { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool Cleared { get; set; }
}
