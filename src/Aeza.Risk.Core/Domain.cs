namespace Aeza.Risk.Core;

/// <summary>Статус услуги в источнике</summary>
public enum ServiceStatus { Active, Suspended, ProlongWait, Deleted, Blocked, Unknown }

/// <summary>Период оплаты услуги</summary>
public enum PaymentTerm { Hour, Week, Month, QuarterYear, HalfYear, Year, Unknown }

/// <summary>Возможность тратить бонусный баланс</summary>
public enum BonusState { Locked, Unlocked, Unknown }

public enum TransactionStatus { Performed, NotPerformed, Unknown }

public enum TransactionType { Order, Prolong, Unknown }

public enum RiskLevel { Low, Medium, High, Unknown }

public enum ScoreState { Complete, Partial, NoRenewableServices, InsufficientData }

/// <summary>Счёт клиента — поля сохраняют исходные имена источника; деньги в еврокопейках</summary>
public sealed record AccountInput(
    long Id,
    string Email,
    long? Balance,
    long? WithdrawBalance,
    long? TotalReplenished,
    long? BonusBalance,
    string? BonusState);

public sealed record ServiceInput(
    long Id,
    long OwnerId,
    string Name,
    string? PaymentTerm,
    bool? AutoProlong,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ExpiresAt,
    string? Status,
    long? Price);

public sealed record TransactionInput(
    long Id,
    long ServiceId,
    long? Amount,
    string? Status,
    DateTimeOffset? PerformedAt,
    DateTimeOffset? CreatedAt,
    string? Type);

/// <summary>Канонический снимок источника, независимый от формата входа</summary>
public sealed record SourceData(
    IReadOnlyList<AccountInput> Accounts,
    IReadOnlyList<ServiceInput> Services,
    IReadOnlyList<TransactionInput> Transactions);

public static class SourceEnums
{
    public static ServiceStatus ParseStatus(string? raw) => raw switch
    {
        "active" => ServiceStatus.Active,
        "suspended" => ServiceStatus.Suspended,
        "prolong_wait" => ServiceStatus.ProlongWait,
        "deleted" => ServiceStatus.Deleted,
        "blocked" => ServiceStatus.Blocked,
        _ => ServiceStatus.Unknown,
    };

    public static PaymentTerm ParseTerm(string? raw) => raw switch
    {
        "hour" => PaymentTerm.Hour,
        "week" => PaymentTerm.Week,
        "month" => PaymentTerm.Month,
        "quarter_year" => PaymentTerm.QuarterYear,
        "half_year" => PaymentTerm.HalfYear,
        "year" => PaymentTerm.Year,
        _ => PaymentTerm.Unknown,
    };

    public static BonusState ParseBonusState(string? raw) => raw switch
    {
        "unlocked" => BonusState.Unlocked,
        "locked" => BonusState.Locked,
        _ => BonusState.Unknown,
    };

    public static TransactionStatus ParseTransactionStatus(string? raw) => raw switch
    {
        "performed" => TransactionStatus.Performed,
        "not_performed" => TransactionStatus.NotPerformed,
        _ => TransactionStatus.Unknown,
    };

    public static TransactionType ParseTransactionType(string? raw) => raw switch
    {
        "order" => TransactionType.Order,
        "prolong" => TransactionType.Prolong,
        _ => TransactionType.Unknown,
    };

    public static bool IsRenewable(ServiceStatus s) =>
        s is ServiceStatus.Active or ServiceStatus.Suspended or ServiceStatus.ProlongWait;

    public static string ToApi(this RiskLevel level) => level switch
    {
        RiskLevel.High => "high",
        RiskLevel.Medium => "medium",
        RiskLevel.Low => "low",
        _ => "unknown",
    };

    public static string ToApi(this ScoreState state) => state switch
    {
        ScoreState.Complete => "complete",
        ScoreState.Partial => "partial",
        ScoreState.NoRenewableServices => "no_renewable_services",
        _ => "insufficient_data",
    };

    public static RiskLevel? ParseLevel(string? raw) => raw switch
    {
        "high" => RiskLevel.High,
        "medium" => RiskLevel.Medium,
        "low" => RiskLevel.Low,
        "unknown" => RiskLevel.Unknown,
        _ => null,
    };
}

/// <summary>Коды флагов качества данных — стабильные строки для API, UI и метрик</summary>
public static class QualityFlags
{
    public const string ProlongWaitBeyondGrace = "PROLONG_WAIT_BEYOND_GRACE";
    public const string ExpiryPassedStatusNotUpdated = "EXPIRY_PASSED_STATUS_NOT_UPDATED";
    public const string BonusStateUnknown = "BONUS_STATE_UNKNOWN";
    public const string FundingIncomplete = "FUNDING_INCOMPLETE";
    public const string UnknownStatus = "UNKNOWN_STATUS";
    public const string UnknownPaymentTerm = "UNKNOWN_PAYMENT_TERM";
    public const string MissingPrice = "MISSING_PRICE";
    public const string MissingExpiresAt = "MISSING_EXPIRES_AT";
    public const string MissingAutoProlong = "MISSING_AUTOPROLONG";
    public const string MissingBalance = "MISSING_BALANCE";
    public const string NegativeBalance = "NEGATIVE_BALANCE";
    /// <summary>У признака поведения с весом &gt; 0 нет данных (источник без событий v2 или мало телеметрии): балл — без него</summary>
    public const string BehaviorDataMissing = "BEHAVIOR_DATA_MISSING";
}
