using System.ComponentModel.DataAnnotations;

using JetBrains.Annotations;

namespace Aeza.Risk.Core.Options;

/// <summary>Параметры аутентификации (секция Auth): bootstrap-пользователи, машинные токены, сессии, блокировки</summary>
[UsedImplicitly]
public sealed class AuthOptions
{
    /// <summary>Имя секции</summary>
    public const string Section = "Auth";

    /// <summary>Логин bootstrap-администратора (создаётся только если его ещё нет)</summary>
    public string? AdminLogin { get; set; }

    /// <summary>Пароль bootstrap-администратора</summary>
    public string? AdminPassword { get; set; }

    /// <summary>Логин bootstrap-аналитика (только чтение)</summary>
    public string? AnalystLogin { get; set; }

    /// <summary>Пароль bootstrap-аналитика</summary>
    public string? AnalystPassword { get; set; }

    /// <summary>Bearer-токен системы-источника: открывает только приём событий и снимка (POST /source/events, PUT /source/snapshot)</summary>
    public string? IngestToken { get; set; }

    /// <summary>Дополнительные входящие коннекторы: имя → Bearer-токен (те же права, что у IngestToken)</summary>
    public Dictionary<string, string> IngestConnectors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Cookie только по HTTPS (за TLS-прокси)</summary>
    public bool SecureCookies { get; set; }

    /// <summary>Срок жизни сессии, часы</summary>
    [Range(1, 168)]
    public int SessionHours { get; set; } = 8;

    /// <summary>Неудачных попыток до блокировки пары (логин, IP)</summary>
    [Range(1, 100)]
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>Длительность блокировки, минуты</summary>
    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 5;

    /// <summary>Попыток входа в минуту с одного IP (rate limit)</summary>
    [Range(1, 100000)]
    public int LoginPerMinute { get; set; } = 10;

    /// <summary>Разрешить короткие bootstrap-пароли вне Development (только для демо-стендов)</summary>
    public bool AllowWeakBootstrapPasswords { get; set; }
}
