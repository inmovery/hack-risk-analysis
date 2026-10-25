using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Options;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Source;
using Aeza.Risk.Infrastructure.Data;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace Aeza.Risk.Infrastructure.Security;

/// <summary>Серверные сессии: создание, проверка (с кэшем 15 с) и отзыв</summary>
public sealed class SessionStore(IServiceScopeFactory scopes, NpgsqlDataSource dataSource, TimeProvider time, ILogger<SessionStore> log)
    : ISessionStore, IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    // Свой кэш, а не общий IMemoryCache: при отзыве всех сессий пользователя его можно очистить целиком
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    /// <summary>SHA-256 идентификатора сессии (в БД и в канале между репликами — только хеш)</summary>
    public static string Hash(string sid) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));

    /// <inheritdoc />
    public async Task<(string Sid, DateTimeOffset ExpiresAt)> CreateAsync(long userId, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var sid = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        var session = new SessionEntity { IdHash = Hash(sid), UserId = userId, CreatedAt = now, LastSeenAt = now, ExpiresAt = now + lifetime };
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return (sid, session.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<bool> IsValidAsync(string sid, CancellationToken cancellationToken)
    {
        var hash = Hash(sid);
        var key = "sess:" + hash;
        if (_cache.TryGetValue(key, out DateTimeOffset expires)) return expires > time.GetUtcNow();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = await db.Sessions.AsNoTracking().Where(x => x.IdHash == hash)
            .Join(db.Users, x => x.UserId, u => u.Id, (x, u) => new { x.ExpiresAt, x.RevokedAt, u.Disabled })
            .SingleOrDefaultAsync(cancellationToken);
        var valid = s is not null && s.RevokedAt is null && !s.Disabled && s.ExpiresAt > time.GetUtcNow();
        _cache.Set(key, valid ? s!.ExpiresAt : DateTimeOffset.MinValue, CacheTtl);
        return valid;
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string sid, CancellationToken cancellationToken)
    {
        var hash = Hash(sid);
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Sessions.Where(x => x.IdHash == hash && x.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, time.GetUtcNow()), cancellationToken);
        }
        Evict(hash);
        try
        {
            await Live.LiveChannel.NotifyAsync(dataSource, Live.LiveChannel.SessionRevoked, new { idHash = hash }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Отзыв уже в БД: другие реплики увидят его не позже чем через 15 с (срок кэша)
            log.LogWarning(ex, "Session revocation was not broadcast to other replicas");
        }
    }

    /// <summary>Забыть проверку сессии (отзыв с другой реплики)</summary>
    public void Evict(string hash) => _cache.Remove("sess:" + hash);

    /// <summary>Забыть все проверки (отзыв всех сессий пользователя, восстановление канала)</summary>
    public void EvictAll() => _cache.Compact(1.0);

    /// <inheritdoc />
    public void Dispose() => _cache.Dispose();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Хеширование паролей (PBKDF2 из ASP.NET Core Identity)</summary>
public sealed class PasswordHasherService : Core.Abstractions.IPasswordHasher
{
    private static readonly UserEntity Dummy = new() { Login = "dummy" };
    private readonly PasswordHasher<UserEntity> _hasher = new();
    private readonly string _dummyHash;

    /// <summary>Создаёт сервис с заранее посчитанным фиктивным хешем</summary>
    public PasswordHasherService() => _dummyHash = _hasher.HashPassword(Dummy, Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public string Hash(UserEntity user, string password) => _hasher.HashPassword(user, password);

    /// <inheritdoc />
    public bool Verify(UserEntity user, string password, out bool rehash)
    {
        var r = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        rehash = r == PasswordVerificationResult.SuccessRehashNeeded;
        return r != PasswordVerificationResult.Failed;
    }

    /// <inheritdoc />
    public void VerifyDummy(string password) => _hasher.VerifyHashedPassword(Dummy, _dummyHash, password);
}

/// <summary>Машинная аутентификация Bearer-токеном системы-источника: приём событий и тестовых данных</summary>
public sealed class MachineTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AuthOptions> auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(AuthenticateResult.NoResult());
        var token = header["Bearer ".Length..].Trim();
        // Все токены сравниваются за постоянное время, без раннего выхода: время ответа не выдаёт, какой токен совпал
        string? connector = Matches(token, auth.Value.IngestToken) ? ConnectorNames.HttpPush : null;
        foreach (var (name, value) in auth.Value.IngestConnectors)
            if (Matches(token, value)) connector ??= name;
        if (connector is null) return Task.FromResult(AuthenticateResult.Fail("invalid token"));
        var role = Roles.MachineIngest;
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, $"machine:{connector}"), new Claim(ClaimTypes.Role, role), new Claim(MachineClaims.Connector, connector)],
            AuthSchemes.Machine);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSchemes.Machine)));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private static bool Matches(string presented, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
