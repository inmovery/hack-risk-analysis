using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aeza.Risk.Core.Source;

/// <summary>Разобранное событие пакета</summary>
/// <param name="Index">Позиция в пакете</param>
/// <param name="EventId">Идентификатор события источника (если есть)</param>
/// <param name="Domain">Доменное событие v2; null — формат v1</param>
/// <param name="Changes">Изменения записей: v1 — одно; v2 — записи из state (аккаунт, услуга, транзакция) или ни одной</param>
/// <param name="Errors">Ошибки по путям (events[i].data.field) — без значений</param>
public sealed record ParsedEvent(
    int Index, string? EventId, DomainEventInput? Domain, IReadOnlyList<SourceChange> Changes, IReadOnlyDictionary<string, string[]> Errors)
{
    /// <summary>Событие можно применять</summary>
    public bool IsValid => Errors.Count == 0 && (Domain is not null || Changes.Count > 0);
}

/// <summary>Разобранный пакет событий</summary>
/// <param name="Synthetic">Пакет помечен источником как синтетика (симулятор)</param>
/// <param name="Events">События в порядке пакета</param>
/// <param name="EnvelopeErrors">Ошибки формы пакета (весь пакет отклоняется)</param>
/// <param name="Warnings">Счётчики предупреждений качества</param>
public sealed record ParsedEventBatch(
    bool Synthetic, IReadOnlyList<ParsedEvent> Events, IReadOnlyDictionary<string, string[]> EnvelopeErrors, IReadOnlyDictionary<string, int> Warnings)
{
    /// <summary>Форма пакета корректна</summary>
    public bool IsValid => EnvelopeErrors.Count == 0;
}

/// <summary>Пакет событий <c>{ synthetic?, events: [...] }</c> (контракт определяем сами)</summary>
public static partial class EventBatchParser
{
    /// <summary>Не больше событий в пакете</summary>
    public const int MaxEvents = 1000;

    /// <summary>Длина eventId</summary>
    public const int MaxEventIdLength = 128;

    /// <summary>Длина типа события</summary>
    public const int MaxTypeLength = 64;

    /// <summary>Насколько occurredAt может опережать часы сервера (рассинхронизация часов источника)</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex TypePattern();

