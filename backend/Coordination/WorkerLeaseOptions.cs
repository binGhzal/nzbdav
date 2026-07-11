using Microsoft.Extensions.Options;

namespace NzbWebDAV.Coordination;

public sealed record WorkerLeaseOptions
{
    public const string ValidationMessage =
        "Worker lease duration and renewal interval must be positive, and renewal interval must be shorter than duration.";

    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(30);

    public static bool IsValid(WorkerLeaseOptions options) =>
        options.Duration > TimeSpan.Zero
        && options.RenewalInterval > TimeSpan.Zero
        && options.RenewalInterval < options.Duration;

    public static WorkerLeaseOptions Validate(WorkerLeaseOptions options)
    {
        if (!IsValid(options))
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(WorkerLeaseOptions),
                [ValidationMessage]);
        return options;
    }
}
