using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Aeza.Risk.Feature.Docs;

/// <summary>Обязательный маршрут Swagger UI: redirect на index.html (страницу обслуживает Swagger UI middleware)</summary>
[UsedImplicitly]
public class GetDocs
{
    /// <summary>Путь Swagger UI</summary>
    public const string UiPath = "/api/v1/docs";

    private static RedirectHttpResult Handle() => TypedResults.Redirect(UiPath + "/index.html");

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint(IConfiguration configuration) : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetDocs);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
        {
            var builder = app.MapGet("docs", Handle).ExcludeFromDescription();
            // Docs:RequireAuth=true — документация только после входа; по умолчанию открыта (в ней нет данных)
            return configuration.GetValue("Docs:RequireAuth", false)
                ? builder.RequireAuthorization(Policies.Reader)
                : builder.AllowAnonymous();
        }
    }
}
