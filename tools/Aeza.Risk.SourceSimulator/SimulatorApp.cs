using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

namespace Aeza.Risk.SourceSimulator;

/// <summary>Симулятор системы-источника</summary>
public static class SimulatorApp
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Options o;
        try
        {
            o = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }
        using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(c =>
        {
            c.SingleLine = true;
            c.TimestampFormat = "HH:mm:ss ";
        }));
        var log = loggers.CreateLogger("Aeza.Risk.SourceSimulator");
        // Ctrl+C и docker stop: отменяем работу и даём программе завершиться самой (состояние сохраняется)
        var stop = new CancellationTokenSource();
        using var onInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c =>
        {
            c.Cancel = true;
            stop.Cancel();
        });
        using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c =>
        {
            c.Cancel = true;
            stop.Cancel();
        });
        try
        {
            return o.Mode switch
            {
                "generate" => Generate(o, log),
                "send" => await SendAsync(o, log, stop.Token),
                "live" or "lifecycle" => await LiveAsync(o, log, scenario: false, stop.Token),
                "scenario" => await LiveAsync(o, log, scenario: true, stop.Token),
                _ => Usage(o.Mode),
            };
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            log.LogError("{Message}", ex.Message);
            return 1;
        }
    }

    private static int Usage(string mode)
    {
        Console.Error.WriteLine($"Неизвестный режим «{mode}». Режимы: generate | send | live | scenario (SIM_MODE или первый аргумент).");
        return 2;
    }

    private static int Generate(Options o, ILogger log)
    {
        var config = SimConfig.Load(o.Config);
        if (o.Customers is { } c) config.Customers = c;
        if (o.Days is { } d) config.Days = d;
        config.Validate();
        var now = DateTimeOffset.UtcNow;
        var end = o.End ?? new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        if (end > now.AddMinutes(1)) throw new InvalidOperationException("--end в будущем: история только в прошлом (анализатор не примет события из будущего)");
        log.LogInformation("Генерация: {Customers} клиентов × {Days} дней до {End}, seed {Seed} → {Dir}", config.Customers, config.Days, Iso.Format(end), o.Seed, o.Dir);
        var started = DateTime.UtcNow;
        var manifest = Dataset.Generate(config, o.Seed, end, o.Dir, o.TruthDaily, p => log.LogInformation("{Progress}", p));
        log.LogInformation("Готово за {Seconds:0} с: событий {Events}, клиентов {Customers}, услуг {Services} (действующих {Live}); runId {RunId}",
            (DateTime.UtcNow - started).TotalSeconds, manifest["events"], manifest["customers"], manifest["services"], manifest["servicesLive"], manifest["runId"]);
        foreach (var (type, count) in manifest["eventsByType"]!.AsObject().OrderByDescending(kv => kv.Value!.GetValue<long>()))
            log.LogInformation("  {Type,-30} {Count}", type, count);
        return 0;
    }

    private static async Task<int> SendAsync(Options o, ILogger log, CancellationToken ct)
    {
        var manifest = ReadManifest(o.Dir);
        using var http = Http(o);
        await WaitForApiAsync(http, log, ct);
        var sender = new OutboxSender(http, Path.Combine(o.Dir, Dataset.EventsFile), Path.Combine(o.Dir, "events.cursor"),
            manifest["runId"]!.GetValue<string>(), SenderOptionsOf(o, chaos: false), log);
        if (!await UploadIfAskedAsync(o, sender, log, ct)) return 1;
        var end = Iso.Parse(manifest["end"]!.GetValue<string>());
        if (o.Via == "import")
        {
            // Пакетный импорт: файл целиком, анализатор применяет сам (durable), пересчёт — один в конце
            var view = await DatasetImporter.ImportAsync(http, o.Dir, manifest["runId"]!.GetValue<string>(), end, log, TimeSpan.FromSeconds(2), ct);
            log.LogInformation("Импорт выполнен: применено {Applied}, записано {Recorded}, без изменений {NoChange}, устаревших {Stale}, повторов {Duplicates}, отклонено {Rejected}",
                view["applied"], view["recorded"], view["noChange"], view["stale"], view["duplicates"], view["rejected"]);
            return view["rejected"]!.GetValue<long>() > 0 ? 1 : 0;
        }
        var length = new FileInfo(Path.Combine(o.Dir, Dataset.EventsFile)).Length;
        log.LogInformation("Отправка {Events} событий набора {RunId} с позиции {Offset}", manifest["events"], manifest["runId"], sender.Cursor.Offset);
        await sender.RunAsync(() => length, () => end, stopWhenDrained: true, ct);
        var t = sender.Totals;
        log.LogInformation("Отправлено: пакетов {Batches}, событий {Events}; applied {Applied}, recorded {Recorded}, duplicates {Duplicates}, stale {Stale}, rejected {Rejected}",
            t.Batches, t.Events, t.Applied, t.Recorded, t.Duplicates, t.Stale, t.Rejected);
        return t.Rejected > 0 ? 1 : 0;
    }

    private static async Task<int> LiveAsync(Options o, ILogger log, bool scenario, CancellationToken ct)
    {
        Directory.CreateDirectory(o.Dir);
        var statePath = Path.Combine(o.Dir, Dataset.StateFile);
        Simulation sim;
        if (File.Exists(statePath))
        {
            sim = Simulation.Load(await File.ReadAllTextAsync(statePath, ct));
            log.LogInformation("Продолжаю мир {RunId} с {Clock} (клиентов {Customers})", sim.W.RunId, Iso.Format(sim.W.Clock), sim.W.Accounts.Count);
        }
        else
        {
            var init = o.Init.FirstOrDefault(File.Exists)
                       ?? throw new InvalidOperationException($"Нет ни {statePath}, ни тестовых данных для старта ({string.Join(", ", o.Init)})");
            var data = JsonNode.Parse(await File.ReadAllTextAsync(init, ct))!.AsObject();
            sim = Simulation.FromSourceData(data, SimConfig.Load(o.Config), o.Seed, DateTimeOffset.UtcNow, Dataset.Sha256(init));
            AtomicFile.Write(statePath, sim.Save());
            log.LogInformation("Мир {RunId} из {Init}: клиентов {Customers}, услуг {Services}", sim.W.RunId, init, sim.W.Accounts.Count, sim.W.Services.Count);
        }
        sim.Narrate = m => log.LogInformation("▶ {Message}", m);
        using var http = Http(o);
        await WaitForApiAsync(http, log, ct);

        // Сначала — история набора, если он есть и ещё не отправлен целиком
        var eventsPath = Path.Combine(o.Dir, Dataset.EventsFile);
        if (File.Exists(eventsPath))
        {
            var history = new OutboxSender(http, eventsPath, Path.Combine(o.Dir, "events.cursor"), sim.W.RunId, SenderOptionsOf(o, chaos: false), log);
            if (!await UploadIfAskedAsync(o, history, log, ct)) return 1;
            var length = new FileInfo(eventsPath).Length;
            if (history.Cursor.Offset < length)
            {
                log.LogInformation("Досылаю историю набора с позиции {Offset} из {Length}", history.Cursor.Offset, length);
                await history.RunAsync(() => length, () => sim.W.HistoryEnd, stopWhenDrained: true, ct);
            }
        }

        // Outbox: событие сначала на диске, потом отправляется. Хвост после последнего сохранения состояния будет сгенерирован заново теми же
        var outboxPath = Path.Combine(o.Dir, "outbox.jsonl");
        var cursorPath = Path.Combine(o.Dir, "outbox.cursor");
        var onDisk = File.Exists(outboxPath) ? new FileInfo(outboxPath).Length : 0;
        if (onDisk > sim.W.OutboxLength)
        {
            using (var f = new FileStream(outboxPath, FileMode.Open, FileAccess.Write)) f.SetLength(sim.W.OutboxLength);
            log.LogInformation("Outbox обрезан до сохранённого состояния: {From} → {To} байт", onDisk, sim.W.OutboxLength);
        }
        else if (onDisk < sim.W.OutboxLength)
        {
            log.LogWarning("Outbox короче сохранённого состояния ({OnDisk} < {Saved}): часть событий потеряна вне симулятора", onDisk, sim.W.OutboxLength);
            sim.W.OutboxLength = onDisk;
        }
        var cursor = Cursor.Load(cursorPath);
        if (sim.W.OutboxLength > 0 && cursor.Offset >= sim.W.OutboxLength)
        {
            // Всё подтверждено — outbox можно начать с нуля, чтобы он не рос бесконечно
            using (var f = new FileStream(outboxPath, FileMode.Open, FileAccess.Write)) f.SetLength(0);
            cursor.Offset = 0;
            cursor.Save(cursorPath);
            sim.W.OutboxLength = 0;
        }
        if (cursor.Offset > sim.W.OutboxLength)
        {
            cursor.Offset = sim.W.OutboxLength;
            cursor.Save(cursorPath);
        }
        AtomicFile.Write(statePath, sim.Save());

        long committed = sim.W.OutboxLength;
        long clock = sim.W.Clock.UtcTicks;
        var sender = new OutboxSender(http, outboxPath, cursorPath, sim.W.RunId, SenderOptionsOf(o, chaos: o.Chaos, sim.C.Noise), log);
        using var senderStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sending = sender.RunAsync(() => Interlocked.Read(ref committed), () => new DateTimeOffset(Interlocked.Read(ref clock), TimeSpan.Zero),
            stopWhenDrained: false, senderStop.Token);

        using var writer = new JsonlWriter(outboxPath, append: true);
        // «Истина» live — отдельным файлом: файлы набора (generate) остаются как в manifest.json
        using var truth = new JsonlWriter(Path.Combine(o.Dir, "truth.live.jsonl"), append: true);
        if (scenario)
        {
            sim.AdvanceTo(DateTimeOffset.UtcNow, new ListSink(), truth); // догнать часы до сценария
            log.LogInformation("{Scenario}", sim.InjectScenario(TimeSpan.FromSeconds(o.StepSeconds)) ?? "Сценарий: нет подходящего клиента с действующей услугой");
        }
        var lastSave = DateTimeOffset.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var buffer = new ListSink();
                sim.AdvanceTo(DateTimeOffset.UtcNow, buffer, truth);
                foreach (var e in buffer.Events) writer.Write(e);
                writer.Flush();
                truth.Flush();
                var length = new FileInfo(outboxPath).Length;
                if (buffer.Events.Count > 0 || DateTimeOffset.UtcNow - lastSave > TimeSpan.FromMinutes(1))
                {
                    sim.W.OutboxLength = length;
                    AtomicFile.Write(statePath, sim.Save());
                    lastSave = DateTimeOffset.UtcNow;
                }
                Interlocked.Exchange(ref committed, length);
                Interlocked.Exchange(ref clock, sim.W.Clock.UtcTicks);
                if (buffer.Events.Count > 0)
                    log.LogInformation("Сгенерировано событий: {Count} ({Types})", buffer.Events.Count,
                        string.Join(", ", buffer.Events.GroupBy(e => e.Type).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key} {g.Count()}")));
                await Task.Delay(TimeSpan.FromSeconds(o.TickSeconds), ct);
            }
        }
        finally
        {
            sim.W.OutboxLength = new FileInfo(outboxPath).Length;
            AtomicFile.Write(statePath, sim.Save());
            await senderStop.CancelAsync();
            try
            {
                await sending;
            }
            catch (OperationCanceledException)
            {
                // остановка
            }
            log.LogInformation("Остановлено; состояние сохранено ({Clock})", Iso.Format(sim.W.Clock));
        }
        return 0;
    }

    private static async Task<bool> UploadIfAskedAsync(Options o, OutboxSender sender, ILogger log, CancellationToken ct)
    {
        var snapshot = Path.Combine(o.Dir, Dataset.SnapshotFile);
        if (sender.Cursor.SnapshotUploaded || !File.Exists(snapshot)) return true;
        if (!o.UploadSnapshot)
        {
            if (sender.Cursor.Offset == 0)
                log.LogWarning("Тестовые данные набора не загружены: события ссылаются на клиентов из {Snapshot}. Нужна пустая база анализатора и SIM_UPLOAD_SNAPSHOT=true", snapshot);
            return true;
        }
        return await sender.UploadSnapshotAsync(snapshot, ct);
    }

    private static JsonObject ReadManifest(string dir)
    {
        var path = Path.Combine(dir, Dataset.ManifestFile);
        if (!File.Exists(path)) throw new InvalidOperationException($"Нет {path}: сначала generate");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static SenderOptions SenderOptionsOf(Options o, bool chaos, NoiseConfig? noise = null) => new()
    {
        BatchSize = o.Batch,
        Shuffle = chaos ? noise?.DeliveryShuffle ?? 0 : 0,
        Duplicate = chaos ? noise?.DeliveryDuplicate ?? 0 : 0,
        ChaosSeed = o.Seed + 7,
    };

    private static HttpClient Http(Options o)
    {
        if (string.IsNullOrEmpty(o.Api)) throw new InvalidOperationException("Нужен SIM_API (--api): адрес анализатора");
        if (string.IsNullOrEmpty(o.Token)) throw new InvalidOperationException("Нужен SIM_INGEST_TOKEN (--token): токен входящего коннектора анализатора");
        var http = new HttpClient { BaseAddress = new Uri(o.Api), Timeout = TimeSpan.FromSeconds(100) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.Token);
        return http;
    }

    private static async Task WaitForApiAsync(HttpClient http, ILogger log, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var r = await http.GetAsync("/health/live", ct);
                if (r.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // ещё стартует
            }
            if (i == 0) log.LogInformation("Жду анализатор {Api}…", http.BaseAddress);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new InvalidOperationException($"Анализатор {http.BaseAddress} не отвечает на /health/live");
    }
}

