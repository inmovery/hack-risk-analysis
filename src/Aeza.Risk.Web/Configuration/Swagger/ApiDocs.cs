using Aeza.Risk.Core.Options;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Feature.Auth;
using Aeza.Risk.Feature.Docs;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace Aeza.Risk.Web.Configuration.Swagger;

/// <summary>OpenAPI (по документу на версию) + Swagger UI на обязательном пути /api/v1/docs</summary>
public static class ApiDocs
{
    /// <summary>Спецификация версии v1</summary>
    public const string DocumentPath = "/api/v1/openapi.json";

    private const string IndexHtml = """
        <!DOCTYPE html>
        <html lang="ru">
        <head>
          <meta charset="UTF-8">
          <title>Aeza Risk API — Swagger</title>
          <link rel="stylesheet" href="./swagger-ui.css">
          <link rel="stylesheet" href="./index.css">
        </head>
        <body>
          <div id="swagger-ui"></div>
          <script src="./swagger-ui-bundle.js"></script>
          <script src="./swagger-ui-standalone-preset.js"></script>
          <script src="./aeza-init.js"></script>
        </body>
        </html>
        """;

    // Собственный init-скрипт без eval: штатный index.js Swashbuckle собирает конфиг через Function, что запрещено CSP `script-src 'self'`
    private const string InitScript = """
        window.onload = function () {
          window.ui = SwaggerUIBundle({
            url: '/api/v1/openapi.json',
            dom_id: '#swagger-ui',
            presets: [SwaggerUIBundle.presets.apis, SwaggerUIStandalonePreset],
            layout: 'StandaloneLayout',
            deepLinking: true,
            tryItOutEnabled: true,
            displayRequestDuration: true,
            validatorUrl: null,
            requestInterceptor: function (req) {
              var m = document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]+)/);
              if (m) { req.headers['X-CSRF-TOKEN'] = decodeURIComponent(m[1]); }
              req.credentials = 'same-origin';
              return req;
            }
          });
        };
        """;

    /// <summary>Браузерный запрос страницы документации</summary>
    public static bool IsBrowserDocsRequest(HttpRequest request) =>
        request.Path.StartsWithSegments(GetDocs.UiPath) && request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    /// <summary>Форма входа панели с возвратом на документацию</summary>
    public static string LoginRedirect(HttpRequest request) =>
        "/login?returnUrl=" + Uri.EscapeDataString(request.Path.Value + request.QueryString.Value);

    /// <summary>Спецификация, init-скрипт и Swagger UI; при Docs:RequireAuth=true — только после входа</summary>
    public static WebApplication UseApiDocs(this WebApplication app, IConfiguration configuration)
    {
        var requireAuth = configuration.GetValue("Docs:RequireAuth", false);
        var loginPageOnHost = configuration.GetValue("Spa:Enabled", true);
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(GetDocs.UiPath))
            {
                if (requireAuth)
                {
                    var user = ctx.User;
                    var ok = user.Identity?.IsAuthenticated == true && (user.IsInRole(Roles.Admin) || user.IsInRole(Roles.Analyst));
                    if (!ok)
                    {
                        if (loginPageOnHost && IsBrowserDocsRequest(ctx.Request))
                        {
                            ctx.Response.Redirect(LoginRedirect(ctx.Request));
                            return;
                        }
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }
                // CSRF-токен в читаемой cookie: interceptor Swagger UI подставляет его в X-CSRF-TOKEN (в т.ч. для POST /auth/login)
                if (ctx.Request.Path.Value is GetDocs.UiPath or GetDocs.UiPath + "/" or GetDocs.UiPath + "/index.html")
                    CsrfTokens.Issue(ctx, ctx.RequestServices.GetRequiredService<IAntiforgery>(),
                        ctx.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.SecureCookies);
            }
            await next();
        });

        var openApi = app.MapOpenApi("/api/{documentName}/openapi.json").WithDocumentPerVersion();
        var init = app.MapGet(GetDocs.UiPath + "/aeza-init.js", () => Results.Text(InitScript, "application/javascript; charset=utf-8"))
            .ExcludeFromDescription();
        if (requireAuth)
        {
            openApi.RequireAuthorization(Policies.Reader);
            init.RequireAuthorization(Policies.Reader);
        }
        else
        {
            openApi.AllowAnonymous();
            init.AllowAnonymous();
        }

        app.UseSwaggerUI(o =>
        {
            o.RoutePrefix = GetDocs.UiPath.TrimStart('/');
            o.SwaggerEndpoint(DocumentPath, "Aeza Risk Analyzer API v1");
            o.DocumentTitle = "Aeza Risk API — Swagger";
            o.IndexStream = () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(IndexHtml));
        });
        return app;
    }
}
