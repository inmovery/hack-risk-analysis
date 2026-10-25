using JetBrains.Annotations;

namespace Aeza.Risk.Web.Configuration.Swagger;

/// <summary>Параметры документации</summary>
[PublicAPI]
public class AppSwaggerOptions
{
    /// <summary>Признак активации Swagger UI и спецификации</summary>
    public bool UseSwagger { get; set; } = true;
}
