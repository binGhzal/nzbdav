using System.Text.Json.Serialization;
using NzbWebDAV.Api.SabControllers;

namespace NzbWebDAV.Api.Controllers.Mount;

public sealed class MountStatusResponse
{
    [JsonPropertyName("mount")]
    public required MountDiagnosticStatus Mount { get; init; }
}
