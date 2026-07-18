using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NzbWebDAV.Tests.TestDoubles;

public sealed class CommandCaptureInterceptor : DbCommandInterceptor
{
    private readonly Lock _lock = new();
    private readonly List<CapturedCommand> _commands = [];

    public IReadOnlyList<CapturedCommand> Commands
    {
        get
        {
            lock (_lock)
                return _commands.ToArray();
        }
    }

    public void Clear()
    {
        lock (_lock)
            _commands.Clear();
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Capture(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Capture(command);
        return ValueTask.FromResult(result);
    }

    private void Capture(DbCommand command)
    {
        var parameters = command.Parameters
            .Cast<DbParameter>()
            .ToDictionary(x => x.ParameterName, x => x.Value);
        lock (_lock)
            _commands.Add(new CapturedCommand(command.CommandText, parameters));
    }
}

public sealed record CapturedCommand(
    string CommandText,
    IReadOnlyDictionary<string, object?> Parameters);
