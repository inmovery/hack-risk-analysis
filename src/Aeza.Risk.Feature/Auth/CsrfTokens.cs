using Aeza.Risk.Core.Security;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace Aeza.Risk.Feature.Auth;

/// <summary>Выдача CSRF-токена</summary>
public static class CsrfTokens
{
    /// <summary>Выдаёт токен и кладёт его в читаемую JS cookie (паттерн SPA): Swagger UI и панель подставляют его в заголовок</summary>
    public static string Issue(HttpContext http, IAntiforgery antiforgery, bool secure)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append(CsrfConstants.ReadableCookie, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = secure,
            Path = "/",
            IsEssential = true,
        });
        return tokens.RequestToken!;
    }
}
