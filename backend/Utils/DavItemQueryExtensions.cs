using System.Linq.Expressions;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Utils;

public static class DavItemQueryExtensions
{
    public static IQueryable<DavItem> WhereVideoFiles(this IQueryable<DavItem> query)
    {
        var item = Expression.Parameter(typeof(DavItem), "item");
        var name = Expression.Property(item, nameof(DavItem.Name));
        var lowerName = Expression.Call(name, nameof(string.ToLower), Type.EmptyTypes);
        Expression body = Expression.Constant(false);

        foreach (var extension in FilenameUtil.VideoFileExtensions)
        {
            var endsWith = Expression.Call(
                lowerName,
                nameof(string.EndsWith),
                Type.EmptyTypes,
                Expression.Constant(extension));
            body = Expression.OrElse(body, endsWith);
        }

        return query.Where(Expression.Lambda<Func<DavItem, bool>>(body, item));
    }
}
