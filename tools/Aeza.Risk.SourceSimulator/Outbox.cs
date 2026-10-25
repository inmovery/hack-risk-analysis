using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

namespace Aeza.Risk.SourceSimulator;

/// <summary>Позиция отправки outbox: сдвигается только после 200/202 анализатора</summary>
public sealed class Cursor
{
    /// <summary>Байтовая позиция первого неподтверждённого события</summary>
    public long Offset { get; set; }
    public long Events { get; set; }
    public bool SnapshotUploaded { get; set; }
    public DateTimeOffset? Watermark { get; set; }

    public static Cursor Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Cursor>(File.ReadAllText(path)) ?? new Cursor() : new Cursor();

    /// <summary>Атомарная запись: временный файл + переименование</summary>
    public void Save(string path) => AtomicFile.Write(path, JsonSerializer.Serialize(this));
}

public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        var tmp = path + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            f.Write(bytes);
            f.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class SenderOptions
{
    public int BatchSize { get; set; } = 1000;
    public int MaxBatchBytes { get; set; } = 4 * 1024 * 1024;
    public TimeSpan MinDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Пауза при ошибках клиента (401/403/422): нужна правка конфигурации</summary>
    public TimeSpan ClientErrorDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan Idle { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan WatermarkEvery { get; set; } = TimeSpan.FromMinutes(1);
    /// <summary>Шум доставки (live): перестановка событий разных аккаунтов внутри пакета, повтор пакета</summary>
    public double Shuffle { get; set; }
    public double Duplicate { get; set; }
    public ulong ChaosSeed { get; set; } = 1;
}

/// <summary>Итог отправки</summary>
public sealed record SendTotals(long Batches, long Events, long Applied, long Recorded, long Duplicates, long Rejected, long Stale);

/// <summary>Исходящий коннектор источника: outbox-файл JSONL → пакеты POST /api/v1/source/events</summary>
public sealed class OutboxSender(HttpClient http, string outboxPath, string cursorPath, string runId, SenderOptions options, ILogger log)
{
    private readonly Rng _chaos = new(options.ChaosSeed);
    private int _batchSize = options.BatchSize;
    private long _batches, _events, _applied, _recorded, _duplicates, _rejected, _stale;
    private DateTimeOffset _lastWatermarkSent = DateTimeOffset.MinValue;

    public Cursor Cursor { get; } = Cursor.Load(cursorPath);

    public SendTotals Totals => new(_batches, _events, _applied, _recorded, _duplicates, _rejected, _stale);

    /// <summary>Остановить после стольких пакетов (тесты: «падение» отправителя посередине)</summary>
    public int? StopAfterBatches { get; set; }

    /// <summary>Отправлять, пока не отменят</summary>
    public async Task RunAsync(Func<long> committed, Func<DateTimeOffset> generatedThrough, bool stopWhenDrained, CancellationToken ct)
    {
        var sent = 0;
        while (!ct.IsCancellationRequested)
        {
            var (lines, bytes) = ReadBatch(committed());
            if (lines.Count == 0)
            {
                await SendWatermarkAsync(generatedThrough(), force: stopWhenDrained, ct);
                if (stopWhenDrained) return;
                await Task.Delay(options.Idle, ct);
                continue;
            }
            var ordered = options.Shuffle > 0 && _chaos.Chance(options.Shuffle) ? ShuffleAcrossAccounts(lines) : lines;
            var result = await PostAsync(Body(ordered), ct);
            if (result is null) continue; // 413: пакет уменьшен, читаем заново
            Account(result, lines.Count);
            Cursor.Offset += bytes;
            Cursor.Events += lines.Count;
            Cursor.Save(cursorPath);
            if (options.Duplicate > 0 && _chaos.Chance(options.Duplicate))
            {
                // Ответ «потерялся» — источник повторяет тот же пакет: анализатор отвечает duplicate
                var again = await PostAsync(Body(ordered), ct);
                if (again is not null) log.LogInformation("Повтор пакета: duplicates {Duplicates}", again["duplicates"]?.GetValue<int>());
            }
            if (DateTimeOffset.UtcNow - _lastWatermarkSent >= options.WatermarkEvery) await SendWatermarkAsync(generatedThrough(), force: false, ct);
            if (StopAfterBatches is { } stop && ++sent >= stop) return;
        }
    }

    /// <summary>Загрузить тестовые данные на T₀ (PUT /api/v1/source/snapshot). 409 — в анализаторе уже более новые данные</summary>
    public async Task<bool> UploadSnapshotAsync(string snapshotPath, CancellationToken ct)
    {
        var delay = options.MinDelay;
        var body = await File.ReadAllTextAsync(snapshotPath, ct);
        while (true)
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await http.PutAsync("/api/v1/source/snapshot", content, ct);
                if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted)
                {
                    Cursor.SnapshotUploaded = true;
                    Cursor.Save(cursorPath);
                    log.LogInformation("Тестовые данные загружены: {Status}", (int)resp.StatusCode);
                    return true;
                }
                var problem = await resp.Content.ReadAsStringAsync(ct);
                if ((int)resp.StatusCode < 500 && resp.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    log.LogError("Тестовые данные не приняты: {Status} {Problem}", (int)resp.StatusCode, Trim(problem));
                    return false;
                }
                log.LogWarning("Анализатор недоступен ({Status}), повтор через {Delay}", (int)resp.StatusCode, delay);
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning("Анализатор недоступен: {Message}; повтор через {Delay}", ex.Message, delay);
            }
            await Task.Delay(delay, ct);
            delay = Next(delay);
        }
    }

    private (List<string> Lines, long Bytes) ReadBatch(long committed)
    {
        var lines = new List<string>();
        if (committed <= Cursor.Offset || !File.Exists(outboxPath)) return (lines, 0);
        using var f = new FileStream(outboxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        f.Seek(Cursor.Offset, SeekOrigin.Begin);
        var limit = (int)Math.Min(committed - Cursor.Offset, options.MaxBatchBytes + 64 * 1024);
        var buffer = new byte[limit];
        var read = 0;
        while (read < limit)
        {
            var n = f.Read(buffer, read, limit - read);
            if (n == 0) break;
            read += n;
        }
        var start = 0;
        long consumed = 0;
        for (var i = 0; i < read && lines.Count < _batchSize; i++)
        {
            if (buffer[i] != (byte)'\n') continue;
            var len = i - start;
            if (consumed + len + 1 > options.MaxBatchBytes && lines.Count > 0) break;
            if (len > 0) lines.Add(Encoding.UTF8.GetString(buffer, start, len));
            consumed += len + 1;
            start = i + 1;
        }
        return (lines, consumed);
    }

    private static string Body(IReadOnlyList<string> lines) => "{\"synthetic\":true,\"events\":[" + string.Join(',', lines) + "]}";

    /// <summary>POST с повторами; null — 413 (пакет уменьшен)</summary>
    private async Task<JsonObject?> PostAsync(string body, CancellationToken ct)
    {
        var delay = options.MinDelay;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan wait;
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await http.PostAsync("/api/v1/source/events", content, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted) return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
                if (resp.StatusCode == HttpStatusCode.RequestEntityTooLarge && _batchSize > 1)
                {
                    _batchSize = Math.Max(1, _batchSize / 2);
                    log.LogWarning("413: пакет уменьшен до {Size}", _batchSize);
                    return null;
                }
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    wait = resp.Headers.RetryAfter?.Delta ?? (resp.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : delay);
                else if ((int)resp.StatusCode >= 500) wait = delay;
                else
                {
                    wait = delay > options.ClientErrorDelay ? delay : options.ClientErrorDelay;
                    log.LogError("Анализатор отклонил пакет: {Status} {Problem}", (int)resp.StatusCode, Trim(text));
                }
                if ((int)resp.StatusCode >= 500) log.LogWarning("Анализатор ответил {Status}; повтор через {Delay}", (int)resp.StatusCode, wait);
            }
            catch (HttpRequestException ex)
            {
                wait = delay;
                log.LogWarning("Анализатор недоступен: {Message}; повтор через {Delay}", ex.Message, wait);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                wait = delay;
                log.LogWarning("Таймаут запроса; повтор через {Delay}", wait);
            }
            await Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, ct);
            delay = Next(delay);
        }
    }

    private TimeSpan Next(TimeSpan delay) => delay * 2 > options.MaxDelay ? options.MaxDelay : delay * 2;

    private void Account(JsonObject r, int count)
    {
        _batches++;
        _events += count;
        _applied += r["applied"]?.GetValue<int>() ?? 0;
        _recorded += r["recorded"]?.GetValue<int>() ?? 0;
        _duplicates += r["duplicates"]?.GetValue<int>() ?? 0;
        _stale += r["stale"]?.GetValue<int>() ?? 0;
        var rejected = r["rejected"]?.GetValue<int>() ?? 0;
        _rejected += rejected;
        // Отклонённые события не повторяются — пишем пути полей (без значений)
        if (rejected > 0) log.LogWarning("Отклонено событий: {Rejected}; поля: {Fields}", rejected, string.Join(", ", (r["errors"]?.AsObject() ?? []).Select(k => k.Key).Take(10)));
        log.LogInformation("Пакет {Batch}: {Count} событий — applied {Applied}, recorded {Recorded}, duplicates {Duplicates}, stale {Stale}, rejected {Rejected}",
            _batches, count, r["applied"], r["recorded"], r["duplicates"], r["stale"], rejected);
    }

    /// <summary>Водяной знак: всё до upTo отправлено и подтверждено (upTo — время первого неподтверждённого события или конец сгенерированного)</summary>
    private async Task SendWatermarkAsync(DateTimeOffset generatedThrough, bool force, CancellationToken ct)
    {
        if (!force && DateTimeOffset.UtcNow - _lastWatermarkSent < options.WatermarkEvery) return;
        var upTo = FirstPendingOccurredAt() is { } first ? first.AddMilliseconds(-1) : generatedThrough;
        if (Cursor.Watermark is { } prev && upTo <= prev)
        {
            _lastWatermarkSent = DateTimeOffset.UtcNow;
            return;
        }
        var occurredAt = upTo > generatedThrough ? upTo : generatedThrough;
        var line = new JsonObject
        {
            ["schemaVersion"] = 2, ["eventId"] = $"{runId}-wm-{upTo.UtcTicks}", ["type"] = "stream.watermark", ["occurredAt"] = Iso.Format(occurredAt),
            ["data"] = new JsonObject { ["upTo"] = Iso.Format(upTo) },
        }.ToJsonString(Iso.Compact);
        var r = await PostAsync(Body([line]), ct);
        if (r is null) return;
        Cursor.Watermark = upTo;
        Cursor.Save(cursorPath);
        _lastWatermarkSent = DateTimeOffset.UtcNow;
        log.LogInformation("Водяной знак: всё до {UpTo} отправлено", Iso.Format(upTo));
    }

    private DateTimeOffset? FirstPendingOccurredAt()
    {
        if (!File.Exists(outboxPath) || new FileInfo(outboxPath).Length <= Cursor.Offset) return null;
        var (lines, _) = ReadBatch(Cursor.Offset + 64 * 1024);
        if (lines.Count == 0) return null;
        using var doc = JsonDocument.Parse(lines[0]);
        return doc.RootElement.TryGetProperty("occurredAt", out var o) ? Iso.Parse(o.GetString()!) : null;
    }

    /// <summary>Перестановка внутри пакета без нарушения порядка в пределах аккаунта</summary>
    private List<string> ShuffleAcrossAccounts(List<string> lines)
    {
        var groups = new List<Queue<string>>();
        var byAccount = new Dictionary<long, Queue<string>>();
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("accountId", out var a) && a.TryGetInt64(out var id))
            {
                if (!byAccount.TryGetValue(id, out var q)) groups.Add(byAccount[id] = q = new Queue<string>());
                q.Enqueue(line);
            }
            else groups.Add(new Queue<string>([line]));
        }
        var result = new List<string>(lines.Count);
        while (groups.Count > 0)
        {
            var i = _chaos.Next(groups.Count);
            result.Add(groups[i].Dequeue());
            if (groups[i].Count == 0) groups.RemoveAt(i);
        }
        return result;
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
