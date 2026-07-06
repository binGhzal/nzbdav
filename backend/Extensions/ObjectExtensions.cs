using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace NzbWebDAV.Extensions;

public static class ObjectExtensions
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private const BindingFlags BindingAttr = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo> PropertyCache = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), FieldInfo> FieldCache = new();

    public static object? GetReflectionProperty(this object obj, string propertyName)
    {
        var type = obj.GetType();
        var key = (type, propertyName);
        if (!PropertyCache.TryGetValue(key, out var prop))
        {
            prop = type.GetProperty(propertyName, BindingAttr)!;
            if (prop == null) return null;
            PropertyCache[key] = prop;
        }

        return prop?.GetValue(obj);
    }

    public static object? GetReflectionField(this object obj, string fieldName)
    {
        var type = obj.GetType();
        var key = (type, fieldName);
        if (!FieldCache.TryGetValue(key, out var prop))
        {
            prop = type.GetField(fieldName, BindingAttr)!;
            if (prop == null) return null;
            FieldCache[key] = prop;
        }

        return prop?.GetValue(obj);
    }

    public static string ToJson(this object obj)
    {
        return JsonSerializer.Serialize(obj);
    }

    public static string ToIndentedJson(this object obj)
    {
        return JsonSerializer.Serialize(obj, Indented);
    }
}
