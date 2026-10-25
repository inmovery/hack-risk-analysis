namespace Aeza.Risk.Core.Source;

/// <summary>Тип записи источника в событии (стабильные строки API: account, service, transaction)</summary>
public enum SourceRecordKind
{
    /// <summary>Аккаунт клиента</summary>
    Account,

    /// <summary>Услуга</summary>
    Service,

    /// <summary>Транзакция</summary>
    Transaction,
}

/// <summary>Исход применения события к текущему состоянию</summary>
public enum ChangeOutcome
{
    /// <summary>Состояние изменено</summary>
    Applied,

    /// <summary>Состояние совпадает с текущим (повтор без eventId)</summary>
    NoChange,

    /// <summary>Нарушает монотонность — устаревшее, отброшено</summary>
    Stale,
}

/// <summary>Стабильные строки исходов, типов записей и кодов изменений (API, БД, метрики)</summary>
public static class SourceCodes
{
    public const string Applied = "applied";
    public const string NoChange = "no_change";
    public const string Stale = "stale";
    public const string Duplicate = "duplicate";
    public const string Rejected = "rejected";

    /// <summary>Поведенческое событие v2 без state: записано в журнал доменных событий, состояние не меняет</summary>
    public const string Recorded = "recorded";

    public const string OriginPush = "push";

    public static string ToApi(this ChangeOutcome o) => o switch
    {
        ChangeOutcome.Applied => Applied,
        ChangeOutcome.NoChange => NoChange,
        _ => Stale,
    };

    public static string ToApi(this SourceRecordKind k) => k switch
    {
        SourceRecordKind.Account => "account",
        SourceRecordKind.Service => "service",
        _ => "transaction",
    };

    public static SourceRecordKind? ParseKind(string? raw) => raw switch
    {
        "account" => SourceRecordKind.Account,
        "service" => SourceRecordKind.Service,
        "transaction" => SourceRecordKind.Transaction,
        _ => null,
    };
}

/// <summary>Коды изменений — факты для журнала, истории и метрик (без PII)</summary>
public static class ChangeCodes
{
    public const string ServiceCreated = "service_created";
    public const string Renewed = "renewed";
    public const string StatusChanged = "status_changed";
    public const string ToProlongWait = "to_prolong_wait";
    public const string Deleted = "deleted";
    public const string Blocked = "blocked";
    public const string PriceChanged = "price_changed";
    public const string AutoProlongChanged = "autoprolong_changed";
    public const string TermChanged = "term_changed";
    public const string OwnerChanged = "owner_changed";
    public const string OwnerUnknown = "owner_unknown";
    public const string CustomerLostAllServices = "customer_lost_all_services";

    public const string AccountCreated = "account_created";
    public const string BalanceChanged = "balance_changed";
    public const string ToppedUp = "topped_up";
    public const string BonusChanged = "bonus_changed";
    public const string BonusStateChanged = "bonus_state_changed";
    public const string WithdrawChanged = "withdraw_changed";
    public const string EmailChanged = "email_changed";

    public const string TransactionCreated = "transaction_created";
    public const string TransactionUpdated = "transaction_updated";
    public const string PaymentPerformed = "payment_performed";
    public const string ServiceUnknown = "service_unknown";
}

/// <summary>Причины отбрасывания устаревшего события</summary>
public static class StaleReasons
{
    public const string DeletedIsTerminal = "deleted_is_terminal";
    public const string ExpiresRegressed = "expires_regressed";
    public const string TotalReplenishedRegressed = "total_replenished_regressed";
    public const string PerformedIsTerminal = "performed_is_terminal";

    /// <summary>v2: событие произошло не позже даты базы (тестовых данных) — его результат уже в базе</summary>
    public const string BeforeBase = "before_base";
}

/// <summary>Изменение одной записи источника: полное состояние. v1 — само событие; v2 — запись из state доменного события</summary>
/// <param name="Kind">Тип записи</param>
/// <param name="EventId">Идентификатор события источника (не гарантирован)</param>
/// <param name="Account">Состояние аккаунта (для Kind = Account)</param>
/// <param name="Service">Состояние услуги (для Kind = Service)</param>
/// <param name="Transaction">Состояние транзакции (для Kind = Transaction)</param>
public sealed record SourceChange(
    SourceRecordKind Kind, string? EventId, AccountInput? Account = null, ServiceInput? Service = null, TransactionInput? Transaction = null)
{
    /// <summary>Идентификатор записи</summary>
    public long EntityId => Kind switch
    {
        SourceRecordKind.Account => Account!.Id,
        SourceRecordKind.Service => Service!.Id,
        _ => Transaction!.Id,
    };

    /// <summary>Событие аккаунта</summary>
    public static SourceChange Of(AccountInput a, string? eventId = null) => new(SourceRecordKind.Account, eventId, Account: a);

    /// <summary>Событие услуги</summary>
    public static SourceChange Of(ServiceInput s, string? eventId = null) => new(SourceRecordKind.Service, eventId, Service: s);

    /// <summary>Событие транзакции</summary>
    public static SourceChange Of(TransactionInput t, string? eventId = null) => new(SourceRecordKind.Transaction, eventId, Transaction: t);
}

/// <summary>Результат применения события</summary>
/// <param name="Outcome">Исход</param>
/// <param name="StaleReason">Причина для <see cref="ChangeOutcome.Stale"/></param>
/// <param name="Changes">Коды изменений</param>
/// <param name="Effect">Значения «от → до» для изменённых полей (без email и имён)</param>
/// <param name="OwnerId">Клиент, к которому относится запись (если известен)</param>
public sealed record ChangeResult(
    ChangeOutcome Outcome, string? StaleReason, IReadOnlyList<string> Changes, IReadOnlyDictionary<string, object?> Effect, long? OwnerId)
{
    /// <summary>Устаревшее событие</summary>
    public static ChangeResult StaleBy(string reason, long? owner) => new(ChangeOutcome.Stale, reason, [], new Dictionary<string, object?>(), owner);

    /// <summary>Без изменений</summary>
    public static ChangeResult Same(long? owner) => new(ChangeOutcome.NoChange, null, [], new Dictionary<string, object?>(), owner);
}
