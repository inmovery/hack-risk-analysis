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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aeza.Risk.Feature.Auth.Login;

/// <summary>Вход по логину и паролю (cookie-сессия)</summary>
[UsedImplicitly]
public class Login
{
    /// <summary>Неверный логин, пароль или блокировка дают одинаковый ответ 401. Блокировка — по паре (логин, IP), общая для всех реплик</summary>
    private static async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> Handle(
        [FromBody] LoginRequest body, HttpContext http, IUserRepository users, IPasswordHasher passwords, ISessionStore sessions,
        IAuditLog audit, IAntiforgery antiforgery, IOptions<AuthOptions> options, TimeProvider time, ILoggerFactory loggerFactory,
        ILoginThrottle throttle, CancellationToken cancellationToken)
    {
        var log = loggerFactory.CreateLogger<Login>();
        var o = options.Value;
        var invalid = AppProblems.Create(StatusCodes.Status401Unauthorized, "Неверный логин или пароль", "invalid_credentials");
        if (string.IsNullOrWhiteSpace(body.Login) || string.IsNullOrEmpty(body.Password) || body.Login.Length > 128 || body.Password.Length > 512)
            return invalid;

        var normalized = body.Login.Trim().ToLowerInvariant();
        var now = time.GetUtcNow();
        // Блокировка по паре (логин, IP): перебор с одного адреса останавливается, а чужой IP не может «запереть» админа навсегда
        var lockKey = $"{normalized}\n{http.Connection.RemoteIpAddress}";
        if (await throttle.IsLockedAsync(lockKey, now, cancellationToken))
        {
            passwords.VerifyDummy(body.Password);
            log.LogWarning("Login rejected: login/IP pair temporarily locked");
            return invalid;
        }

        var user = await users.FindByLoginAsync(normalized, cancellationToken);
        if (user is null || user.Disabled)
        {
            passwords.VerifyDummy(body.Password);
            await RegisterFailureAsync();
            log.LogWarning("Login failed: unknown or disabled account");
            return invalid;
        }
        if (!passwords.Verify(user, body.Password, out var rehash))
        {
            user.FailedCount++; // счётчик для аудита; блокировка — по паре (логин, IP)
            await users.CommitAsync(cancellationToken);
            await RegisterFailureAsync();
            return invalid;
        }

        await throttle.ResetAsync(lockKey, cancellationToken);
        user.FailedCount = 0;
        user.LockedUntil = null;
        if (rehash) user.PasswordHash = passwords.Hash(user, body.Password);
        await users.CommitAsync(cancellationToken);
        var (sid, expires) = await sessions.CreateAsync(user.Id, TimeSpan.FromHours(o.SessionHours), cancellationToken);
        await audit.WriteAsync(user.Login, "login", new { }, cancellationToken);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, user.Login),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(AuthClaims.UserId, user.Id.ToString(CultureInfo.InvariantCulture)),
            new Claim(AuthClaims.SessionId, sid),
            new Claim(AuthClaims.ExpiresAt, expires.ToString("O", CultureInfo.InvariantCulture)),
        ], AuthSchemes.Cookie));
        // Сессионная cookie (без expires у браузера); срок жизни задают тикет и серверная запись сессии
        await http.SignInAsync(AuthSchemes.Cookie, principal, new AuthenticationProperties { IsPersistent = false, ExpiresUtc = expires, AllowRefresh = false });
        http.User = principal; // CSRF-токен привязан к новому пользователю
        var csrf = CsrfTokens.Issue(http, antiforgery, o.SecureCookies);
        log.LogInformation("Login succeeded for user {UserId}", user.Id);
        return TypedResults.Ok(new LoginResponse(new UserInfo(user.Login, user.Role, expires), csrf));

        async Task RegisterFailureAsync()
        {
            if (await throttle.RegisterFailureAsync(lockKey, now, o.MaxFailedAttempts, TimeSpan.FromMinutes(o.LockoutMinutes), cancellationToken))
                log.LogWarning("Login/IP pair locked for {Minutes} min", o.LockoutMinutes);
        }
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(Login);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPost("auth/login", Handle)
                .AllowAnonymous()
                .AddEndpointFilter<CsrfFilter>()
                .RequireRateLimiting(RateLimitPolicies.Login)
                .WithTags("Auth")
                .WithSummary("Вход по логину и паролю (cookie-сессия)")
                .WithDescription("Неверный логин, пароль или блокировка дают одинаковый ответ 401. После входа возвращается новый CSRF-токен.");
    }
}
