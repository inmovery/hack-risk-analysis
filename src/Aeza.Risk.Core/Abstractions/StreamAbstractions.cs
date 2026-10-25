using Aeza.Risk.Core.Source;

namespace Aeza.Risk.Core.Abstractions;

/// <summary>Кто и как передал события</summary>
/// <param name="Origin">push</param>
/// <param name="Actor">Пользователь или машинный клиент</param>
/// <param name="Synthetic">Пакет от симулятора</param>
/// <param name="Connector">Входящий коннектор: http-push (INGEST_TOKEN), имя из Auth:IngestConnectors, panel (сессия администратора) или batch-import</param>
/// <param name="Rejected">Отклонено событий пакета ещё до приёма (ошибки полей) — в счётчики коннектора</param>
/// <param name="LastError">Первая ошибка пакета: путь поля и код, без значений</param>
/// <param name="DeferRecalc">Не ставить пересчёт (пакетный импорт: один пересчёт в конце)</param>
/// <param name="Chunk">Кусок пакетного импорта: отметить выполненным в той же транзакции, что и приём</param>
/// <param name="SimulationWorld">Ревизия snapshot системы, к которому относится пакет: пакет пересозданного snapshot системы отклоняется</param>
public sealed record IngestMeta(
    string Origin, string Actor, bool Synthetic, string Connector, int Rejected = 0, string? LastError = null, bool DeferRecalc = false,
    ImportChunkRef? Chunk = null, long? SimulationWorld = null);

/// <summary>Пакет относится к уже пересозданному snapshot системы</summary>
/// <param name="current">Текущая ревизия snapshot системы</param>
public sealed class SimulationWorldChangedException(long? current) : Exception($"Snapshot системы симуляции пересоздан: текущая ревизия {current}")
{
    /// <summary>Текущая ревизия snapshot системы</summary>
    public long? Current { get; } = current;
}

/// <summary>Кусок пакетного импорта</summary>
/// <param name="ImportId">Импорт</param>
/// <param name="No">Номер куска</param>
public sealed record ImportChunkRef(long ImportId, int No);

/// <summary>Событие пакета к приёму</summary>
/// <param name="Index">Позиция в пакете</param>
/// <param name="Domain">Доменное событие v2; null — v1</param>
/// <param name="Changes">Изменения записей: v1 — одно (с eventId); v2 — из state (без eventId: дедупликация по доменному событию)</param>
public sealed record IngestItem(int Index, DomainEventInput? Domain, IReadOnlyList<SourceChange> Changes);

/// <summary>Исход одного события</summary>
/// <param name="Index">Позиция в пакете</param>
/// <param name="EventId">Идентификатор события источника</param>
/// <param name="Outcome">applied | no_change | stale | duplicate | rejected | recorded (v2 без state)</param>
/// <param name="Seq">Номер в журнале: v1 — журнала изменений записей, v2 — журнала доменных событий</param>
/// <param name="Reason">Причина stale/rejected</param>
/// <param name="Changes">Коды изменений</param>
public sealed record IngestItemResult(int Index, string? EventId, string Outcome, long? Seq, string? Reason, IReadOnlyList<string> Changes);

/// <summary>Итог приёма пакета</summary>
/// <param name="Received">Событий в пакете (включая отклонённые)</param>
/// <param name="Applied">Изменили состояние</param>
/// <param name="NoChange">То же состояние</param>
/// <param name="Stale">Устаревшие (правила монотонности)</param>
/// <param name="Duplicates">Повтор eventId</param>
/// <param name="Recorded">Поведенческие события v2 (без state): записаны в журнал доменных событий</param>
/// <param name="UnknownTypes">События v2 с типом не из каталога (приняты, отмечены в качестве данных)</param>
/// <param name="LastSeq">Водяной знак журнала изменений после приёма</param>
/// <param name="JobId">Поставленный пересчёт (если были применённые)</param>
/// <param name="Results">Исходы по событиям</param>
/// <param name="ChangeCounts">Сколько раз встретился каждый код изменения</param>
public sealed record IngestBatchResult(
    int Received, int Applied, int NoChange, int Stale, int Duplicates, int Recorded, int UnknownTypes, long LastSeq, long? JobId,
    IReadOnlyList<IngestItemResult> Results, IReadOnlyDictionary<string, int> ChangeCounts)
{
    /// <summary>Пустой итог (в пакете нет корректных событий)</summary>
    public static IngestBatchResult Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, null, [], new Dictionary<string, int>());
}

/// <summary>Единая точка приёма событий источника</summary>
public interface ISourceIngestion
{
    /// <summary>Применить события в порядке списка</summary>
    Task<IngestBatchResult> IngestAsync(IReadOnlyList<IngestItem> items, IngestMeta meta, CancellationToken cancellationToken);
}

