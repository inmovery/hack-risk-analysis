using System.Text.Json.Serialization.Metadata;

namespace Aeza.Risk.SharedKernel.OpenApi;

/// <summary>Идентификаторы схем OpenAPI вида «Пространство.Тип»: исключают коллизии одноимённых DTO разных срезов</summary>
public static class SchemaIdsHelper
{
    /// <summary>Возвращает идентификатор схемы</summary>
    public static string? GetSchemaId(this JsonTypeInfo jsonType)
        => BuildSchemaId(jsonType.Type);

    private static string? BuildSchemaId(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return BuildSchemaId(underlying);

        if (type.IsEnum)
            return GetNamespacedName(type);

        if (IsSystemType(type))
            return null;

        if (!type.IsGenericType)
            return GetNamespacedName(type);

        var name = GetShortName(type.Name);
        var arguments = type.GetGenericArguments()
            .Select(BuildArgumentId)
            .Where(arg => !string.IsNullOrEmpty(arg))
            .ToArray();

        return arguments.Length == 0
            ? GetNamespacedName(type, name)
            : $"{GetNamespacedName(type, name)}_{string.Join("_", arguments)}";
    }

    private static string BuildArgumentId(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            return BuildArgumentId(underlying);

        if (type.IsArray)
            return $"{BuildArgumentId(type.GetElementType()!)}Array";

        if (type.IsEnum || !type.IsGenericType)
            return type.Name;

        var name = GetShortName(type.Name);
        var arguments = type.GetGenericArguments()
            .Select(BuildArgumentId)
            .Where(arg => !string.IsNullOrEmpty(arg))
            .ToArray();

        return arguments.Length == 0 ? name : $"{name}_{string.Join("_", arguments)}";
    }

    private static string GetShortName(string typeName)
    {
        var tick = typeName.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? typeName[..tick] : typeName;
    }

    private static string GetNamespacedName(Type type, string? name = null)
    {
        var typeName = name ?? type.Name;
        return type.Namespace is null
            ? typeName
            : $"{type.Namespace.Split('.').Last()}.{typeName}";
    }

    private static bool IsSystemType(Type type)
        => type.Namespace is null
           || type.Namespace == "System"
           || type.Namespace.StartsWith("System.", StringComparison.Ordinal);
}
