using Microsoft.AspNetCore.Http;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueRequest
{
    public int Start { get; init; } = 0;
    public int Limit { get; init; } = int.MaxValue;
    public string? Category { get; init; }
    public HashSet<Guid> NzoIds { get; init; } = [];
    public string? Search { get; init; }
    public HashSet<QueuePriorityFilter> Priorities { get; init; } = [];
    public HashSet<string> Statuses { get; init; } = [];
    public QueueSortField SortField { get; init; } = QueueSortField.Priority;
    public bool SortDescending { get; init; } = true;
    public CancellationToken CancellationToken { get; init; }


    public GetQueueRequest(HttpContext context)
    {
        var startParam = context.GetRequestParam("start");
        var limitParam = context.GetRequestParam("limit");
        var nzoIdsParam = context.GetRequestParam("nzo_ids");
        Category = context.GetRequestParam("category") ?? context.GetRequestParam("cat");
        Search = context.GetRequestParam("search");
        Priorities = ParsePriorities(context.GetRequestParam("priority"));
        Statuses = ParseStatuses(context.GetRequestParam("status"));
        SortField = ParseSortField(context.GetRequestParam("sort"));
        SortDescending = ParseSortDirection(context.GetRequestParam("order"));
        CancellationToken = context.RequestAborted;

        if (startParam is not null)
        {
            var isValidStartParam = int.TryParse(startParam, out int start);
            if (!isValidStartParam) throw new BadHttpRequestException("Invalid start parameter");
            Start = Math.Max(0, start);
        }

        if (limitParam is not null)
        {
            var isValidLimit = int.TryParse(limitParam, out int limit);
            if (!isValidLimit) throw new BadHttpRequestException("Invalid limit parameter");
            Limit = Math.Max(0, limit);
        }

        if (nzoIdsParam is not null)
        {
            NzoIds = nzoIdsParam
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Guid.Parse)
                .ToHashSet();
        }
    }

    private static HashSet<QueuePriorityFilter> ParsePriorities(string? priorityParam)
    {
        if (string.IsNullOrWhiteSpace(priorityParam)) return [];

        return priorityParam
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x switch
            {
                "-2" or "Paused" => QueuePriorityFilter.Paused,
                "-1" or "Low" => QueuePriorityFilter.Low,
                "0" or "Normal" => QueuePriorityFilter.Normal,
                "1" or "High" => QueuePriorityFilter.High,
                "2" or "Force" => QueuePriorityFilter.Force,
                _ => throw new BadHttpRequestException("Invalid priority parameter")
            })
            .ToHashSet();
    }

    private static HashSet<string> ParseStatuses(string? statusParam)
    {
        if (string.IsNullOrWhiteSpace(statusParam)) return [];

        return statusParam
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .ToHashSet();
    }

    private static QueueSortField ParseSortField(string? sortParam)
    {
        if (string.IsNullOrWhiteSpace(sortParam)) return QueueSortField.Priority;

        return sortParam.Trim().ToLowerInvariant() switch
        {
            "priority" => QueueSortField.Priority,
            "name" or "filename" => QueueSortField.Name,
            "category" or "cat" => QueueSortField.Category,
            "status" => QueueSortField.Status,
            "size" or "mb" => QueueSortField.Size,
            "created" or "created_at" or "age" => QueueSortField.CreatedAt,
            _ => throw new BadHttpRequestException("Invalid sort parameter")
        };
    }

    private static bool ParseSortDirection(string? orderParam)
    {
        if (string.IsNullOrWhiteSpace(orderParam)) return true;

        return orderParam.Trim().ToLowerInvariant() switch
        {
            "desc" or "descending" => true,
            "asc" or "ascending" => false,
            _ => throw new BadHttpRequestException("Invalid order parameter")
        };
    }

    public enum QueuePriorityFilter
    {
        Paused = QueueItem.PriorityOption.Paused,
        Low = QueueItem.PriorityOption.Low,
        Normal = QueueItem.PriorityOption.Normal,
        High = QueueItem.PriorityOption.High,
        Force = QueueItem.PriorityOption.Force
    }

    public enum QueueSortField
    {
        Priority,
        Name,
        Category,
        Status,
        Size,
        CreatedAt
    }
}
