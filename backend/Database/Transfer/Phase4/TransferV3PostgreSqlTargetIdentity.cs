using System.Net;

namespace NzbWebDAV.Database.Transfer;

internal sealed record TransferV3PostgreSqlTargetIdentity(
    string SystemIdentifier,
    DateTimeOffset PostmasterStartTimeUtc,
    string DatabaseName,
    uint DatabaseOid,
    string SchemaName,
    uint SchemaOid,
    string RoleName,
    uint RoleOid,
    string ServerVersion,
    int ServerVersionNumber,
    bool IsInRecovery,
    bool DefaultTransactionReadOnly,
    bool TransactionReadOnly,
    string? ServerAddress,
    int? ServerPort);

internal readonly struct TransferV3PostgreSqlServerSettingsProjection
{
    internal TransferV3PostgreSqlServerSettingsProjection(
        string? logMinMessages,
        string? logMinErrorStatement,
        string? logErrorVerbosity,
        string? logStatement,
        string? logDuration,
        string? logMinDurationStatement,
        string? logMinDurationSample,
        string? logTransactionSampleRate,
        string? logParameterMaxLength,
        string? logParameterMaxLengthOnError,
        string? debugPrintParse,
        string? debugPrintRewritten,
        string? debugPrintPlan,
        string? sharedPreloadLibraries,
        string? sessionPreloadLibraries,
        string? localPreloadLibraries,
        string? logDestination,
        string? loggingCollector,
        string? fsync,
        string? fullPageWrites,
        string? synchronousCommit,
        string? clientEncoding,
        string? dateStyle,
        string? sessionReplicationRole,
        uint? temporarySchemaOid,
        string? defaultTransactionReadOnly,
        string? transactionReadOnly,
        string? timeZone,
        bool? roleIsSuperuser,
        bool? hasReadAllSettings)
    {
        LogMinMessages = logMinMessages;
        LogMinErrorStatement = logMinErrorStatement;
        LogErrorVerbosity = logErrorVerbosity;
        LogStatement = logStatement;
        LogDuration = logDuration;
        LogMinDurationStatement = logMinDurationStatement;
        LogMinDurationSample = logMinDurationSample;
        LogTransactionSampleRate = logTransactionSampleRate;
        LogParameterMaxLength = logParameterMaxLength;
        LogParameterMaxLengthOnError = logParameterMaxLengthOnError;
        DebugPrintParse = debugPrintParse;
        DebugPrintRewritten = debugPrintRewritten;
        DebugPrintPlan = debugPrintPlan;
        SharedPreloadLibraries = sharedPreloadLibraries;
        SessionPreloadLibraries = sessionPreloadLibraries;
        LocalPreloadLibraries = localPreloadLibraries;
        LogDestination = logDestination;
        LoggingCollector = loggingCollector;
        Fsync = fsync;
        FullPageWrites = fullPageWrites;
        SynchronousCommit = synchronousCommit;
        ClientEncoding = clientEncoding;
        DateStyle = dateStyle;
        SessionReplicationRole = sessionReplicationRole;
        TemporarySchemaOid = temporarySchemaOid;
        DefaultTransactionReadOnly = defaultTransactionReadOnly;
        TransactionReadOnly = transactionReadOnly;
        TimeZone = timeZone;
        RoleIsSuperuser = roleIsSuperuser;
        HasReadAllSettings = hasReadAllSettings;
    }

    internal string? LogMinMessages { get; }

    internal string? LogMinErrorStatement { get; }

    internal string? LogErrorVerbosity { get; }

    internal string? LogStatement { get; }

    internal string? LogDuration { get; }

    internal string? LogMinDurationStatement { get; }

    internal string? LogMinDurationSample { get; }

    internal string? LogTransactionSampleRate { get; }

    internal string? LogParameterMaxLength { get; }

    internal string? LogParameterMaxLengthOnError { get; }

    internal string? DebugPrintParse { get; }

    internal string? DebugPrintRewritten { get; }

    internal string? DebugPrintPlan { get; }

    internal string? SharedPreloadLibraries { get; }

    internal string? SessionPreloadLibraries { get; }

    internal string? LocalPreloadLibraries { get; }

    internal string? LogDestination { get; }

    internal string? LoggingCollector { get; }

    internal string? Fsync { get; }

    internal string? FullPageWrites { get; }

    internal string? SynchronousCommit { get; }

    internal string? ClientEncoding { get; }

    internal string? DateStyle { get; }

    internal string? SessionReplicationRole { get; }

    internal uint? TemporarySchemaOid { get; }

    internal string? DefaultTransactionReadOnly { get; }

    internal string? TransactionReadOnly { get; }

    internal string? TimeZone { get; }

    internal bool? RoleIsSuperuser { get; }

    internal bool? HasReadAllSettings { get; }
}

internal readonly struct TransferV3PostgreSqlIdentityProjection
{
    internal TransferV3PostgreSqlIdentityProjection(
        string? systemIdentifier,
        DateTimeOffset? postmasterStartTimeUtc,
        string? databaseName,
        uint? databaseOid,
        string? schemaName,
        uint? schemaOid,
        string? roleName,
        uint? roleOid,
        string? serverVersion,
        int? serverVersionNumber,
        bool? isInRecovery,
        IPAddress? serverAddress,
        int? serverPort)
    {
        SystemIdentifier = systemIdentifier;
        PostmasterStartTimeUtc = postmasterStartTimeUtc;
        DatabaseName = databaseName;
        DatabaseOid = databaseOid;
        SchemaName = schemaName;
        SchemaOid = schemaOid;
        RoleName = roleName;
        RoleOid = roleOid;
        ServerVersion = serverVersion;
        ServerVersionNumber = serverVersionNumber;
        IsInRecovery = isInRecovery;
        ServerAddress = serverAddress;
        ServerPort = serverPort;
    }

    internal string? SystemIdentifier { get; }

    internal DateTimeOffset? PostmasterStartTimeUtc { get; }

    internal string? DatabaseName { get; }

    internal uint? DatabaseOid { get; }

    internal string? SchemaName { get; }

    internal uint? SchemaOid { get; }

    internal string? RoleName { get; }

    internal uint? RoleOid { get; }

    internal string? ServerVersion { get; }

    internal int? ServerVersionNumber { get; }

    internal bool? IsInRecovery { get; }

    internal IPAddress? ServerAddress { get; }

    internal int? ServerPort { get; }
}
