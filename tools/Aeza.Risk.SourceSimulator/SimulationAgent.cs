using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

namespace Aeza.Risk.SourceSimulator;

/// <summary>Событие, собранное в панели</summary>
public sealed record AgentCommand(long Id, string Type, long AccountId, long? ServiceId, JsonObject Data);

/// <summary>Указания анализатора агенту (GET /api/v1/simulation/agent)</summary>
public sealed record AgentInstructions(
    bool EventsOnly, bool Running, int TickSeconds, int ScenarioStepSeconds, bool Chaos, WorldSpec? World, long ScenarioRequest, IReadOnlyList<AgentCommand>? Commands = null)
{
    public static AgentInstructions Parse(JsonObject o)
    {
        var commands = o["commands"] is JsonArray arr
            ? arr.OfType<JsonObject>().Where(c => Long(c["id"]) is not null && Str(c["type"]) is not null && Long(c["accountId"]) is not null)
                .Select(c => new AgentCommand(Long(c["id"])!.Value, Str(c["type"])!, Long(c["accountId"])!.Value, Long(c["serviceId"]),
                    c["data"] as JsonObject is { } d ? (JsonObject)d.DeepClone() : []))
                .ToList()
            : [];
        var settings = o["settings"] as JsonObject ?? [];
        WorldSpec? world = null;
        if (o["world"] is JsonObject w && w["settings"] is JsonObject ws && Str(w["startedAt"]) is { } started)
        {
            world = new WorldSpec(
                Long(w["revision"]) ?? 0, Iso.Parse(started), Str(ws["source"]) == WorldSpec.FromGenerator ? WorldSpec.FromGenerator : WorldSpec.FromOrganizer,
                (int)(Long(ws["seed"]) ?? 42), (int)(Long(ws["extraCustomers"]) ?? 0), (int)(Long(ws["customers"]) ?? 0), (int)(Long(ws["historyDays"]) ?? 0));
        }
        return new AgentInstructions(
            Bool(o["eventsOnly"]) ?? false, Bool(settings["running"]) ?? true,
            (int)Math.Clamp(Long(settings["tickSeconds"]) ?? 5, 1, 300), (int)Math.Clamp(Long(settings["scenarioStepSeconds"]) ?? 15, 1, 600),
            Bool(settings["chaos"]) ?? false, world, Long(o["scenarioRequest"]) ?? 0, commands);
    }

    private static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static long? Long(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && long.TryParse(v.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : null;

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

public static class AgentPhases
{
    public const string Idle = "idle";
    public const string Bootstrap = "bootstrap";
    public const string History = "history";
    public const string Live = "live";
    public const string Paused = "paused";
    public const string Error = "error";
}

public sealed class AgentOptions
{
    public string Dir { get; init; } = "sim-data";
    public IReadOnlyList<string> Init { get; init; } = ["/seed/data.json"];
    public string? Config { get; init; }
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan StatusEvery { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Ожидание ответа на указания и состояние: недоступный анализатор не должен останавливать шаг надолго</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int Batch { get; init; } = 1000;
    /// <summary>Неотправленных событий больше этого — генерация ждёт отправку</summary>
    public long MaxBacklog { get; init; } = 100_000;
    /// <summary>Сколько генерировать за шаг, прежде чем снова прочитать указания</summary>
    public TimeSpan StepBudget { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Полностью отправленный outbox больше этого обнуляется</summary>
    public long CompactBytes { get; init; } = 32L * 1024 * 1024;
    /// <summary>Задержки отправителя (повторы, простой, водяной знак)</summary>
    public SenderOptions Sender { get; init; } = new();
}

/// <summary>Режим agent: snapshot системы задаёт и включает анализатор, симулятор только исполняет и сообщает состояние</summary>
public sealed class SimulationAgent(HttpClient http, AgentOptions options, ILogger log) : IAsyncDisposable
{
    public const string Version = "2";
    public const string InstructionsPath = "/api/v1/simulation/agent";
    public const string StatusPath = "/api/v1/simulation/agent/status";
    public const string CommandsPath = "/api/v1/simulation/agent/commands";

    private WorldRun? _run;
    private long _scenarioHandled;
    private bool _stateLoaded;
    private string? _lastError;
    private string? _lastNarration;
    private string? _readProblem;
    private string? _statusProblem;
    private DateTimeOffset _lastStatus = DateTimeOffset.MinValue;

    public string Phase { get; private set; } = AgentPhases.Idle;
    public long ScenarioHandled => _scenarioHandled;
    public string? LastNarration => _lastNarration;
    public string? LastError => _lastError;
    public Simulation? Sim => _run?.Sim;
    public long? Revision => _run?.Spec.Revision;
    public string? WorldDir => _run?.Dir;
    public OutboxSender? Sender => _run?.Sender;

    private string AgentStatePath => Path.Combine(options.Dir, "agent.json");

    public async Task RunAsync(CancellationToken ct)
    {
        log.LogInformation("Агент: указания {Path}, каталог {Dir}", InstructionsPath, options.Dir);
        try
        {
            while (!ct.IsCancellationRequested) await Task.Delay(await StepAsync(ct), ct);
        }
        finally
        {
            await StopRunAsync();
        }
    }

    /// <summary>Один шаг: прочитать указания, привести snapshot системы к ним, сообщить состояние. Возвращает паузу до следующего шага</summary>
    public async Task<TimeSpan> StepAsync(CancellationToken ct)
    {
        LoadAgentState();
        var before = Phase;
        var wait = options.Poll;
        if (await ReadInstructionsAsync(ct) is { } instructions)
        {
            try
            {
                wait = await ApplyAsync(instructions, ct);
                _lastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_lastError != ex.Message) log.LogError("Агент: {Message}", ex.Message);
                _lastError = ex.Message;
                Phase = AgentPhases.Error;
                await StopRunAsync();
            }
        }
        await ReportAsync(force: Phase != before, ct);
        return wait;
    }

    public async ValueTask DisposeAsync() => await StopRunAsync();

    private async Task<TimeSpan> ApplyAsync(AgentInstructions i, CancellationToken ct)
    {
        if (!i.EventsOnly || i.World is null)
        {
            if (_run is not null)
            {
                log.LogInformation("Работа на событиях выключена: snapshot системы {Revision} остановлен", _run.Spec.Revision);
                await StopRunAsync();
            }
            Phase = AgentPhases.Idle;
            return options.Poll;
        }
        if (_run is null || _run.Spec.Revision != i.World.Revision)
        {
            await StopRunAsync();
            DeleteOtherWorlds(i.World.Revision);
            _run = WorldRun.Open(i.World, options, http, log, m => _lastNarration = m);
        }
        var run = _run;
        if (run.Sending is { IsCompleted: true })
        {
            // Анализатор ответил 409 (snapshot системы сменился), а указания прежние, или отправитель упал — запускаем снова
            if (run.Sending.Exception is { } ex) log.LogWarning("Отправитель остановился: {Message}", ex.GetBaseException().Message);
            run.StartSender();
        }

        var now = DateTimeOffset.UtcNow;
        var caughtUp = true;
        if (!run.Meta.HistoryDone)
        {
            if (i.Running) caughtUp = run.CatchUp(Iso.Sec(i.World.StartedAt), options.StepBudget, options.MaxBacklog);
        }
        else if (i.Running && (now - run.LastTick >= TimeSpan.FromSeconds(i.TickSeconds) || i.ScenarioRequest > _scenarioHandled))
        {
            caughtUp = run.CatchUp(now, options.StepBudget, options.MaxBacklog);
            if (caughtUp) run.LastTick = now;
        }

        if (i.ScenarioRequest < _scenarioHandled) SaveScenarioHandled(i.ScenarioRequest); // счётчик анализатора сброшен
        if (i.ScenarioRequest > _scenarioHandled && i.Running && run.Meta.HistoryDone && caughtUp)
        {
            var told = run.Sim.InjectScenario(TimeSpan.FromSeconds(i.ScenarioStepSeconds)) ?? "Сценарий: нет подходящего клиента с действующей услугой";
            _lastNarration = told;
            log.LogInformation("▶ {Message}", told);
            SaveScenarioHandled(i.ScenarioRequest);
            run.Save();
        }

        // События из панели — после истории, на текущих часах snapshot системы
        if (run.Meta.HistoryDone && caughtUp)
            foreach (var c in (i.Commands ?? []).Where(c => !run.Meta.Commands.ContainsKey(c.Id)))
            {
                var outcome = run.ApplyCommand(c);
                log.LogInformation("Событие из панели {Id} ({Type}): {Status} {Message}", c.Id, c.Type, outcome.Status, outcome.Message);
            }
        await ReportCommandsAsync(run, ct);

        run.SetChaos(i.Chaos);
        await run.CompactIfDrainedAsync(options.CompactBytes);

        var sent = run.SentEvents;
        Phase = !i.Running ? AgentPhases.Paused
            : sent < run.Meta.BootstrapEvents ? AgentPhases.Bootstrap
            : !run.Meta.HistoryDone || sent < run.Meta.PreLiveEvents ? AgentPhases.History
            : AgentPhases.Live;
        if (run.Sender?.WorldChanged == true) return TimeSpan.Zero; // скорее перечитать указания
        return caughtUp || run.Backlog >= options.MaxBacklog ? options.Poll : TimeSpan.FromMilliseconds(50);
    }

    private async Task ReportCommandsAsync(WorldRun run, CancellationToken ct)
    {
        var pending = run.Meta.Commands.Where(kv => !kv.Value.Reported).ToList();
        if (pending.Count == 0) return;
        var body = new JsonArray(pending.Select(kv => (JsonNode)new JsonObject
        {
            ["id"] = kv.Key, ["status"] = kv.Value.Status, ["eventIds"] = new JsonArray(kv.Value.EventIds.Select(e => (JsonNode)e).ToArray()),
            ["message"] = kv.Value.Message,
        }).ToArray());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(CommandsPath, content, timeout.Token);
            if (!resp.IsSuccessStatusCode) return;
            foreach (var (_, outcome) in pending) outcome.Reported = true;
            run.Save();
        }
        catch (HttpRequestException)
        {
            // Отчёт повторится на следующем шаге
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Отчёт повторится на следующем шаге
        }
    }

    private async Task<AgentInstructions?> ReadInstructionsAsync(CancellationToken ct)
    {
        string? problem;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            using var resp = await http.GetAsync(InstructionsPath, timeout.Token);
            if (resp.IsSuccessStatusCode && JsonNode.Parse(await resp.Content.ReadAsStringAsync(timeout.Token)) is JsonObject o)
            {
                if (_readProblem is not null) log.LogInformation("Указания анализатора снова доступны");
                _readProblem = null;
                return AgentInstructions.Parse(o);
            }
            problem = resp.StatusCode == HttpStatusCode.NotFound
                ? $"анализатор не знает {InstructionsPath} (404): жду"
                : $"указания: {(int)resp.StatusCode}";
        }
        catch (HttpRequestException ex)
        {
            problem = $"анализатор недоступен: {ex.Message}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            problem = "анализатор не ответил на запрос указаний";
        }
        catch (JsonException)
        {
            problem = "указания — не JSON";
        }
        if (problem != _readProblem) log.LogWarning("Агент: {Problem}", problem);
        _readProblem = problem;
        return null;
    }

    private JsonObject Status()
    {
        var run = _run;
        var w = run?.Sim.W;
        return new JsonObject
        {
            ["agentVersion"] = Version,
            ["worldRevision"] = run?.Spec.Revision ?? 0,
            ["runId"] = w?.RunId,
            ["phase"] = Phase,
            ["simClock"] = w is null ? null : Iso.Format(w.Clock),
            ["customers"] = w?.Accounts.Count ?? 0,
            ["services"] = w?.Services.Count ?? 0,
            ["liveServices"] = w?.Services.Values.Count(s => s.Live) ?? 0,
            ["eventsGenerated"] = run?.Meta.OutboxEvents ?? 0,
            ["eventsSent"] = run?.SentEvents ?? 0,
            ["backlog"] = run?.Backlog ?? 0,
            ["lastSentAt"] = run?.Sender?.LastAckAt is { } at ? Iso.Format(at) : null,
            ["lastError"] = _lastError,
            ["lastNarration"] = _lastNarration,
            ["scenarioHandled"] = _scenarioHandled,
        };
    }

    private async Task ReportAsync(bool force, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastStatus < options.StatusEvery) return;
        _lastStatus = now;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);
        string? problem = null;
        try
        {
            using var content = new StringContent(Status().ToJsonString(Iso.Compact), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(StatusPath, content, timeout.Token);
            if (!resp.IsSuccessStatusCode) problem = $"{(int)resp.StatusCode}";
        }
        catch (HttpRequestException ex)
        {
            problem = ex.GetBaseException().Message;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            problem = "нет ответа";
        }
        // Сообщим на следующем шаге; в лог — только смену причины
        if (problem is not null && problem != _statusProblem) log.LogWarning("Состояние агента не принято: {Problem}", problem);
        _statusProblem = problem;
    }

    private async Task StopRunAsync()
    {
        if (_run is not { } run) return;
        _run = null;
        await run.StopSenderAsync();
        try
        {
            run.Save();
        }
        catch (IOException ex)
        {
            log.LogWarning("Состояние snapshot системы не сохранено: {Message}", ex.Message);
        }
    }

    private void DeleteOtherWorlds(long revision)
    {
        if (!Directory.Exists(options.Dir)) return;
        foreach (var dir in Directory.GetDirectories(options.Dir, "world-*"))
        {
            if (Path.GetFileName(dir) == WorldRun.DirName(revision)) continue;
            Directory.Delete(dir, recursive: true);
            log.LogInformation("Удалён snapshot системы прошлой ревизии: {Dir}", dir);
        }
    }

    private void LoadAgentState()
    {
        if (_stateLoaded) return;
        _stateLoaded = true;
        try
        {
            if (File.Exists(AgentStatePath) && JsonNode.Parse(File.ReadAllText(AgentStatePath)) is JsonObject o && o["scenarioHandled"] is JsonValue v)
                _scenarioHandled = v.GetValue<long>();
        }
        catch (JsonException)
        {
            log.LogWarning("Повреждён {Path}: счётчик сценариев с нуля", AgentStatePath);
        }
    }

    private void SaveScenarioHandled(long value)
    {
        _scenarioHandled = value;
        Directory.CreateDirectory(options.Dir);
        AtomicFile.Write(AgentStatePath, new JsonObject { ["scenarioHandled"] = value }.ToJsonString());
    }
}

/// <summary>Учёт snapshot системы агента: что уже записано в outbox и где кончается начальное состояние и история</summary>
public sealed class WorldMeta
{
    public long Revision { get; set; }
    public string RunId { get; set; } = "";
    public long BootstrapEvents { get; set; }
    public bool HistoryDone { get; set; }
    /// <summary>Событий до live: начальное состояние и история</summary>
    public long PreLiveEvents { get; set; }
    /// <summary>Событий записано в outbox за всё время snapshot системы</summary>
    public long OutboxEvents { get; set; }
    /// <summary>Итоги событий из панели (по номеру): применяются один раз, отчёт повторяется, пока анализатор его не примет</summary>
    public Dictionary<long, CommandOutcome> Commands { get; set; } = new();
}

/// <summary>Итог события из панели</summary>
public sealed class CommandOutcome
{
    public string Status { get; set; } = "";
    public List<string> EventIds { get; set; } = [];
    public string? Message { get; set; }
    public bool Reported { get; set; }
}

/// <summary>Snapshot системы одной ревизии на диске: {dir}/world-N — state.json, outbox.jsonl, outbox.cursor</summary>
public sealed class WorldRun
{
    private readonly HttpClient _http;
    private readonly AgentOptions _options;
    private readonly ILogger _log;
    private CancellationTokenSource? _senderStop;
    private SenderOptions _senderOptions = new();
    private long _committed;
    private long _clock;

    private WorldRun(WorldSpec spec, Simulation sim, WorldMeta meta, string dir, HttpClient http, AgentOptions options, ILogger log)
    {
        Spec = spec;
        Sim = sim;
        Meta = meta;
        Dir = dir;
        _http = http;
        _options = options;
        _log = log;
    }

    public WorldSpec Spec { get; }
    public Simulation Sim { get; }
    public WorldMeta Meta { get; }
    public string Dir { get; }
    public OutboxSender? Sender { get; private set; }
    public Task? Sending { get; private set; }
    public DateTimeOffset LastTick { get; set; } = DateTimeOffset.MinValue;

    public string StatePath => Path.Combine(Dir, "state.json");
    public string OutboxPath => Path.Combine(Dir, "outbox.jsonl");
    public string CursorPath => Path.Combine(Dir, "outbox.cursor");

    /// <summary>Подтверждено анализатором событий за всё время snapshot системы</summary>
    public long SentEvents => Sender?.Cursor.Events ?? Cursor.Load(CursorPath).Events;

    public long Backlog => Math.Max(0, Meta.OutboxEvents - SentEvents);

    public static string DirName(long revision) => $"world-{revision}";

    /// <summary>Продолжить сохранённый snapshot системы этой ревизии или построить его заново (те же события: анализатор отбросит повторы по eventId)</summary>
    public static WorldRun Open(WorldSpec spec, AgentOptions options, HttpClient http, ILogger log, Action<string> narrate)
    {
        var dir = Path.Combine(options.Dir, DirName(spec.Revision));
        Directory.CreateDirectory(dir);
        var (organizer, hash) = spec.Generated ? (null, null) : ReadOrganizer(options.Init);
        // Без файла организаторов сохранённый snapshot системы продолжаем как есть: сверить его не с чем
        var expected = !spec.Generated && hash is null ? null : spec.RunId(hash);
        var run = TryLoad(spec, expected, dir, options, http, log) ?? Create(spec, organizer, hash, dir, options, http, log);
        run.Sim.Narrate = m =>
        {
            narrate(m);
            log.LogInformation("▶ {Message}", m);
        };
        run.StartSender();
        return run;
    }

    private static (JsonObject? Data, string? Hash) ReadOrganizer(IReadOnlyList<string> init)
    {
        var path = init.FirstOrDefault(File.Exists);
        if (path is null) return (null, null);
        return (JsonNode.Parse(File.ReadAllText(path))!.AsObject(), Dataset.Sha256(path));
    }

    private static WorldRun? TryLoad(WorldSpec spec, string? runId, string dir, AgentOptions options, HttpClient http, ILogger log)
    {
        var statePath = Path.Combine(dir, "state.json");
        if (!File.Exists(statePath)) return null;
        WorldMeta meta;
        Simulation sim;
        using (var doc = JsonDocument.Parse(File.ReadAllText(statePath)))
        {
            meta = doc.RootElement.GetProperty("agent").Deserialize<WorldMeta>() ?? new WorldMeta();
            sim = Simulation.Load(doc.RootElement.GetProperty("sim").GetRawText());
        }
        if (meta.Revision != spec.Revision || (runId is not null && meta.RunId != runId))
        {
            log.LogWarning("Сохранённый snapshot системы {Dir} не совпадает с заданным: строю заново", dir);
            return null;
        }
        var run = new WorldRun(spec, sim, meta, dir, http, options, log);
        sim.TruthDaily = false;
        var onDisk = File.Exists(run.OutboxPath) ? new FileInfo(run.OutboxPath).Length : 0;
        if (onDisk < sim.W.OutboxLength)
        {
            log.LogWarning("Outbox snapshot системы {Dir} короче сохранённого ({OnDisk} < {Saved}): строю заново", dir, onDisk, sim.W.OutboxLength);
            return null;
        }
        // Хвост после последнего сохранения будет сгенерирован заново теми же событиями
        if (onDisk > sim.W.OutboxLength) Truncate(run.OutboxPath, sim.W.OutboxLength);
        var cursor = Cursor.Load(run.CursorPath);
        if (cursor.Offset > sim.W.OutboxLength)
        {
            cursor.Offset = sim.W.OutboxLength;
            cursor.Events = meta.OutboxEvents;
            cursor.Save(run.CursorPath);
        }
        if (sim.W.OutboxLength > 0 && cursor.Offset == sim.W.OutboxLength) run.Compact();
        run.Sync();
        log.LogInformation("Продолжаю snapshot системы {Revision} ({RunId}) с {Clock}: клиентов {Customers}", spec.Revision, meta.RunId, Iso.Format(sim.W.Clock), sim.W.Accounts.Count);
        return run;
    }

    private static WorldRun Create(WorldSpec spec, JsonObject? organizer, string? hash, string dir, AgentOptions options, HttpClient http, ILogger log)
    {
        var (sim, bootstrap) = AgentWorld.Build(spec, SimConfig.Load(options.Config), organizer, hash);
        var meta = new WorldMeta { Revision = spec.Revision, RunId = sim.W.RunId, HistoryDone = !spec.Generated };
        var run = new WorldRun(spec, sim, meta, dir, http, options, log);
        foreach (var f in new[] { run.OutboxPath, run.CursorPath }) File.Delete(f);
        using (var w = new JsonlWriter(run.OutboxPath))
        {
            foreach (var line in bootstrap) w.WriteLine(line);
            w.Flush();
        }
        meta.BootstrapEvents = meta.OutboxEvents = bootstrap.Count;
        if (meta.HistoryDone) meta.PreLiveEvents = bootstrap.Count;
        run.Sync();
        run.Save();
        log.LogInformation("Snapshot системы {Revision} ({RunId}, {Source}): клиентов {Customers}, услуг {Services}, начальных записей {Records}",
            spec.Revision, sim.W.RunId, spec.Source, sim.W.Accounts.Count, sim.W.Services.Count, bootstrap.Count);
        return run;
    }

    /// <summary>Довести snapshot системы до <paramref name="target"/> кусками по суткам; false — не успели за шаг или ждём отправку</summary>
    public bool CatchUp(DateTimeOffset target, TimeSpan budget, long maxBacklog)
    {
        target = Iso.Sec(target);
        var sw = Stopwatch.StartNew();
        var written = 0;
        var byType = new Dictionary<string, int>();
        while (Sim.W.Clock < target && sw.Elapsed < budget && Backlog < maxBacklog)
        {
            var next = Sim.W.Clock.AddDays(1) < target ? Sim.W.Clock.AddDays(1) : target;
            var buffer = new ListSink();
            Sim.AdvanceTo(next, buffer, Discard.Instance);
            if (buffer.Events.Count == 0) continue;
            using (var w = new JsonlWriter(OutboxPath, append: true))
            {
                foreach (var e in buffer.Events)
                {
                    w.Write(e);
                    byType[e.Type] = byType.GetValueOrDefault(e.Type) + 1;
                }
                w.Flush();
            }
            Meta.OutboxEvents += buffer.Events.Count;
            written += buffer.Events.Count;
        }
        if (!Meta.HistoryDone && Sim.W.Clock >= Iso.Sec(Spec.StartedAt))
        {
            Meta.HistoryDone = true;
            Meta.PreLiveEvents = Meta.OutboxEvents;
            _log.LogInformation("История snapshot системы {Revision} сгенерирована: событий {Events}", Spec.Revision, Meta.OutboxEvents - Meta.BootstrapEvents);
        }
        Sync();
        if (written > 0)
        {
            Save();
            if (Meta.HistoryDone)
                _log.LogInformation("Сгенерировано событий: {Count} ({Types})", written,
                    string.Join(", ", byType.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} {kv.Value}")));
        }
        return Sim.W.Clock >= target;
    }

    /// <summary>Применить событие из панели: записать результат в outbox и запомнить итог</summary>
    public CommandOutcome ApplyCommand(AgentCommand command)
    {
        var buffer = new ListSink();
        var refused = Sim.ApplyCommand(command.Type, command.AccountId, command.ServiceId, command.Data, buffer);
        if (buffer.Events.Count > 0)
        {
            using (var w = new JsonlWriter(OutboxPath, append: true))
            {
                foreach (var e in buffer.Events) w.Write(e);
                w.Flush();
            }
            Meta.OutboxEvents += buffer.Events.Count;
        }
        var outcome = buffer.Events.Count > 0
            ? new CommandOutcome { Status = "applied", EventIds = buffer.Events.Select(e => e.EventId).ToList() }
            : new CommandOutcome { Status = "rejected", Message = refused ?? "Snapshot системы не принял событие: условие для него не выполнено" };
        Meta.Commands[command.Id] = outcome;
        // Хранится только хвост итогов: старые уже приняты анализатором
        foreach (var old in Meta.Commands.Where(kv => kv.Value.Reported).OrderBy(kv => kv.Key).Take(Math.Max(0, Meta.Commands.Count - 500)).Select(kv => kv.Key).ToList())
            Meta.Commands.Remove(old);
        Sync();
        Save();
        return outcome;
    }

    /// <summary>Шум доставки — только в live, после начального состояния и истории</summary>
    public void SetChaos(bool chaos)
    {
        var on = chaos && Meta.HistoryDone && SentEvents >= Meta.PreLiveEvents;
        _senderOptions.Shuffle = on ? Sim.C.Noise.DeliveryShuffle : 0;
        _senderOptions.Duplicate = on ? Sim.C.Noise.DeliveryDuplicate : 0;
    }

    public void StartSender()
    {
        var t = _options.Sender;
        _senderOptions = new SenderOptions
        {
            BatchSize = _options.Batch, MaxBatchBytes = t.MaxBatchBytes, MinDelay = t.MinDelay, MaxDelay = t.MaxDelay, ClientErrorDelay = t.ClientErrorDelay,
            Idle = t.Idle, WatermarkEvery = t.WatermarkEvery, ChaosSeed = Spec.SeedValue + 7, World = Spec.Revision.ToString(CultureInfo.InvariantCulture),
        };
        Sender = new OutboxSender(_http, OutboxPath, CursorPath, Sim.W.RunId, _senderOptions, _log);
        _senderStop = new CancellationTokenSource();
        Sending = Sender.RunAsync(() => Interlocked.Read(ref _committed), () => new DateTimeOffset(Interlocked.Read(ref _clock), TimeSpan.Zero),
            stopWhenDrained: false, _senderStop.Token);
    }

    public async Task StopSenderAsync()
    {
        if (_senderStop is null || Sending is null) return;
        await _senderStop.CancelAsync();
        try
        {
            await Sending;
        }
        catch (OperationCanceledException)
        {
            // остановка
        }
        catch (Exception ex)
        {
            _log.LogWarning("Отправитель остановлен с ошибкой: {Message}", ex.Message);
        }
        _senderStop.Dispose();
        _senderStop = null;
        Sending = null;
    }

    /// <summary>Всё отправлено и outbox большой — начать его с нуля</summary>
    public async Task CompactIfDrainedAsync(long threshold)
    {
        if (Sender is null || Sim.W.OutboxLength < threshold || Sender.Cursor.Offset < Sim.W.OutboxLength) return;
        await StopSenderAsync();
        Compact();
        StartSender();
    }

    public void Save()
    {
        Sim.W.OutboxLength = File.Exists(OutboxPath) ? new FileInfo(OutboxPath).Length : 0;
        WriteState();
    }

    private void WriteState() => AtomicFile.Write(StatePath, "{\"agent\":" + JsonSerializer.Serialize(Meta) + ",\"sim\":" + Sim.Save() + "}");

    /// <summary>Сначала состояние (outbox пуст), потом файл и позиция: при сбое посередине открытие сведёт их снова</summary>
    private void Compact()
    {
        Sim.W.OutboxLength = 0;
        WriteState();
        Truncate(OutboxPath, 0);
        var cursor = Cursor.Load(CursorPath);
        cursor.Offset = 0;
        cursor.Events = Meta.OutboxEvents;
        cursor.Save(CursorPath);
        Sync();
    }

    private void Sync()
    {
        Interlocked.Exchange(ref _committed, File.Exists(OutboxPath) ? new FileInfo(OutboxPath).Length : 0);
        Interlocked.Exchange(ref _clock, Sim.W.Clock.UtcTicks);
    }

    private static void Truncate(string path, long length)
    {
        if (!File.Exists(path)) return;
        using var f = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        f.SetLength(length);
    }

    private sealed class Discard : ITruthSink
    {
        public static readonly Discard Instance = new();

        public void Write(JsonObject line)
        {
        }
    }
}
