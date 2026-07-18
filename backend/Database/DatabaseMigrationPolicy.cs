namespace NzbWebDAV.Database;

internal static class DatabaseMigrationPolicy
{
    internal const string SqliteHistoryTableName = "__EFMigrationsHistory";
    internal const string PostgreSqlHistoryTableName = "__EFMigrationsHistory_PostgreSql";
}
