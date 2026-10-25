using Aeza.Risk.Core.Abstractions;

using JetBrains.Annotations;

namespace Aeza.Risk.Feature.Source.Events;

/// <summary>Документационная форма пакета событий: v1 (полное состояние записи) и v2 (доменное событие) можно смешивать</summary>
/// <param name="Synthetic">Пакет — синтетика (симулятор, демонстрация)</param>
/// <param name="Events">События в порядке возникновения (не больше 1000)</param>
[PublicAPI]
public sealed record EventBatchRequest(bool? Synthetic, IReadOnlyList<SourceEventRequest> Events);

/// <summary>Событие источника</summary>
/// <param name="SchemaVersion">2 — доменное событие; нет или 1 — полное состояние записи</param>
/// <param name="EventId">v2: обязателен (идемпотентность). v1: необязателен; если задан — дедупликация</param>
/// <param name="Type">v2: домен.действие из каталога (service.renewed, payment.failed, usage.daily, stream.watermark…)</param>
/// <param name="OccurredAt">v2: время события в источнике (ISO 8601 с поясом), не позже чем через 5 минут от часов сервера</param>
/// <param name="AccountId">v2: аккаунт; обязателен для событий аккаунта, нет у глобальных</param>
/// <param name="AccountSeq">v2: номер события аккаунта в источнике, растёт на 1 (пропуск виден в качестве данных)</param>
/// <param name="ServiceId">v2: услуга, если событие про неё</param>
/// <param name="Data">v2: детали события (объект, без PII). v1: полное состояние записи в формате тестовых данных</param>
/// <param name="State">v2: полное состояние изменённых записей после события { account?, service?, transaction? } в формате тестовых данных</param>
[PublicAPI]
public sealed record SourceEventRequest(
    int? SchemaVersion, string? EventId, string Type, DateTimeOffset? OccurredAt, long? AccountId, long? AccountSeq, long? ServiceId, object Data,
    object? State);

/// <summary>Итог приёма пакета</summary>
/// <param name="Received">Событий в пакете</param>
/// <param name="Applied">Изменили состояние</param>
/// <param name="NoChange">То же состояние</param>
/// <param name="Stale">Устаревшие (правила монотонности)</param>
/// <param name="Duplicates">Повтор eventId</param>
/// <param name="Rejected">Ошибки полей</param>
/// <param name="Recorded">Поведенческие события v2 без state: записаны в журнал доменных событий</param>
/// <param name="UnknownTypes">События v2 с типом не из каталога</param>
/// <param name="LastSeq">Водяной знак журнала изменений</param>
/// <param name="JobId">Поставленный пересчёт</param>
/// <param name="Results">Исходы по событиям</param>
/// <param name="Errors">Ошибки по путям полей</param>
[PublicAPI]
public sealed record EventBatchResponse(
    int Received, int Applied, int NoChange, int Stale, int Duplicates, int Rejected, int Recorded, int UnknownTypes, long LastSeq, long? JobId,
    IReadOnlyList<IngestItemResult> Results, IReadOnlyDictionary<string, string[]> Errors);

/// <summary>Запись журнала изменений записей</summary>
/// <param name="Seq">Номер в журнале</param>
/// <param name="ReceivedAt">Время получения</param>
/// <param name="OccurredAt">Время события: v2 — occurredAt источника, v1 — время получения</param>
/// <param name="EventType">Доменный тип v2; null — v1</param>
/// <param name="Kind">account | service | transaction</param>
/// <param name="EntityId">Идентификатор записи</param>
/// <param name="OwnerId">Клиент</param>
/// <param name="Outcome">applied | no_change | stale</param>
/// <param name="StaleReason">Причина stale</param>
/// <param name="Changes">Коды изменений</param>
/// <param name="Effect">Значения «от → до» без PII</param>
/// <param name="Origin">push</param>
/// <param name="Synthetic">Синтетика</param>
[PublicAPI]
public sealed record JournalItemDto(
    long Seq, DateTimeOffset ReceivedAt, DateTimeOffset OccurredAt, string? EventType, string Kind, long EntityId, long? OwnerId, string Outcome,
    string? StaleReason, IReadOnlyList<string> Changes, IReadOnlyDictionary<string, object?> Effect, string Origin, bool Synthetic);

