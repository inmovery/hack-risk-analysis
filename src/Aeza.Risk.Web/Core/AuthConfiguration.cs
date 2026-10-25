using System.Threading.RateLimiting;

using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Options;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Infrastructure.Security;
using Aeza.Risk.Web.Configuration.Swagger;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Aeza.Risk.Web.Core;

/// <summary>Аутентификация (cookie-сессия + машинные токены), политики, antiforgery, ограничение частоты входа</summary>
public static class AuthConfiguration
{
    /// <summary>Регистрация схем, политик и защиты от CSRF/перебора</summary>
    public static IServiceCollection AddAppAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        var spaEnabled = configuration.GetValue("Spa:Enabled", true);

        services.AddAuthentication(AuthSchemes.Cookie)
            .AddCookie(AuthSchemes.Cookie, o =>
            {
                o.Cookie.Name = "aeza.session";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = auth.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(auth.SessionHours);
                o.SlidingExpiration = false;
                o.Events.OnRedirectToLogin = ctx =>
                {
                    // Закрытая документация (Docs:RequireAuth=true) при панели на этом же хосте: браузер → форма входа с возвратом; иначе 401
                    if (spaEnabled && ApiDocs.IsBrowserDocsRequest(ctx.Request))
                    {
                        ctx.Response.Redirect(ApiDocs.LoginRedirect(ctx.Request));
                        return Task.CompletedTask;
                    }
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                o.Events.OnValidatePrincipal = async ctx =>
                {
                    var sid = ctx.Principal?.FindFirst(AuthClaims.SessionId)?.Value;
                    var sessions = ctx.HttpContext.RequestServices.GetRequiredService<ISessionStore>();
                    if (sid is null || !await sessions.IsValidAsync(sid, ctx.HttpContext.RequestAborted))
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(AuthSchemes.Cookie);
                        ctx.HttpContext.Response.Cookies.Delete(CsrfConstants.ReadableCookie); // токен был привязан к отозванной сессии
                    }
                };
            })
            .AddScheme<AuthenticationSchemeOptions, MachineTokenHandler>(AuthSchemes.Machine, null);
        // Срок cookie-тикета и серверной сессии считаются одними часами (TimeProvider из DI; в тестах — FakeTimeProvider)
        services.AddOptions<CookieAuthenticationOptions>(AuthSchemes.Cookie).Configure<TimeProvider>((o, tp) => o.TimeProvider = tp);

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Reader, p => p.AddAuthenticationSchemes(AuthSchemes.Cookie).RequireRole(Roles.Admin, Roles.Analyst))
            .AddPolicy(Policies.Admin, p => p.AddAuthenticationSchemes(AuthSchemes.Cookie).RequireRole(Roles.Admin))
            .AddPolicy(Policies.SourceWriter, p => p.AddAuthenticationSchemes(AuthSchemes.Cookie, AuthSchemes.Machine).RequireRole(Roles.Admin, Roles.MachineIngest))
            .AddPolicy(Policies.ImportStatus, p => p.AddAuthenticationSchemes(AuthSchemes.Cookie, AuthSchemes.Machine).RequireRole(Roles.Admin, Roles.Analyst, Roles.MachineIngest));

        services.AddAntiforgery(o =>
        {
            o.HeaderName = CsrfConstants.HeaderName;
            o.Cookie.Name = CsrfConstants.AntiforgeryCookie;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = auth.SecureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicies.Login, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = auth.LoginPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        return services;
    }
}
