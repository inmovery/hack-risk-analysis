using System.IO.Compression;
using System.Text;
using System.Text.Json;

using Aeza.Risk.Core;
using Aeza.Risk.Core.Source;
using Aeza.Risk.SharedKernel.Json;

using Npgsql;

namespace Aeza.Risk.Ml;

/// <summary>Событие журнала для переигрывания: время события, доменная часть (v2) и изменения записей</summary>
public sealed record JournalEvent(DateTimeOffset OccurredAt, DomainEventInput? Domain, IReadOnlyList<SourceChange> Changes);

/// <summary>База (тестовые данные) и журнал по времени события — то же, что принял анализатор</summary>
public interface IJournal
{
    /// <summary>Описание источника для карточки модели (без секретов)</summary>
    string Describe { get; }
    bool Synthetic { get; }
    SourceData Base { get; }
    DateTimeOffset BaseAsOf { get; }
    /// <summary>События по возрастанию времени события</summary>
    IEnumerable<JournalEvent> Events();
}

/// <summary>Журнал из файлов набора симулятора: snapshot.json + events.jsonl (формат контракта анализатора)</summary>
public sealed class FileJournal : IJournal
{
    private readonly string _events;

    public FileJournal(string dir)
    {
        var snapshot = SnapshotParser.Parse(File.ReadAllBytes(Path.Combine(dir, "snapshot.json")));
        if (!snapshot.IsValid || snapshot.SourceAsOf is not { } asOf) throw new InvalidOperationException("snapshot.json не прошёл проверку или без sourceAsOf");
        Base = snapshot.Data;
        BaseAsOf = asOf;
        Synthetic = snapshot.Synthetic;
        _events = Path.Combine(dir, "events.jsonl");
        var manifest = Path.Combine(dir, "manifest.json");
        Describe = File.Exists(manifest) && JsonDocument.Parse(File.ReadAllText(manifest)).RootElement.TryGetProperty("runId", out var run)
            ? $"dataset:{run.GetString()}"
            : $"dataset:{Path.GetFileName(Path.GetFullPath(dir))}";
    }

    public string Describe { get; }
    public bool Synthetic { get; }
    public SourceData Base { get; }
    public DateTimeOffset BaseAsOf { get; }

    public IEnumerable<JournalEvent> Events()
    {
        using var reader = new StreamReader(_events, Encoding.UTF8);
        var chunk = new List<string>(1000);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            chunk.Add(line);
            if (chunk.Count < 1000) continue;
            foreach (var e in Parse(chunk)) yield return e;
            chunk.Clear();
        }
        foreach (var e in Parse(chunk)) yield return e;
    }

    private static IEnumerable<JournalEvent> Parse(List<string> lines)
    {
        if (lines.Count == 0) yield break;
        var batch = EventBatchParser.Parse(Encoding.UTF8.GetBytes("{\"events\":[" + string.Join(',', lines) + "]}"));
        foreach (var e in batch.Events)
            if (e is { IsValid: true, Domain: { } d }) yield return new JournalEvent(d.OccurredAt, d, e.Changes);
    }
}

/// <summary>Журнал из БД анализатора: действующая база (сырой файл загрузки) и журнал — доменные события v2 с изменениями записей из state и события v1</summary>
public sealed class DbJournal(string connectionString) : IJournal
{
    private SourceData? _base;
    private DateTimeOffset _asOf;
    private bool _synthetic;

    public string Describe => $"db:{new NpgsqlConnectionStringBuilder(connectionString) { Password = null }.Host}/{new NpgsqlConnectionStringBuilder(connectionString).Database}";

    public bool Synthetic
    {
        get
        {
            Load();
            return _synthetic;
        }
    }

    public SourceData Base
    {
        get
        {
            Load();
            return _base!;
        }
    }

    public DateTimeOffset BaseAsOf
    {
        get
        {
            Load();
            return _asOf;
        }
    }

