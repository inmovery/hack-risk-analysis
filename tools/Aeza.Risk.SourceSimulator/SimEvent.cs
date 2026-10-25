using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aeza.Risk.SourceSimulator;

/// <summary>Доменное событие v2 в том виде, в каком его отправляет источник</summary>
public sealed record SimEvent(
    string EventId, string Type, DateTimeOffset OccurredAt, long? AccountId, long? AccountSeq, long? ServiceId, JsonObject Data, JsonObject? State)
{
    /// <summary>Одна строка JSONL — ровно то, что уходит в POST /api/v1/source/events</summary>
    public string ToJsonLine()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = Iso.Compact.Encoder }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 2);
            w.WriteString("eventId", EventId);
            w.WriteString("type", Type);
            w.WriteString("occurredAt", Iso.Format(OccurredAt));
            if (AccountId is { } a) w.WriteNumber("accountId", a);
            if (AccountSeq is { } s) w.WriteNumber("accountSeq", s);
            if (ServiceId is { } sid) w.WriteNumber("serviceId", sid);
            w.WritePropertyName("data");
            Data.WriteTo(w);
            if (State is not null)
            {
                w.WritePropertyName("state");
                State.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>Состояние записей в формате тестовых данных (оригинальные имена полей)</summary>
public static class Records
{
    public static JsonObject Account(AccountRec a) => new()
    {
        ["id"] = a.Id,
        ["email"] = a.Email,
        ["balance"] = a.Balance,
        ["withdrawBalance"] = a.WithdrawBalance,
        ["totalReplenished"] = a.TotalReplenished,
        ["bonusBalance"] = a.BonusBalance,
        ["bonusState"] = a.BonusState,
    };

    public static JsonObject Service(ServiceRec s) => new()
    {
        ["id"] = s.Id,
        ["name"] = s.Name,
        ["ownerId"] = s.OwnerId,
        ["paymentTerm"] = s.PaymentTerm,
        ["autoProlong"] = s.AutoProlong,
        ["createdAt"] = Iso.Format(s.CreatedAt),
        ["expiresAt"] = Iso.Format(s.ExpiresAt),
        ["status"] = s.Status,
        ["price"] = s.Price,
    };

    public static JsonObject Transaction(long id, long serviceId, long amount, bool performed, DateTimeOffset at, string type) => new()
    {
        ["id"] = id,
        ["amount"] = amount,
        ["status"] = performed ? "performed" : "not_performed",
        ["performedAt"] = performed ? Iso.Format(at) : null,
        ["createdAt"] = Iso.Format(at),
        ["type"] = type,
        ["serviceId"] = serviceId,
    };

    public static JsonObject State(JsonObject? account = null, JsonObject? service = null, JsonObject? transaction = null)
    {
        var o = new JsonObject();
        if (account is not null) o["account"] = account;
        if (service is not null) o["service"] = service;
        if (transaction is not null) o["transaction"] = transaction;
        return o;
    }
}

public static class Iso
{
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Format(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static string Date(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>Время с точностью до секунды: так оно одинаково в памяти, в событии и в файле состояния</summary>
    public static DateTimeOffset Sec(DateTimeOffset d) => new(d.UtcTicks - d.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    public static DateTimeOffset DayStart(DateTimeOffset d) => new(d.UtcDateTime.Date, TimeSpan.Zero);
}

/// <summary>Куда идут события</summary>
public interface IEventSink
{
    void Write(SimEvent e);
}

/// <summary>Куда идёт «истина» (скрытое состояние) — только для проверки ML, в анализатор не отправляется</summary>
public interface ITruthSink
{
    void Write(JsonObject line);
}

public sealed class ListSink : IEventSink, ITruthSink
{
    public List<SimEvent> Events { get; } = [];
    public List<JsonObject> Truth { get; } = [];

    public void Write(SimEvent e) => Events.Add(e);

    public void Write(JsonObject line) => Truth.Add(line);
}

/// <summary>Построчная запись в файл (UTF-8, LF)</summary>
public sealed class JsonlWriter(string path, bool append = false) : IEventSink, ITruthSink, IDisposable
{
    private readonly StreamWriter _w = new(new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false))
    {
        NewLine = "\n",
    };

    public long Lines { get; private set; }

    public void Write(SimEvent e) => WriteLine(e.ToJsonLine());

    public void Write(JsonObject line) => WriteLine(line.ToJsonString(Iso.Compact));

    public void WriteLine(string line)
    {
        _w.WriteLine(line);
        Lines++;
    }

    /// <summary>Сбросить на диск (outbox: событие сначала на диске, потом отправляется)</summary>
    public void Flush()
    {
        _w.Flush();
        ((FileStream)_w.BaseStream).Flush(flushToDisk: true);
    }

    public void Dispose() => _w.Dispose();
}

/// <summary>Счётчики по типам (manifest, лог)</summary>
public sealed class Stats : IEventSink
{
    public SortedDictionary<string, long> ByType { get; } = new(StringComparer.Ordinal);
    public long Total { get; private set; }

    public void Write(SimEvent e)
    {
        ByType[e.Type] = ByType.GetValueOrDefault(e.Type) + 1;
        Total++;
    }
}
