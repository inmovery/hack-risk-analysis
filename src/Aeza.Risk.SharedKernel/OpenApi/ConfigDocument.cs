using System.Reflection;
using System.Text;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Aeza.Risk.SharedKernel.OpenApi;

/// <summary>Общие сведения документа OpenAPI: название, версия сборки, описание и версия шаблона сервиса</summary>
public class ConfigDocument : IOpenApiDocumentTransformer
{
    /// <summary>Название API в документе</summary>
    public const string Title = "Aeza Risk Analyzer API";

    /// <summary>Описание API (аутентификация, единицы денег, смысл score)</summary>
    public const string Description =
        "Анализатор рисков оттока: рейтинг клиентов, причины, денежная оценка, настройки модели, тестовые данные и поток событий источника, ручная выгрузка.\n\n" +
        "Все операции с данными требуют входа. В Swagger: раскройте POST /api/v1/auth/login → Try it out → логин/пароль → Execute; " +
        "дальше cookie-сессия aeza.session и заголовок X-CSRF-TOKEN (из cookie XSRF-TOKEN) подставляются автоматически. " +
        "Деньги — целые еврокопейки (100 = €1). Score — индекс приоритета 0–100, не вероятность. " +
        "Бизнес-метрики Prometheus — GET /metrics: без авторизации, вне этого документа (OpenTelemetry).";

    /// <inheritdoc />
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info ??= new OpenApiInfo();
        document.Info.Title = Title;
        document.Info.Version = Environment.GetEnvironmentVariable("BUILD_VERSION") is { Length: > 0 } build ? build : "1.0.0";

        var description = new StringBuilder(Description);
        var templateVersion = typeof(ConfigDocument).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "TemplateVersion")?.Value;
        if (!string.IsNullOrEmpty(templateVersion))
            description.Append("\n\nВерсия шаблона сервиса: ").Append(templateVersion);
        document.Info.Description = description.ToString();

        // Относительные URL: спецификация одинакова за прокси, в контейнере и в тестах
        document.Servers?.Clear();
        return Task.CompletedTask;
    }
}
