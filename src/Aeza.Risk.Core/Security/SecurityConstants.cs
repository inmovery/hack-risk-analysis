namespace Aeza.Risk.Core.Security;

/// <summary>Роли пользователей и машинных клиентов</summary>
public static class Roles
{
    /// <summary>Администратор: всё, включая изменение модели и загрузку тестовых данных</summary>
    public const string Admin = "admin";

    /// <summary>Аналитик: только чтение</summary>
    public const string Analyst = "analyst";

    /// <summary>Система-источник (Bearer INGEST_TOKEN)</summary>
    public const string MachineIngest = "ingest";
}

/// <summary>Политики авторизации</summary>
public static class Policies
{
    /// <summary>Чтение данных (admin, analyst)</summary>
    public const string Reader = "reader";

    /// <summary>Изменения (admin)</summary>
    public const string Admin = "admin";

    /// <summary>Загрузка тестовых данных (admin или ingest-токен)</summary>
    public const string SourceWriter = "source-writer";

    /// <summary>Статус своего импорта: пользователи панели или токен входящего коннектора (только счётчики, без данных клиентов)</summary>
    public const string ImportStatus = "import-status";
}

/// <summary>Claims cookie-сессии</summary>
public static class AuthClaims
{
    /// <summary>Идентификатор серверной сессии</summary>
    public const string SessionId = "sid";

    /// <summary>Идентификатор пользователя</summary>
    public const string UserId = "uid";

    /// <summary>Окончание сессии (ISO 8601)</summary>
    public const string ExpiresAt = "exp_at";
}

/// <summary>Схемы аутентификации</summary>
public static class AuthSchemes
{
    /// <summary>Cookie-сессия пользователя</summary>
    public const string Cookie = "Cookies";

    /// <summary>Bearer-токены машинных клиентов</summary>
    public const string Machine = "Machine";
}

/// <summary>Утверждения (claims) машинных клиентов</summary>
public static class MachineClaims
{
    /// <summary>Имя входящего коннектора источника (http-push или из Auth:IngestConnectors)</summary>
    public const string Connector = "aeza:connector";
}

/// <summary>CSRF: заголовок и читаемая cookie</summary>
public static class CsrfConstants
{
    /// <summary>Заголовок с токеном для изменяющих запросов</summary>
    public const string HeaderName = "X-CSRF-TOKEN";

    /// <summary>Читаемая JS cookie с токеном (Swagger UI, панель на том же хосте)</summary>
    public const string ReadableCookie = "XSRF-TOKEN";

    /// <summary>HttpOnly cookie antiforgery</summary>
    public const string AntiforgeryCookie = "aeza.csrf";
}

/// <summary>Политики ограничения частоты запросов</summary>
public static class RateLimitPolicies
{
    /// <summary>Вход: попыток в минуту с одного IP</summary>
    public const string Login = "login";
}