    /// <summary>Разобрать тело запроса; <paramref name="now"/> — для проверки occurredAt из будущего (v2)</summary>
    public static ParsedEventBatch Parse(ReadOnlySpan<byte> utf8, DateTimeOffset? now = null)
    {
        var envelope = new Dictionary<string, string[]>();
        var warnings = new Dictionary<string, int>();
        void Warn(string code) => warnings[code] = warnings.GetValueOrDefault(code) + 1;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            envelope["$"] = ["Тело не является корректным JSON."];
            return new ParsedEventBatch(false, [], envelope, warnings);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                envelope["$"] = ["Корень — объект { synthetic?, events: [...] }."];
                return new ParsedEventBatch(false, [], envelope, warnings);
            }
            var synthetic = root.TryGetProperty("synthetic", out var syn) && syn.ValueKind == JsonValueKind.True;
            if (!root.TryGetProperty("events", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                envelope["events"] = ["Обязательный массив events."];
                return new ParsedEventBatch(synthetic, [], envelope, warnings);
            }
            if (arr.GetArrayLength() > MaxEvents)
            {
                envelope["events"] = [$"Не более {MaxEvents} событий в пакете."];
                return new ParsedEventBatch(synthetic, [], envelope, warnings);
            }

            var result = new List<ParsedEvent>(arr.GetArrayLength());
            var i = 0;
            foreach (var e in arr.EnumerateArray())
            {
                result.Add(ParseOne(e, i, Warn, now));
                i++;
            }
            return new ParsedEventBatch(synthetic, result, envelope, warnings);
        }
    }

    private static ParsedEvent ParseOne(JsonElement e, int index, Action<string> warn, DateTimeOffset? now)
    {
        var p = $"events[{index}]";
        var errors = new Dictionary<string, List<string>>();
        void Err(string path, string code, string message)
        {
            if (!errors.TryGetValue(path, out var l)) errors[path] = l = new List<string>();
            l.Add(message);
        }
        ParsedEvent Fail(string? id) => new(index, id, null, [], errors.ToDictionary(k => k.Key, k => k.Value.ToArray()));

        if (e.ValueKind != JsonValueKind.Object) { Err(p, "invalid_type", "Событие — объект."); return Fail(null); }
        var eventId = ReadEventId(e, p, Err, out var idInvalid);
        if (idInvalid) return Fail(null);

        if (e.TryGetProperty("schemaVersion", out var ver) && ver.ValueKind != JsonValueKind.Null)
        {
            if (ver.ValueKind == JsonValueKind.Number && ver.TryGetInt32(out var v) && v == 2) return ParseV2(e, index, p, eventId, Err, Fail, warn, now);
            if (!(ver.ValueKind == JsonValueKind.Number && ver.TryGetInt32(out var v1) && v1 == 1))
            {
                Err($"{p}.schemaVersion", "invalid_schema_version", "schemaVersion: 1 (полное состояние записи) или 2 (доменное событие).");
                return Fail(eventId);
            }
        }

        // v1: полное состояние одной записи
        var kind = e.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String ? SourceCodes.ParseKind(typeEl.GetString()) : null;
        if (kind is null) { Err($"{p}.type", "invalid_type", "type: account, service или transaction (v1) либо schemaVersion: 2 с доменным типом."); return Fail(eventId); }
        if (!e.TryGetProperty("data", out var data)) { Err($"{p}.data", "missing_field", "Обязательное поле data — полное состояние записи."); return Fail(eventId); }
        foreach (var prop in e.EnumerateObject())
            if (prop.Name is not ("eventId" or "type" or "data" or "schemaVersion")) warn("unknown_event_field");

        var change = ReadRecord(kind.Value, data, $"{p}.data", eventId, Err, warn);
        return errors.Count > 0 || change is null ? Fail(eventId) : new ParsedEvent(index, eventId, null, [change], new Dictionary<string, string[]>());
    }

    private static ParsedEvent ParseV2(
        JsonElement e, int index, string p, string? eventId, Action<string, string, string> err, Func<string?, ParsedEvent> fail, Action<string> warn,
        DateTimeOffset? now)
    {
        if (eventId is null) err($"{p}.eventId", "missing_field", "eventId обязателен в schemaVersion 2 (идемпотентность).");

        string? type = null;
        if (!e.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            err($"{p}.type", "missing_field", "type — строка домен.действие (например service.renewed).");
        else
        {
            type = typeEl.GetString()!;
            if (type.Length > MaxTypeLength || !TypePattern().IsMatch(type))
                err($"{p}.type", "invalid_type", $"type — домен.действие: строчные латинские буквы, цифры и _, до {MaxTypeLength} символов.");
        }

        DateTimeOffset? occurredAt = null;
        if (!e.TryGetProperty("occurredAt", out var occEl) || occEl.ValueKind == JsonValueKind.Null)
            err($"{p}.occurredAt", "missing_field", "occurredAt обязателен: время события в источнике (ISO 8601 с часовым поясом).");
        else
        {
            occurredAt = SnapshotParser.ReadDate(occEl, $"{p}.occurredAt", err);
            if (occurredAt is { } o && now is { } n && o > n + FutureTolerance)
                err($"{p}.occurredAt", "occurred_in_future", "occurredAt в будущем: часы источника опережают сервер больше чем на 5 минут.");
        }

        var accountId = ReadOptionalId(e, "accountId", p, err);
        var accountSeq = ReadOptionalId(e, "accountSeq", p, err);
        var serviceId = ReadOptionalId(e, "serviceId", p, err);

        var info = type is null ? null : EventCatalog.Find(type);
        var scope = info?.Scope ?? (accountId is null ? EventScope.Global : EventScope.Account);
        if (scope == EventScope.Account)
        {
            // Задано, но некорректно — ошибка уже есть (invalid_id); здесь — только отсутствующее или null
            if (accountId is null && !Present(e, "accountId")) err($"{p}.accountId", "missing_field", "accountId обязателен для события аккаунта.");
            if (accountSeq is null && !Present(e, "accountSeq"))
                err($"{p}.accountSeq", "missing_field", "accountSeq обязателен для события аккаунта: номер события в пределах аккаунта, растёт на 1.");
        }
        else if (accountId is not null)
            err($"{p}.accountId", "unexpected_field", "У глобального события нет accountId.");

        if (!e.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            err($"{p}.data", "missing_field", "data — объект с деталями события (может быть пустым {}).");

        DateTimeOffset? upTo = null;
        if (type == EventCatalog.Watermark && data.ValueKind == JsonValueKind.Object)
        {
            if (!data.TryGetProperty("upTo", out var upEl) || upEl.ValueKind == JsonValueKind.Null)
                err($"{p}.data.upTo", "missing_field", "upTo обязателен: момент, до которого все события отправлены.");
            else upTo = SnapshotParser.ReadDate(upEl, $"{p}.data.upTo", err);
            // Источник не может подтвердить полноту позже момента, когда он это подтверждает
            if (upTo is { } u && occurredAt is { } at && u > at)
                err($"{p}.data.upTo", "watermark_ahead", "upTo не может быть позже occurredAt самого события stream.watermark.");
        }

        var changes = new List<SourceChange>();
        var stateErrors = 0;
        void StateErr(string path, string code, string message)
        {
            stateErrors++;
            err(path, code, message);
        }
        if (e.TryGetProperty("state", out var state) && state.ValueKind != JsonValueKind.Null)
        {
            if (state.ValueKind != JsonValueKind.Object)
                StateErr($"{p}.state", "invalid_type", "state — объект { account?, service?, transaction? } с полным состоянием записей.");
            else
            {
                // Порядок применения: аккаунт → услуга → транзакция (услуга не станет «без аккаунта», транзакция — «без услуги»)
                foreach (var kind in new[] { SourceRecordKind.Account, SourceRecordKind.Service, SourceRecordKind.Transaction })
                    if (state.TryGetProperty(kind.ToApi(), out var rec) && rec.ValueKind != JsonValueKind.Null
                        && ReadRecord(kind, rec, $"{p}.state.{kind.ToApi()}", null, StateErr, warn) is { } c)
                        changes.Add(c);
                foreach (var prop in state.EnumerateObject())
                    if (prop.Name is not ("account" or "service" or "transaction")) warn("unknown_state_field");
            }
        }
        foreach (var c in changes)
        {
            if (c.Account is { } a && accountId is { } aid && a.Id != aid)
                err($"{p}.state.account.id", "state_mismatch", "state.account.id не совпадает с accountId события.");
            if (c.Service is { } s)
            {
                if (accountId is { } owner && s.OwnerId != owner) err($"{p}.state.service.ownerId", "state_mismatch", "state.service.ownerId не совпадает с accountId события.");
                if (serviceId is { } sid && s.Id != sid) err($"{p}.state.service.id", "state_mismatch", "state.service.id не совпадает с serviceId события.");
            }
        }
        // Битая запись в state уже дала ошибку по своим полям — «обязателен» не дублируем
        if (info?.RequiredState is { } required && changes.All(c => c.Kind != required) && stateErrors == 0)
            err($"{p}.state.{required.ToApi()}", "missing_field", $"Событие {type} меняет запись: state.{required.ToApi()} обязателен (полное состояние после события).");

        foreach (var prop in e.EnumerateObject())
            if (prop.Name is not ("schemaVersion" or "eventId" or "type" or "occurredAt" or "accountId" or "accountSeq" or "serviceId" or "data" or "state"))
                warn("unknown_event_field");
        if (info is null && type is not null) warn("unknown_event_type");

        var invalid = fail(eventId);
        if (invalid.Errors.Count > 0) return invalid;
        var domain = new DomainEventInput(eventId!, type!, occurredAt!.Value, accountId, accountSeq, serviceId, data.GetRawText(), info is not null, upTo);
        return new ParsedEvent(index, eventId, domain, changes, new Dictionary<string, string[]>());
    }

    private static string? ReadEventId(JsonElement e, string p, Action<string, string, string> err, out bool invalid)
    {
        invalid = false;
        if (!e.TryGetProperty("eventId", out var idEl) || idEl.ValueKind == JsonValueKind.Null) return null;
        var eventId = idEl.ValueKind switch
        {
            JsonValueKind.String => idEl.GetString(),
            JsonValueKind.Number => idEl.GetRawText(),
            _ => null,
        };
        if (!string.IsNullOrEmpty(eventId) && eventId.Length <= MaxEventIdLength) return eventId;
        err($"{p}.eventId", "invalid_event_id", $"eventId — непустая строка до {MaxEventIdLength} символов.");
        invalid = true;
        return null;
    }

    private static bool Present(JsonElement e, string field) => e.TryGetProperty(field, out var v) && v.ValueKind != JsonValueKind.Null;

    private static long? ReadOptionalId(JsonElement e, string field, string p, Action<string, string, string> err)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var id) && id >= 1) return id;
        err($"{p}.{field}", "invalid_id", $"{field} — целое число ≥ 1.");
        return null;
    }

    private static SourceChange? ReadRecord(SourceRecordKind kind, JsonElement data, string path, string? eventId, Action<string, string, string> err, Action<string> warn) =>
        kind switch
        {
            SourceRecordKind.Account => SnapshotParser.ReadAccount(data, path, err, warn) is { } a ? SourceChange.Of(a, eventId) : null,
            SourceRecordKind.Service => SnapshotParser.ReadService(data, path, err, warn) is { } s ? SourceChange.Of(s, eventId) : null,
            _ => SnapshotParser.ReadTransaction(data, path, err, warn) is { } t ? SourceChange.Of(t, eventId) : null,
        };
}
