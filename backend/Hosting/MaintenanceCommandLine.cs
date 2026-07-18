namespace NzbWebDAV.Hosting;

internal enum MaintenanceCommandKind
{
    None,
    DatabaseMigration,
    ExportJson,
    ImportJson,
    ExportV3Unavailable,
    ImportV3Unavailable,
}

internal sealed record MaintenanceCommand(
    MaintenanceCommandKind Kind,
    string? Argument = null,
    bool Replace = false,
    TransferV3ImportAuthorization? ImportAuthorization = null);

internal sealed class TransferV3ImportAuthorization
{
    private TransferV3ImportAuthorization()
    {
    }

    internal static MaintenanceCommand ParseExactImportV3(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 2 || !MaintenanceCommandLine.IsArgument(arguments[1]))
            throw MaintenanceCommandLine.InvalidArguments();
        return new MaintenanceCommand(
            MaintenanceCommandKind.ImportV3Unavailable,
            arguments[1],
            ImportAuthorization: new TransferV3ImportAuthorization());
    }
}

internal static class MaintenanceCommandLine
{
    internal const string InvalidArgumentsMessage =
        "Invalid command-line arguments. Supported maintenance commands are --db-migration [target], "
        + "--db-export-json PATH, and --db-import-json PATH [--replace].";

    internal const string TransferV3UnavailableMessage =
        "Database transfer-v3 commands are unavailable in this build.";

    internal const string PostgreSqlUnavailableMessage =
        "PostgreSQL runtime is disabled until the provider-native migration and transfer gates pass. "
        + "Use NZBDAV_DATABASE_PROVIDER=sqlite.";

    internal static MaintenanceCommand Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
            return new MaintenanceCommand(MaintenanceCommandKind.None);

        return arguments[0] switch
        {
            "--db-migration" => ParseMigration(arguments),
            "--db-export-json" => ParseSinglePath(arguments, MaintenanceCommandKind.ExportJson),
            "--db-import-json" => ParseImportJson(arguments),
            "--db-export-v3" => ParseSinglePath(arguments, MaintenanceCommandKind.ExportV3Unavailable),
            "--db-import-v3" => TransferV3ImportAuthorization.ParseExactImportV3(arguments),
            _ => throw InvalidArguments(),
        };
    }

    private static MaintenanceCommand ParseMigration(IReadOnlyList<string> arguments)
    {
        return arguments.Count switch
        {
            1 => new MaintenanceCommand(MaintenanceCommandKind.DatabaseMigration),
            2 when IsArgument(arguments[1]) =>
                new MaintenanceCommand(MaintenanceCommandKind.DatabaseMigration, arguments[1]),
            _ => throw InvalidArguments(),
        };
    }

    private static MaintenanceCommand ParseSinglePath(
        IReadOnlyList<string> arguments,
        MaintenanceCommandKind kind)
    {
        if (arguments.Count != 2 || !IsArgument(arguments[1]))
            throw InvalidArguments();
        return new MaintenanceCommand(kind, arguments[1]);
    }

    private static MaintenanceCommand ParseImportJson(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 2 && IsArgument(arguments[1]))
            return new MaintenanceCommand(MaintenanceCommandKind.ImportJson, arguments[1]);
        if (arguments.Count == 3
            && IsArgument(arguments[1])
            && string.Equals(arguments[2], "--replace", StringComparison.Ordinal))
            return new MaintenanceCommand(MaintenanceCommandKind.ImportJson, arguments[1], Replace: true);
        throw InvalidArguments();
    }

    internal static bool IsArgument(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value[0] != '-';

    internal static ArgumentException InvalidArguments() => new(InvalidArgumentsMessage);
}
