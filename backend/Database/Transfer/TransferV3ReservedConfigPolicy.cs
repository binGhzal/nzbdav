namespace NzbWebDAV.Database.Transfer;

internal static class TransferV3ReservedConfigPolicy
{
    internal const string ImportStateKey = "database.import-state";
    internal const string ReservedConfigMessage =
        "The database.import-state configuration key is reserved for transfer-v3 state management.";
    internal const string LegacySnapshotMessage =
        "Legacy database transfer v2 snapshots cannot contain transfer-v3 import state.";

    internal static bool IsReserved(string? configName) =>
        string.Equals(configName, ImportStateKey, StringComparison.Ordinal);

    internal static void ThrowIfReserved(IEnumerable<string?> configNames)
    {
        ArgumentNullException.ThrowIfNull(configNames);
        if (configNames.Any(IsReserved))
            throw new InvalidOperationException(ReservedConfigMessage);
    }
}