/// <summary>Запись журнала изменений записей (без состояния записи)</summary>
/// <param name="Seq">Номер в журнале</param>
/// <param name="ReceivedAt">Время получения сервером</param>
/// <param name="OccurredAt">Время события: v2 — occurredAt источника, v1 — время получения</param>
/// <param name="EventType">Доменный тип v2 (service.renewed…); null — v1</param>
/// <param name="Kind">account | service | transaction</param>
/// <param name="EntityId">Идентификатор записи</param>
/// <param name="OwnerId">Клиент (если известен)</param>
/// <param name="Outcome">applied | no_change | stale</param>
/// <param name="StaleReason">Причина stale</param>
/// <param name="Changes">Коды изменений</param>
/// <param name="Effect">Значения «от → до» без PII</param>
/// <param name="Origin">push</param>
/// <param name="Synthetic">События симулятора</param>
public sealed record JournalItem(
    long Seq, DateTimeOffset ReceivedAt, DateTimeOffset OccurredAt, string? EventType, string Kind, long EntityId, long? OwnerId, string Outcome,
    string? StaleReason, IReadOnlyList<string> Changes, IReadOnlyDictionary<string, object?> Effect, string Origin, bool Synthetic);

/// <summary>Покрытие журнала</summary>
/// <param name="StreamStartedAt">Первое событие (null — событий не было)</param>
/// <param name="LastEventAt">Последнее событие</param>
/// <param name="SourceUpdatedAt">Последнее изменение источника (событие или снимок)</param>
/// <param name="Events">Событий в журнале</param>
/// <param name="Synthetic">Есть события симулятора</param>
public sealed record StreamCoverage(DateTimeOffset? StreamStartedAt, DateTimeOffset? LastEventAt, DateTimeOffset? SourceUpdatedAt, long Events, bool Synthetic);

/// <summary>Факты за день события</summary>
public sealed record HistoryDay(
    DateOnly Date, int Renewed, int ToProlongWait, int Deleted, int Blocked, int CustomersLostAll, long PaymentsMinor, int Payments, long TopUpsMinor, int TopUps);

/// <summary>Исходы по периоду оплаты</summary>
public sealed record TermOutcomes(int Renewed, int NotRenewed);

/// <summary>Исходы сроков (непродление за N дней)</summary>
public sealed record ExpiryOutcomeStats(
    int GraceDays, int Renewed, int NotRenewed, int Excluded, int Pending, IReadOnlyDictionary<string, TermOutcomes> ByTerm, decimal? NotRenewedRatePct);

/// <summary>Счётчики приёма для страницы источника</summary>
/// <param name="Last24h">Исходы за 24 ч</param>
/// <param name="EventsSinceBase">Событий после базового снимка</param>
/// <param name="OrphanServices">Услуги без аккаунта</param>
/// <param name="OrphanTransactions">Транзакции без услуги</param>
/// <param name="DomainEvents24h">Доменных событий v2 получено за 24 ч</param>
/// <param name="UnknownTypes24h">Из них с типом не из каталога</param>
/// <param name="AccountsWithGaps">Аккаунтов с пропусками в accountSeq</param>
/// <param name="OldestOrphanAt">С какого момента ждёт аккаунта самая давняя услуга без аккаунта («парковка»)</param>
public sealed record StreamCounters(
    IReadOnlyDictionary<string, int> Last24h, long EventsSinceBase, int OrphanServices, int OrphanTransactions,
    int DomainEvents24h, int UnknownTypes24h, int AccountsWithGaps, DateTimeOffset? OldestOrphanAt);

/// <summary>Входящий коннектор источника</summary>
/// <param name="Name">http-push, имя из Auth:IngestConnectors или panel</param>
/// <param name="Watermark">Все события до этого момента отправлены (stream.watermark)</param>
/// <param name="LastReceivedAt">Последнее полученное событие</param>
/// <param name="LastOccurredAt">Время события самого свежего из полученных</param>
/// <param name="Events">Принято доменных событий всего</param>
/// <param name="Rejected">Отклонено событий всего</param>
/// <param name="Duplicates">Повторов eventId всего</param>
/// <param name="LastError">Последняя ошибка приёма (путь поля, без значений)</param>
/// <param name="LastErrorAt">Когда</param>
public sealed record ConnectorState(
    string Name, DateTimeOffset? Watermark, DateTimeOffset? LastReceivedAt, DateTimeOffset? LastOccurredAt, long Events, long Rejected, long Duplicates,
    string? LastError, DateTimeOffset? LastErrorAt);

/// <summary>Чтение журнала, фактов и состояния приёма</summary>
public interface ISourceStreamRepository
{
    /// <summary>Журнал: новые сверху; beforeSeq — страница старше</summary>
    Task<IReadOnlyList<JournalItem>> ListAsync(long? beforeSeq, int limit, string? kind, string? outcome, long? ownerId, CancellationToken cancellationToken);

    /// <summary>Покрытие журнала</summary>
    Task<StreamCoverage> GetCoverageAsync(CancellationToken cancellationToken);

    /// <summary>Факты по дням события начиная с <paramref name="from"/></summary>
    Task<IReadOnlyList<HistoryDay>> GetDailyAsync(DateTimeOffset from, CancellationToken cancellationToken);

    /// <summary>Исходы сроков</summary>
    Task<ExpiryOutcomeStats> GetOutcomeStatsAsync(int graceDays, CancellationToken cancellationToken);

    /// <summary>Счётчики приёма</summary>
    Task<StreamCounters> GetCountersAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Входящие коннекторы: водяной знак, последнее событие</summary>
    Task<IReadOnlyList<ConnectorState>> GetConnectorsAsync(CancellationToken cancellationToken);
}
