using JetBrains.Annotations;

namespace Aeza.Risk.Feature.Auth;

/// <summary>Запрос входа</summary>
/// <param name="Login">Логин</param>
/// <param name="Password">Пароль</param>
[PublicAPI]
public sealed record LoginRequest(string Login, string Password);

/// <summary>Текущий пользователь</summary>
/// <param name="Login">Логин</param>
/// <param name="Role">Роль: admin | analyst</param>
/// <param name="SessionExpiresAt">Окончание сессии</param>
[PublicAPI]
public sealed record UserInfo(string Login, string Role, DateTimeOffset? SessionExpiresAt);

/// <summary>Ответ на вход</summary>
/// <param name="User">Пользователь</param>
/// <param name="CsrfToken">Новый CSRF-токен (привязан к сессии)</param>
[PublicAPI]
public sealed record LoginResponse(UserInfo User, string CsrfToken);

/// <summary>CSRF-токен</summary>
/// <param name="Token">Значение для заголовка X-CSRF-TOKEN</param>
[PublicAPI]
public sealed record CsrfResponse(string Token);
