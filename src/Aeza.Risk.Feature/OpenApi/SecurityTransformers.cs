using Aeza.Risk.Core.Security;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Aeza.Risk.Feature.OpenApi;

/// <summary>Схемы безопасности документа: cookie-сессия, CSRF-заголовок, машинные Bearer-токены</summary>
internal sealed class SecuritySchemesTransformer : IOpenApiDocumentTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["cookieAuth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = "aeza.session", Description = "Cookie-сессия после POST /api/v1/auth/login.",
        };
        document.Components.SecuritySchemes["csrfHeader"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = CsrfConstants.HeaderName, Description = "CSRF-токен для изменяющих запросов.",
        };
        document.Components.SecuritySchemes["ingestToken"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "INGEST_TOKEN — только приём событий и загрузка тестовых данных (система-источник).",
        };
        return Task.CompletedTask;
    }
}

/// <summary>Требования безопасности операций по их политикам и документированные коды отказа доступа (401/403/429)</summary>
internal sealed class SecurityRequirementsTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var policies = metadata.OfType<IAuthorizeData>().Select(a => a.Policy).Where(p => p is not null).ToHashSet();
        var anonymous = metadata.OfType<IAllowAnonymous>().Any();
        var method = context.Description.HttpMethod ?? "GET";
        var writes = method is "POST" or "PUT" or "PATCH" or "DELETE";
        var doc = context.Document;

        OpenApiSecurityRequirement Req(params string[] schemes)
        {
            var r = new OpenApiSecurityRequirement();
            foreach (var s in schemes) r[new OpenApiSecuritySchemeReference(s, doc)] = [];
            return r;
        }

        operation.Security ??= [];
        if (policies.Contains(Policies.SourceWriter))
        {
            operation.Security.Add(Req("cookieAuth", "csrfHeader"));
            operation.Security.Add(Req("ingestToken"));
        }
        else if (policies.Count > 0)
            operation.Security.Add(writes ? Req("cookieAuth", "csrfHeader") : Req("cookieAuth"));
        else if (anonymous && writes)
            operation.Security.Add(Req("csrfHeader"));

        // Коды отказа доступа — в контракте, чтобы Swagger и клиенты видели их как документированные
        operation.Responses ??= new OpenApiResponses();
        void Add(string code, string description) => operation.Responses.TryAdd(code, new OpenApiResponse { Description = description });
        if (policies.Count > 0) Add("401", "Нет входа, сессия истекла или неверный токен.");
        if (policies.Count > 0 && writes) Add("403", "Недостаточно прав роли/токена или неверный X-CSRF-TOKEN.");
        else if (anonymous && writes) Add("403", "Неверный или отсутствующий X-CSRF-TOKEN.");
        if (context.Description.RelativePath == "api/v1/auth/login")
        {
            Add("401", "Неверный логин или пароль (ответ одинаков для обоих случаев) либо временная блокировка.");
            Add("429", "Слишком много попыток входа с этого адреса.");
        }
        return Task.CompletedTask;
    }
}