/// <summary>Параметры: переменные окружения SIM_* и аргументы --имя значение (аргументы важнее)</summary>
public sealed class Options
{
    public string Mode { get; private set; } = "live";
    public string Dir { get; private set; } = "sim-data";
    public string? Api { get; private set; }
    public string? Token { get; private set; }
    public ulong Seed { get; private set; } = 42;
    public int? Customers { get; private set; }
    public int? Days { get; private set; }
    public DateTimeOffset? End { get; private set; }
    public string? Config { get; private set; }
    public IReadOnlyList<string> Init { get; private set; } = ["/seed/data.json"];
    public bool UploadSnapshot { get; private set; }
    public int Batch { get; private set; } = 1000;
    public int TickSeconds { get; private set; } = 5;
    public int StepSeconds { get; private set; } = 15;
    public bool TruthDaily { get; private set; } = true;
    public bool Chaos { get; private set; } = true;
    /// <summary>send: push (пакетами по 1000, по умолчанию) или import (файл целиком, пакетный импорт анализатора)</summary>
    public string Via { get; private set; } = "push";

    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "MODE", "DIR", "API", "INGEST_TOKEN", "SEED", "CUSTOMERS", "DAYS", "END", "CONFIG", "INIT", "UPLOAD_SNAPSHOT", "BATCH", "TICK_SECONDS", "STEP_SECONDS", "TRUTH_DAILY", "CHAOS", "VIA" })
            if (Environment.GetEnvironmentVariable("SIM_" + name) is { Length: > 0 } v) values[name] = v;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                if (i == 0) values["MODE"] = a;
                else throw new ArgumentException($"Лишний аргумент «{a}»");
                continue;
            }
            var key = a[2..].Replace('-', '_').ToUpperInvariant();
            if (key == "TOKEN") key = "INGEST_TOKEN";
            if (key is "UPLOAD_SNAPSHOT" && (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))) values[key] = "true";
            else if (i + 1 < args.Length) values[key] = args[++i];
            else throw new ArgumentException($"Нет значения для {a}");
        }
        var o = new Options();
        string? Get(string k) => values.GetValueOrDefault(k);
        int? Int(string k) => Get(k) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null;
        bool? Bool(string k) => Get(k) is { } s ? s is "1" or "true" or "yes" : null;
        o.Mode = (Get("MODE") ?? "live").ToLowerInvariant();
        o.Dir = Get("DIR") ?? o.Dir;
        o.Api = Get("API");
        o.Token = Get("INGEST_TOKEN");
        if (Get("SEED") is { } seed) o.Seed = ulong.Parse(seed, CultureInfo.InvariantCulture);
        o.Customers = Int("CUSTOMERS");
        o.Days = Int("DAYS");
        if (Get("END") is { } end) o.End = Iso.Parse(end);
        o.Config = Get("CONFIG");
        if (Get("INIT") is { } init) o.Init = init.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        o.UploadSnapshot = Bool("UPLOAD_SNAPSHOT") ?? false;
        o.Batch = Math.Clamp(Int("BATCH") ?? o.Batch, 1, 1000);
        o.TickSeconds = Math.Clamp(Int("TICK_SECONDS") ?? o.TickSeconds, 1, 300);
        o.StepSeconds = Math.Clamp(Int("STEP_SECONDS") ?? o.StepSeconds, 1, 600);
        o.TruthDaily = Bool("TRUTH_DAILY") ?? true;
        o.Chaos = Bool("CHAOS") ?? true;
        o.Via = (Get("VIA") ?? "push").ToLowerInvariant() is "import" ? "import" : "push";
        return o;
    }
}
