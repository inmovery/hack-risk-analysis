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

namespace Aeza.Risk.Feature.Auth.Csrf;

/// <summary>Получить CSRF-токен (для login/logout/PATCH/PUT в заголовке X-CSRF-TOKEN)</summary>
[UsedImplicitly]
public class GetCsrf
{
    private static Ok<CsrfResponse> Handle(HttpContext http, IAntiforgery antiforgery, IOptions<AuthOptions> options) =>
        TypedResults.Ok(new CsrfResponse(CsrfTokens.Issue(http, antiforgery, options.Value.SecureCookies)));

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetCsrf);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("auth/csrf", Handle)
                .AllowAnonymous()
                .WithTags("Auth")
                .WithSummary("Получить CSRF-токен (для login/logout/PATCH/PUT в заголовке X-CSRF-TOKEN)");
    }
}
