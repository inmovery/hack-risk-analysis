using Aeza.Risk.Feature.Docs;

using Microsoft.AspNetCore.HttpOverrides;

namespace Aeza.Risk.Web.Core;

/// <summary>Части конвейера: доверенный прокси, заголовки безопасности, статика панели, fallback-маршруты</summary>
public static class PipelineExtensions
{
    /// <summary>Доверяем X-Forwarded-* только от частных сетей (Docker/Compose); прямой доступ к app извне закрыт привязкой к 127.0.0.1</summary>
    public static IApplicationBuilder UseProxyHeaders(this IApplicationBuilder app, IConfiguration configuration)
    {
        if (!configuration.GetValue("Proxy:Enabled", false)) return app;
        var fwd = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = configuration.GetValue("Proxy:ForwardLimit", 1),
        };
        fwd.KnownProxies.Clear();
        fwd.KnownIPNetworks.Clear();
        foreach (var net in (configuration["Proxy:KnownNetworks"] ?? "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16")
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            fwd.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(net));
        return app.UseForwardedHeaders(fwd);
    }

    /// <summary>Заголовки безопасности и CSP для всех ответов</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; " +
                                      "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            await next();
        });

    /// <summary>Статика панели (SPA) с кешированием ассетов</summary>
    public static IApplicationBuilder UseSpaStaticFiles(this IApplicationBuilder app)
    {
        app.UseDefaultFiles();
        return app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var path = ctx.Context.Request.Path.Value ?? "";
                ctx.Context.Response.Headers.CacheControl = path.StartsWith("/assets/", StringComparison.Ordinal) ? "public, max-age=31536000, immutable" : "no-cache";
            },
        });
    }

    /// <summary>/api/* без маршрута — ProblemDetails 404; остальное — SPA (index.html) или, без панели, корень → Swagger</summary>
    public static void MapApiFallbacks(this WebApplication app, bool spaEnabled)
    {
        app.MapFallback("/api/{**rest}", () => Results.Problem(statusCode: 404, title: "Маршрут API не найден",
                extensions: new Dictionary<string, object?> { ["code"] = "route_not_found" }))
            .ExcludeFromDescription();
        if (spaEnabled)
            app.MapFallbackToFile("index.html");
        else
            app.MapGet("/", () => Results.Redirect(GetDocs.UiPath)).ExcludeFromDescription(); // корень API-хоста → Swagger
    }
}
