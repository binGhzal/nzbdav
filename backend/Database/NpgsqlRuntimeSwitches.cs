using System.Runtime.CompilerServices;

namespace NzbWebDAV.Database;

internal static class NpgsqlRuntimeSwitches
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);
    }
}
