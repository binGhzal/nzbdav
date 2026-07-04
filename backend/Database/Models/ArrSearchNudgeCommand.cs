namespace NzbWebDAV.Database.Models;

public class ArrSearchNudgeCommand
{
    public Guid Id { get; set; }
    public string ArrApp { get; set; } = "";
    public string InstanceKey { get; set; } = "";
    public string InstanceHost { get; set; } = "";
    public string CommandName { get; set; } = "";
    public int? CommandId { get; set; }
    public string TargetsJson { get; set; } = "[]";
    public string Mode { get; set; } = "report";
    public string Status { get; set; } = "planned";
    public string CooldownKey { get; set; } = "";
    public int Score { get; set; }
    public string ReasonsJson { get; set; } = "[]";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset NextAllowedAt { get; set; }
}