    private void Load()
    {
        if (_base is not null) return;
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT s.source_as_of, s.raw_gzip, s.synthetic FROM app_state a JOIN source_snapshots s ON s.id = a.latest_source_snapshot_id", conn);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("В базе анализатора нет тестовых данных");
        _asOf = r.GetFieldValue<DateTimeOffset>(0);
        var raw = (byte[])r[1];
        _synthetic = r.GetBoolean(2);
        using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var parsed = SnapshotParser.Parse(ms.ToArray());
        if (!parsed.IsValid) throw new InvalidOperationException("Тестовые данные в базе не прошли проверку парсером");
        _base = parsed.Data;
    }

    public IEnumerable<JournalEvent> Events()
    {
        using var v2Conn = new NpgsqlConnection(connectionString);
        using var v1Conn = new NpgsqlConnection(connectionString);
        v2Conn.Open();
        v1Conn.Open();
        using var v2 = new NpgsqlCommand(@"
SELECT d.occurred_at, d.event_id, d.type, d.known_type, d.account_id, d.account_seq, d.service_id, d.data::text,
       coalesce(json_agg(json_build_object('kind', e.kind, 'payload', e.payload) ORDER BY e.seq) FILTER (WHERE e.seq IS NOT NULL), '[]'::json)::text
FROM source_domain_events d LEFT JOIN source_events e ON e.domain_seq = d.seq
GROUP BY d.seq ORDER BY d.occurred_at, d.seq", v2Conn) { CommandTimeout = 0 };
        using var v1 = new NpgsqlCommand("SELECT occurred_at, event_id, kind, payload::text FROM source_events WHERE domain_seq IS NULL ORDER BY occurred_at, seq", v1Conn)
            { CommandTimeout = 0 };
        using var a = v2.ExecuteReader();
        using var b = v1.ExecuteReader();
        var nextA = ReadV2(a);
        var nextB = ReadV1(b);
        while (nextA is not null || nextB is not null)
        {
            if (nextB is null || nextA is not null && nextA.OccurredAt <= nextB.OccurredAt)
            {
                yield return nextA!;
                nextA = ReadV2(a);
            }
            else
            {
                yield return nextB;
                nextB = ReadV1(b);
            }
        }
    }

    private static JournalEvent? ReadV2(NpgsqlDataReader r)
    {
        if (!r.Read()) return null;
        var at = r.GetFieldValue<DateTimeOffset>(0);
        var type = r.GetString(2);
        var data = r.GetString(7);
        DateTimeOffset? upTo = null;
        if (type == EventCatalog.Watermark && JsonDocument.Parse(data).RootElement.TryGetProperty("upTo", out var u) && u.ValueKind == JsonValueKind.String)
            upTo = DateTimeOffset.Parse(u.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var domain = new DomainEventInput(r.GetString(1), type, at, r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetInt64(6), data, r.GetBoolean(3), upTo);
        var changes = new List<SourceChange>();
        foreach (var rec in JsonDocument.Parse(r.GetString(8)).RootElement.EnumerateArray())
            changes.Add(FromPayload(rec.GetProperty("kind").GetString()!, rec.GetProperty("payload").GetRawText()));
        return new JournalEvent(at, domain, changes);
    }

    private static JournalEvent? ReadV1(NpgsqlDataReader r)
    {
        if (!r.Read()) return null;
        return new JournalEvent(r.GetFieldValue<DateTimeOffset>(0), null, [FromPayload(r.GetString(2), r.GetString(3), r.IsDBNull(1) ? null : r.GetString(1))]);
    }

    /// <summary>Состояние записи из журнала (тот же формат, что пишет приём анализатора)</summary>
    private static SourceChange FromPayload(string kind, string payload, string? eventId = null) => SourceCodes.ParseKind(kind) switch
    {
        SourceRecordKind.Account => SourceChange.Of(AppJson.Deserialize<AccountInput>(payload), eventId),
        SourceRecordKind.Service => SourceChange.Of(AppJson.Deserialize<ServiceInput>(payload), eventId),
        _ => SourceChange.Of(AppJson.Deserialize<TransactionInput>(payload), eventId),
    };
}
