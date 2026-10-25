using System.Globalization;
using System.Security.Claims;

using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Options;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Aeza.Risk.Feature.Auth.Logout;

/// <summary>Завершить сессию (отзыв на сервере, закрытие live-соединений)</summary>
[UsedImplicitly]
public class Logout
{
    private static async Task<NoContent> Handle(HttpContext http, ISessionStore sessions, ILiveSessions live, IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var sid = http.User.FindFirst(AuthClaims.SessionId)?.Value;
        if (sid is not null)
        {
            await sessions.RevokeAsync(sid, cancellationToken);
            live.AbortSession(sid);
        }
        await audit.WriteAsync(http.User.Identity?.Name ?? "", "logout", new { }, cancellationToken);
        await http.SignOutAsync(AuthSchemes.Cookie);
        http.Response.Cookies.Delete(CsrfConstants.ReadableCookie);
        return TypedResults.NoContent();
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(Logout);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPost("auth/logout", Handle)
                .RequireAuthorization(Policies.Reader)
                .AddEndpointFilter<CsrfFilter>()
                .WithTags("Auth")
                .WithSummary("Завершить сессию (отзыв на сервере, закрытие live-соединений)");
    }
}
