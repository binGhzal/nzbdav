using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NzbWebDAV.Database.Interceptors;

public class SqliteForeignKeyEnabler : DbConnectionInterceptor
{
    private static readonly string[] Pragmas =
    [
        "PRAGMA foreign_keys = ON;",
        "PRAGMA journal_mode = WAL;",
        "PRAGMA synchronous = NORMAL;",
        "PRAGMA busy_timeout = 30000;"
    ];

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        foreach (var pragma in Pragmas)
        {
            using var command = connection.CreateCommand();
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }
    }
}
