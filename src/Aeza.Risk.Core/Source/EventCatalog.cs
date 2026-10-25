namespace Aeza.Risk.Core.Source;

/// <summary>К чему относится событие</summary>
public enum EventScope
{
    /// <summary>Событие аккаунта: accountId и accountSeq обязательны, порядок — в пределах аккаунта</summary>
    Account,

    /// <summary>Глобальное событие (цены, аварии ДЦ, водяной знак): без accountId</summary>
    Global,
}

/// <summary>Тип доменного события из каталога</summary>
/// <param name="Type">Строка типа: домен.действие</param>
/// <param name="Scope">Аккаунт или глобальное</param>
/// <param name="RequiredState">Запись, которую событие обязано нести в state (S-события); null — поведенческое или глобальное</param>
public sealed record EventTypeInfo(string Type, EventScope Scope, SourceRecordKind? RequiredState);

/// <summary>Каталог доменных событий v2</summary>
public static class EventCatalog
{
    /// <summary>Водяной знак коннектора: все события до data.upTo отправлены</summary>
    public const string Watermark = "stream.watermark";

    private static readonly EventTypeInfo[] All =
    [
        // Аккаунт
        new("account.registered", EventScope.Account, SourceRecordKind.Account),
        new("account.verified", EventScope.Account, SourceRecordKind.Account),
        new("account.security_changed", EventScope.Account, null),
        new("account.login", EventScope.Account, null),
        new("account.closed", EventScope.Account, SourceRecordKind.Account),
        // Деньги
        new("balance.topped_up", EventScope.Account, SourceRecordKind.Account),
        new("payment.charged", EventScope.Account, SourceRecordKind.Transaction),
        new("payment.failed", EventScope.Account, SourceRecordKind.Transaction),
        new("bonus.granted", EventScope.Account, SourceRecordKind.Account),
        new("bonus.expired", EventScope.Account, SourceRecordKind.Account),
        new("withdrawal.requested", EventScope.Account, SourceRecordKind.Account),
        new("renewal.reminder_sent", EventScope.Account, null),
        new("notification.opened", EventScope.Account, null),
        // Жизненный цикл услуги
        new("service.ordered", EventScope.Account, SourceRecordKind.Service),
        new("service.renewed", EventScope.Account, SourceRecordKind.Service),
        new("service.expired", EventScope.Account, SourceRecordKind.Service),
        new("service.suspended", EventScope.Account, SourceRecordKind.Service),
        new("service.resumed", EventScope.Account, SourceRecordKind.Service),
        new("service.blocked", EventScope.Account, SourceRecordKind.Service),
        new("service.unblocked", EventScope.Account, SourceRecordKind.Service),
        new("service.deleted", EventScope.Account, SourceRecordKind.Service),
        new("service.plan_changed", EventScope.Account, SourceRecordKind.Service),
        new("service.term_changed", EventScope.Account, SourceRecordKind.Service),
        new("service.autoprolong_changed", EventScope.Account, SourceRecordKind.Service),
        new("service.reinstalled", EventScope.Account, null),
        new("service.backup_created", EventScope.Account, null),
        new("service.migrated", EventScope.Account, null),
        // Использование
        new("usage.daily", EventScope.Account, null),
        new("service.incident", EventScope.Account, null),
        // Поддержка и опыт
        new("ticket.opened", EventScope.Account, null),
        new("ticket.first_replied", EventScope.Account, null),
        new("ticket.closed", EventScope.Account, null),
        new("nps.responded", EventScope.Account, null),
        // Удержание
        new("retention.offer_sent", EventScope.Account, null),
        new("retention.offer_redeemed", EventScope.Account, null),
        // Глобальные
        new("tariff.price_changed", EventScope.Global, null),
        new("datacenter.incident", EventScope.Global, null),
        new(Watermark, EventScope.Global, null),
    ];

    private static readonly Dictionary<string, EventTypeInfo> ByType = All.ToDictionary(t => t.Type, StringComparer.Ordinal);