/// <summary>Страница журнала (новые сверху)</summary>
/// <param name="Items">События</param>
/// <param name="NextBeforeSeq">Для следующей (более старой) страницы — beforeSeq; null — конец</param>
[PublicAPI]
public sealed record JournalPageDto(IReadOnlyList<JournalItemDto> Items, long? NextBeforeSeq);

/// <summary>Входящий коннектор источника (исходящих запросов к источнику нет)</summary>
/// <param name="Key">http-push (INGEST_TOKEN), batch-import (импорт истории), snapshot (тестовые данные), panel (админ), имя из Auth:IngestConnectors</param>
/// <param name="Enabled">Настроен (для http-push и именованных — задан токен)</param>
/// <param name="Status">ok | idle | silent</param>
/// <param name="LastReceivedAt">Последнее полученное событие (для snapshot — последняя загрузка)</param>
/// <param name="LastOccurredAt">Время события самого свежего из полученных</param>
/// <param name="Watermark">Источник подтвердил: все события до этого момента отправлены</param>
/// <param name="LagSeconds">Задержка: сейчас − время события самого свежего из полученных</param>
/// <param name="Events">Принято событий всего</param>
/// <param name="Rejected">Отклонено событий всего (ошибки полей)</param>
/// <param name="Duplicates">Повторов eventId всего</param>
/// <param name="LastError">Последняя ошибка: путь поля, без значений</param>
/// <param name="LastErrorAt">Когда была последняя ошибка</param>
/// <param name="LastImport">batch-import: последний импорт</param>
[PublicAPI]
public sealed record ConnectorDto(
    string Key, bool Enabled, string Status, DateTimeOffset? LastReceivedAt, DateTimeOffset? LastOccurredAt, DateTimeOffset? Watermark, long? LagSeconds, long Events,
    long Rejected, long Duplicates, string? LastError, DateTimeOffset? LastErrorAt, ImportView? LastImport);

/// <summary>Тестовые данные — база текущего состояния</summary>
[PublicAPI]
public sealed record StreamBaseDto(long Revision, DateTimeOffset SourceAsOf, bool Synthetic);

/// <summary>Качество потока v2</summary>
/// <param name="DomainEvents24h">Доменных событий получено за 24 ч</param>
/// <param name="UnknownTypes24h">Из них с типом не из каталога</param>
/// <param name="AccountsWithGaps">Аккаунтов с пропусками в accountSeq (потерянные события)</param>
/// <param name="OldestOrphanAt">С какого момента ждёт аккаунта самая давняя «запаркованная» услуга</param>
[PublicAPI]
public sealed record StreamQualityDto(int DomainEvents24h, int UnknownTypes24h, int AccountsWithGaps, DateTimeOffset? OldestOrphanAt);

/// <summary>Состояние приёма источника</summary>
[PublicAPI]
public sealed record StreamStatusDto(
    StreamBaseDto? Base, long Seq, DateTimeOffset? SourceUpdatedAt, DateTimeOffset? StreamStartedAt, DateTimeOffset? LastEventAt,
    long EventsTotal, long EventsSinceBase, IReadOnlyDictionary<string, int> Last24h, int OrphanServices, int OrphanTransactions,
    bool Synthetic, bool Silent, int SilentAfterMinutes, StreamQualityDto Quality, IReadOnlyList<ConnectorDto> Connectors);

internal static class JournalMapping
{
    public static JournalItemDto ToDto(this JournalItem e) =>
        new(e.Seq, e.ReceivedAt, e.OccurredAt, e.EventType, e.Kind, e.EntityId, e.OwnerId, e.Outcome, e.StaleReason, e.Changes, e.Effect, e.Origin, e.Synthetic);
}
