namespace NzbWebDAV.Api.Controllers.TestRcloneConnection;

public class TestRcloneConnectionResponse : BaseApiResponse
{
    public bool Connected { get; set; }
    public string? ErrorCategory { get; set; }
    public int? ResponseStatusCode { get; set; }
}