    /// <summary>Тип из каталога или null</summary>
    public static EventTypeInfo? Find(string type) => ByType.GetValueOrDefault(type);

    /// <summary>Все известные типы</summary>
    public static IReadOnlyCollection<EventTypeInfo> Known => All;
}

/// <summary>Имена входящих коннекторов (журнал доменных событий, водяной знак)</summary>
public static class ConnectorNames
{
    /// <summary>INGEST_TOKEN</summary>
    public const string HttpPush = "http-push";

    /// <summary>Сессия администратора (панель, Swagger)</summary>
    public const string Panel = "panel";

    /// <summary>Тестовые данные (PUT /source/snapshot)</summary>
    public const string Snapshot = "snapshot";

    /// <summary>Пакетный импорт истории (POST /source/events/import)</summary>
    public const string BatchImport = "batch-import";

    /// <summary>Имя дополнительного коннектора из Auth:IngestConnectors</summary>
    public static bool IsValidCustom(string name) =>
        name.Length is >= 1 and <= 40 && name[0] is >= 'a' and <= 'z' && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
        && name is not (HttpPush or Panel or Snapshot or BatchImport);
}

/// <summary>Доменное событие v2 после проверки</summary>
/// <param name="EventId">Уникален в источнике (идемпотентность)</param>
/// <param name="Type">домен.действие</param>
/// <param name="OccurredAt">Время события в источнике (UTC)</param>
/// <param name="AccountId">Аккаунт; null — глобальное событие</param>
/// <param name="AccountSeq">Номер события аккаунта в источнике</param>
/// <param name="ServiceId">Услуга, если событие про неё</param>
/// <param name="DataJson">Детали события (JSON-объект, без PII)</param>
/// <param name="KnownType">Тип есть в каталоге</param>
/// <param name="WatermarkUpTo">Для stream.watermark — момент, до которого всё отправлено</param>
public sealed record DomainEventInput(
    string EventId, string Type, DateTimeOffset OccurredAt, long? AccountId, long? AccountSeq, long? ServiceId, string DataJson, bool KnownType,
    DateTimeOffset? WatermarkUpTo);

/// <summary>Как номер события аккаунта лёг в последовательность</summary>
public enum SeqObservation
{
    /// <summary>Следующий по порядку (или первый для аккаунта)</summary>
    InOrder,

    /// <summary>Номер больше ожидаемого — образовался пропуск</summary>
    GapOpened,

    /// <summary>Пришёл ранее пропущенный номер</summary>
    GapFilled,

    /// <summary>Номер не больше уже виденного и не из пропусков: повтор с новым eventId или поздняя доставка</summary>
    Late,
}

/// <summary>Состояние последовательности аккаунта: максимальный номер и пропуски</summary>
/// <param name="MaxSeq">Максимальный принятый номер</param>
/// <param name="Missing">Пропущенные номера ниже MaxSeq</param>
public sealed record AccountSeqState(long MaxSeq, IReadOnlyList<long> Missing)
{
    /// <summary>Не больше стольких пропусков храним (дальше — только факт пропуска)</summary>
    public const int MaxTrackedMissing = 1000;

    /// <summary>Учесть номер; чистая функция</summary>
    public static (AccountSeqState State, SeqObservation Observation) Observe(AccountSeqState? current, long seq)
    {
        if (current is null) return (new AccountSeqState(seq, []), SeqObservation.InOrder);
        if (seq == current.MaxSeq + 1) return (current with { MaxSeq = seq }, SeqObservation.InOrder);
        if (seq > current.MaxSeq)
        {
            var missing = new List<long>(current.Missing);
            for (var s = current.MaxSeq + 1; s < seq && missing.Count < MaxTrackedMissing; s++) missing.Add(s);
            return (new AccountSeqState(seq, missing), SeqObservation.GapOpened);
        }
        if (current.Missing.Contains(seq)) return (current with { Missing = current.Missing.Where(m => m != seq).ToList() }, SeqObservation.GapFilled);
        return (current, SeqObservation.Late);
    }
}
