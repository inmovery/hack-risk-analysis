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

namespace Aeza.Risk.Feature.Auth.Me;

/// <summary>Текущий пользователь</summary>
[UsedImplicitly]
public class GetMe
{
    private static Ok<UserInfo> Handle(ClaimsPrincipal user)
    {
        var exp = user.FindFirst(AuthClaims.ExpiresAt)?.Value;
        return TypedResults.Ok(new UserInfo(user.Identity!.Name!, user.FindFirst(ClaimTypes.Role)!.Value,
            exp is null ? null : DateTimeOffset.Parse(exp, CultureInfo.InvariantCulture)));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetMe);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("auth/me", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("Auth")
                .WithSummary("Текущий пользователь");
    }
}
