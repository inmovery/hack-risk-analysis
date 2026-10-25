using Aeza.Risk.Infrastructure.Live;

namespace Aeza.Risk.Web.Core;

/// <summary>Отвечает за штатный CORS: панель на отдельном домене ходит в API с cookie</summary>
public static class CorsConfiguration
{
    /// <summary>Имя политики</summary>
    public const string CorsPolicyName = "frontendOriginsPolicy";

    /// <summary>Политика CORS только для явно перечисленных origin (CorsOrigins)</summary>
    public static IServiceCollection AddCorsPolicy(this IServiceCollection services, string[]? origins)
    {
        var allowed = new AllowedOrigins((origins ?? []).Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim().TrimEnd('/')).ToArray());
        services.AddSingleton(allowed);
        services.AddCors(options =>
        {
            options.AddPolicy(CorsPolicyName,
                corsPolicyBuilder =>
                {
                    corsPolicyBuilder.WithOrigins(allowed.Origins)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .SetIsOriginAllowedToAllowWildcardSubdomains()
                        .AllowCredentials()
                        .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
                });
        });
        return services;
    }

    /// <summary>CORS + проверка Origin для hub: WebSocket-рукопожатие CORS не проходит, поэтому hub принимает только свой origin и разрешённые</summary>
    public static IApplicationBuilder UseCorsPolicy(this IApplicationBuilder app)
    {
        app.UseCors(CorsPolicyName);
        var origins = app.ApplicationServices.GetRequiredService<AllowedOrigins>().Origins;
        if (origins.Length == 0) return app;
        return app.Use(async (ctx, next) =>
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            if (ctx.Request.Path.StartsWithSegments(RiskHub.Path) && origin.Length > 0
                && !origins.Contains(origin, StringComparer.OrdinalIgnoreCase)
                && !string.Equals(origin, $"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });
    }

    /// <summary>Разрешённые origin панели</summary>
    public sealed record AllowedOrigins(string[] Origins);
}
