using Aeza.Risk.Core.Entities;

namespace Aeza.Risk.Core.Abstractions;

/// <summary>Пользователи панели</summary>
public interface IUserRepository
{
    /// <summary>Пользователь по нормализованному логину (для изменений)</summary>
    Task<UserEntity?> FindByLoginAsync(string normalizedLogin, CancellationToken cancellationToken);

    /// <summary>Есть ли пользователь с логином</summary>
    Task<bool> ExistsAsync(string normalizedLogin, CancellationToken cancellationToken);

    /// <summary>Есть ли хотя бы один администратор</summary>
    Task<bool> AnyAdminAsync(CancellationToken cancellationToken);

    /// <summary>Добавить пользователя (сохранение — <see cref="CommitAsync"/>)</summary>
    void Add(UserEntity user);

    /// <summary>Отозвать все активные сессии пользователя</summary>
    Task RevokeSessionsAsync(long userId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Сохранить изменения</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>Серверные сессии: в cookie — зашифрованный тикет с sid, в БД — SHA-256(sid) для отзыва</summary>
public interface ISessionStore
{
    /// <summary>Создать сессию</summary>
    Task<(string Sid, DateTimeOffset ExpiresAt)> CreateAsync(long userId, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Сессия действующая (не отозвана, не истекла, пользователь не отключён)</summary>
    Task<bool> IsValidAsync(string sid, CancellationToken cancellationToken);

    /// <summary>Отозвать сессию</summary>
    Task RevokeAsync(string sid, CancellationToken cancellationToken);
}

/// <summary>Счётчик неудачных входов по ключу (логин, IP), общий для всех реплик сервиса (хранится в PostgreSQL)</summary>
public interface ILoginThrottle
{
    /// <summary>Ключ заблокирован на момент now</summary>
    Task<bool> IsLockedAsync(string key, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Зарегистрировать неудачу; true — эта неудача включила блокировку на lockout</summary>
    Task<bool> RegisterFailureAsync(string key, DateTimeOffset now, int maxFailures, TimeSpan lockout, CancellationToken cancellationToken);

    /// <summary>Сбросить счётчик после успешного входа</summary>
    Task ResetAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Хеширование паролей</summary>
public interface IPasswordHasher
{
    /// <summary>Хеш пароля пользователя</summary>
    string Hash(UserEntity user, string password);

    /// <summary>Проверка пароля; rehash — хеш устарел и его стоит пересчитать</summary>
    bool Verify(UserEntity user, string password, out bool rehash);

    /// <summary>Выравнивание времени ответа, когда логина нет (не раскрываем существование учётной записи)</summary>
    void VerifyDummy(string password);
}

/// <summary>Журнал аудита действий пользователей</summary>
public interface IAuditLog
{
    /// <summary>Записать событие (сохраняется сразу)</summary>
    Task WriteAsync(string actor, string action, object details, CancellationToken cancellationToken);
}
