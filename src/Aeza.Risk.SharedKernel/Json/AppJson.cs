using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aeza.Risk.SharedKernel.Json;

/// <summary>Единые опции сериализации хранимых jsonb-документов (сводки поколений, ревизии настроек, детали клиентов)</summary>
public static class AppJson
{
    /// <summary>camelCase, enum — snake_case строками</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Сериализует значение</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Десериализует значение (null недопустим)</summary>
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
                                                   ?? throw new JsonException($"Пустой JSON для {typeof(T).Name}");
}
