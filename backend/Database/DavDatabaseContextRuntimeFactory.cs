namespace NzbWebDAV.Database;

internal static class DavDatabaseContextRuntimeFactory
{
    public const string UnsupportedProviderMessage =
        "Unsupported database provider. Set NZBDAV_DATABASE_PROVIDER to 'sqlite' or 'postgres'.";

    public static DavDatabaseContext Create()
    {
        if (DavDatabaseContext.IsSqlite)
            return new DavDatabaseContext();
        if (DavDatabaseContext.IsPostgres)
            return new PostgreSqlDavDatabaseContext();

        throw new InvalidOperationException(UnsupportedProviderMessage);
    }
}
