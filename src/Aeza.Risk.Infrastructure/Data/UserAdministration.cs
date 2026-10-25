using Aeza.Risk.Core.Abstractions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aeza.Risk.Infrastructure.Data;

/// <summary>Администрирование пользователей из консоли (Migrator set-password)</summary>
public sealed class UserAdministration(IUserRepository users, IPasswordHasher passwords, TimeProvider time, AppDbContext db, ILogger<UserAdministration> logger)
{
    /// <summary>Сменить пароль пользователя и отозвать его сессии; возвращает код выхода</summary>
    public async Task<int> SetPasswordAsync(string login, string password, CancellationToken cancellationToken)
    {
        if (password.Length < DataSeeder.MinPasswordLength)
        {
            logger.LogError("Password must be at least {Length} characters", DataSeeder.MinPasswordLength);
            return 2;
        }
        var user = await users.FindByLoginAsync(login.Trim().ToLowerInvariant(), cancellationToken);
        if (user is null)
        {
            logger.LogError("User not found");
            return 3;
        }
        user.PasswordHash = passwords.Hash(user, password);
        user.FailedCount = 0;
        user.LockedUntil = null;
        await users.RevokeSessionsAsync(user.Id, time.GetUtcNow(), cancellationToken);
        await users.CommitAsync(cancellationToken);
        logger.LogInformation("Password updated, active sessions of the user revoked");
        try
        {
            // Работающие реплики сервиса сбрасывают кэш проверок сессий сразу (иначе отзыв виден через 15 с) и закрывают live-соединения
            await db.Database.ExecuteSqlRawAsync("SELECT pg_notify({0}, {1})",
                [Live.LiveChannel.Name, Live.LiveChannel.Serialize(Live.LiveChannel.UserSessionsRevoked, new { userId = user.Id })], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Session revocation was not broadcast; replicas will see it within 15 s");
        }
        return 0;
    }
}
