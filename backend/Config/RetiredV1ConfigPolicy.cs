namespace NzbWebDAV.Config;

internal static class RetiredV1ConfigPolicy
{
    internal const string RetiredConfigMessage =
        "STRM import configuration is not supported by the symlink-only V1 contract.";

    internal static bool IsRetired(string? configName) =>
        configName is "api.strm-key"
            or "api.import-strategy"
            or "api.completed-downloads-dir"
            or "general.base-url";
}
