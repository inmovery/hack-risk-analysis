using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Aeza.Risk.Core.Source;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Aeza.Risk.SourceSimulator.Tests;

/// <summary>Режим agent: snapshot системы из указаний анализатора, начальное состояние событиями v1, жизнь — v2</summary>
public sealed class AgentTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly string OrganizerPath = Path.Combine(AppContext.BaseDirectory, "organizer", "data.json");

    private static JsonObject Organizer() => JsonNode.Parse(File.ReadAllText(OrganizerPath))!.AsObject();

    private static string OrganizerHash => Dataset.Sha256(OrganizerPath);

    private static WorldSpec OrganizerSpec(int extra = 30, DateTimeOffset? at = null) => new(1, at ?? Start, WorldSpec.FromOrganizer, 7, extra, 0, 0);

    private static WorldSpec GeneratedSpec(int customers = 60, int days = 20, DateTimeOffset? at = null) => new(1, at ?? Start, WorldSpec.FromGenerator, 7, 0, customers, days);

    private static (Simulation Sim, List<string> Bootstrap) Build(WorldSpec spec) =>
        AgentWorld.Build(spec, new SimConfig(), spec.Generated ? null : Organizer(), spec.Generated ? null : OrganizerHash);

    private static JsonObject Data(string line) => JsonNode.Parse(line)!["data"]!.AsObject();

    private static List<SimEvent> Advance(Simulation sim, DateTimeOffset to)
    {
        var sink = new ListSink();
        sim.AdvanceTo(to, sink, new ListSink());
        return sink.Events;
    }

    [Fact]
    public void OrganizerWithExtraCustomers_IdsAndEmailsDoNotCollide()
    {
        var (sim, bootstrap) = Build(OrganizerSpec(extra: 50));
        var organizer = Organizer();
        var byType = bootstrap.GroupBy(l => JsonNode.Parse(l)!["type"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.Select(Data).ToList());

        var accounts = byType["account"];
        Assert.Equal(organizer["accounts"]!.AsArray().Count + 50, accounts.Count);
        Assert.Equal(accounts.Count, accounts.Select(a => a["id"]!.GetValue<long>()).Distinct().Count());
        Assert.Equal(accounts.Count, accounts.Select(a => a["email"]!.GetValue<string>()).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var organizerAccounts = organizer["accounts"]!.AsArray().Select(a => a!["id"]!.GetValue<long>()).ToHashSet();
        Assert.All(accounts.Where(a => !organizerAccounts.Contains(a["id"]!.GetValue<long>())), a => Assert.True(a["id"]!.GetValue<long>() >= 1001));

        var services = byType["service"];
        Assert.Equal(services.Count, services.Select(s => s["id"]!.GetValue<long>()).Distinct().Count());
        var organizerServices = organizer["services"]!.AsArray().Select(s => s!["id"]!.GetValue<long>()).ToHashSet();
        Assert.All(services.Where(s => !organizerServices.Contains(s["id"]!.GetValue<long>())), s => Assert.True(s["id"]!.GetValue<long>() >= 10001));

        var transactions = byType["transaction"].Select(t => t["id"]!.GetValue<long>()).ToList();
        Assert.Equal(transactions.Count, transactions.Distinct().Count());
        Assert.True(transactions.Count > organizer["transactions"]!.AsArray().Count);
        var organizerTransactions = organizer["transactions"]!.AsArray().Select(t => t!["id"]!.GetValue<long>()).ToHashSet();
        Assert.All(transactions.Where(t => !organizerTransactions.Contains(t)), t => Assert.True(t >= 100001));

        // Жизнь snapshot системы: новые клиенты, услуги и транзакции тоже не совпадают с уже существующими
        var events = Advance(sim, Start.AddDays(30));
        var newTransactions = events.Select(e => e.State?["transaction"]?["id"]?.GetValue<long>()).OfType<long>().ToList();
        Assert.NotEmpty(newTransactions);
        Assert.DoesNotContain(newTransactions, transactions.Contains);
        Assert.Equal(sim.W.Accounts.Count, sim.W.Accounts.Values.Select(a => a.Email).Distinct().Count());
    }

    [Theory]
    [InlineData(WorldSpec.FromOrganizer)]
    [InlineData(WorldSpec.FromGenerator)]
    public void Bootstrap_PassesAnalyzerRules_ThenLifeApplies(string source)
    {
        var spec = source == WorldSpec.FromGenerator ? GeneratedSpec() : OrganizerSpec();
        var (sim, bootstrap) = Build(spec);
        var state = new SourceState();
        var now = Start.AddDays(60);

        var boot = Feed(bootstrap, state, now);
        Assert.True(boot.Problems.Count == 0, string.Join("\n", boot.Problems.Take(20)));
        Assert.Equal(bootstrap.Count, boot.V1);
        Assert.Equal(0, boot.V2);
        Assert.Equal(state.Accounts.Count, sim.W.Accounts.Count);

        // generated: история до старта snapshot системы, затем жизнь; organizer — сразу жизнь
        var life = Advance(sim, Start.AddDays(14)).Select(e => e.ToJsonLine()).ToList();
        var result = Feed(life, state, now);
        Assert.True(result.Problems.Count == 0, string.Join("\n", result.Problems.Take(20)));
        Assert.Equal(life.Count, result.V2);
        Assert.True(result.Applied > 0);
        Assert.Contains(life, l => l.Contains("\"service.renewed\"", StringComparison.Ordinal) || l.Contains("\"payment.charged\"", StringComparison.Ordinal));
    }

    private sealed record FeedResult(List<string> Problems, int V1, int V2, int Applied);

    /// <summary>Пакеты как у анализатора: разбор, затем правила монотонности источника</summary>
    private static FeedResult Feed(List<string> lines, SourceState state, DateTimeOffset now)
    {
        var problems = new List<string>();
        int v1 = 0, v2 = 0, applied = 0;
        foreach (var chunk in lines.Chunk(1000))
        {
            var batch = EventBatchParser.Parse(Encoding.UTF8.GetBytes("{\"synthetic\":true,\"events\":[" + string.Join(',', chunk) + "]}"), now);
            Assert.True(batch.IsValid);
            Assert.False(batch.Warnings.ContainsKey("unknown_event_field"));
            foreach (var e in batch.Events)
            {
                if (!e.IsValid)
                {
                    problems.Add($"rejected {chunk[e.Index][..Math.Min(120, chunk[e.Index].Length)]}: {string.Join(",", e.Errors.Keys)}");
                    continue;
                }
                if (e.Domain is null) v1++;
                else v2++;
                foreach (var change in e.Changes)
                {
                    var r = state.Apply(change);
                    if (r.Outcome == ChangeOutcome.Stale) problems.Add($"{e.EventId}: stale {r.StaleReason}");
                    if (r.Outcome == ChangeOutcome.Applied) applied++;
                    if (r.Changes.Contains(ChangeCodes.OwnerUnknown) || r.Changes.Contains(ChangeCodes.ServiceUnknown))
                        problems.Add($"{e.EventId}: запись раньше владельца");
                }
            }
        }
        return new FeedResult(problems, v1, v2, applied);
    }

    [Fact]
    public void SameSettingsAndStart_GiveSameEvents()
    {
        foreach (var spec in new[] { GeneratedSpec(customers: 40, days: 10), OrganizerSpec(extra: 20) })
        {
            var (a, bootA) = Build(spec);
            var (b, bootB) = Build(spec);
            Assert.Equal(bootA, bootB);
            Assert.All(bootA, l => Assert.StartsWith($"{a.W.RunId}-boot-", JsonNode.Parse(l)!["eventId"]!.GetValue<string>(), StringComparison.Ordinal));
            var to = Start.AddDays(3);
            Assert.Equal(Advance(a, to).Select(e => e.ToJsonLine()), Advance(b, to).Select(e => e.ToJsonLine()));

            var (other, bootOther) = Build(spec with { StartedAt = Start.AddMinutes(1) });
            Assert.NotEqual(a.W.RunId, other.W.RunId);
            Assert.Empty(bootOther.Select(l => JsonNode.Parse(l)!["eventId"]!.GetValue<string>()).Intersect(bootA.Select(l => JsonNode.Parse(l)!["eventId"]!.GetValue<string>())));
        }
    }

    [Fact]
    public void Bootstrap_IsAccountsThenServicesThenTransactions()
    {
        var (_, bootstrap) = Build(OrganizerSpec(extra: 5));
        var types = bootstrap.Select(l => JsonNode.Parse(l)!["type"]!.GetValue<string>()).ToList();
        Assert.Equal(types.OrderBy(t => t switch { "account" => 0, "service" => 1, _ => 2 }), types);
        Assert.All(bootstrap, l => Assert.Matches(@"-boot-[ast]-\d+$", JsonNode.Parse(l)!["eventId"]!.GetValue<string>()));
    }

    // ---------- Агент против фейкового анализатора ----------

    private sealed class FakeAnalyzer : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private JsonObject _instructions = [];
        private readonly List<(string? World, List<JsonObject> Events)> _batches = [];
        private readonly List<JsonObject> _statuses = [];
        private readonly List<JsonObject> _commandReports = [];

        public List<JsonObject> CommandReports
        {
            get
            {
                lock (_lock) return [.. _commandReports];
            }
        }

        public JsonObject Instructions
        {
            get
            {
                lock (_lock) return (JsonObject)_instructions.DeepClone();
            }
            set
            {
                lock (_lock) _instructions = value;
            }
        }

        public List<(string? World, List<JsonObject> Events)> Batches
        {
            get
            {
                lock (_lock) return [.. _batches];
            }
        }

        public List<JsonObject> Statuses
        {
            get
            {
                lock (_lock) return [.. _statuses];
            }
        }

        /// <summary>События без водяных знаков, по порядку приёма</summary>
        public List<(string? World, JsonObject Event)> Events =>
            Batches.SelectMany(b => b.Events.Select(e => (b.World, e))).Where(x => x.e["type"]!.GetValue<string>() != "stream.watermark").ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == SimulationAgent.InstructionsPath) return Json(HttpStatusCode.OK, Instructions);
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (path == SimulationAgent.CommandsPath)
            {
                lock (_lock) _commandReports.AddRange(JsonNode.Parse(body)!.AsArray().Select(x => x!.AsObject()));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path == SimulationAgent.StatusPath)
            {
                lock (_lock) _statuses.Add(JsonNode.Parse(body)!.AsObject());
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path != "/api/v1/source/events") return new HttpResponseMessage(HttpStatusCode.NotFound);
            var world = request.Headers.TryGetValues(OutboxSender.WorldHeader, out var values) ? values.Single() : null;
            var current = Instructions["world"]?["revision"]?.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (world is not null && world != current)
                return Json(HttpStatusCode.Conflict, new JsonObject { ["code"] = "simulation_world_changed", ["title"] = "Snapshot системы симуляции сменился" });
            var events = JsonNode.Parse(body)!["events"]!.AsArray().Select(e => e!.AsObject()).ToList();
            lock (_lock) _batches.Add((world, events));
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["received"] = events.Count, ["applied"] = events.Count, ["recorded"] = 0, ["duplicates"] = 0, ["rejected"] = 0, ["stale"] = 0,
            });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
            new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static JsonObject Instructions(
        bool eventsOnly, long revision, DateTimeOffset startedAt, string source = WorldSpec.FromOrganizer, int extra = 10, int customers = 20, int days = 2,
        bool running = true, long scenario = 0)
    {
        var world = new JsonObject { ["source"] = source, ["seed"] = 7, ["extraCustomers"] = extra, ["customers"] = customers, ["historyDays"] = days };
        return new JsonObject
        {
            ["eventsOnly"] = eventsOnly,
            ["settings"] = new JsonObject { ["running"] = running, ["tickSeconds"] = 1, ["scenarioStepSeconds"] = 15, ["chaos"] = false, ["world"] = world.DeepClone() },
            ["world"] = new JsonObject { ["revision"] = revision, ["startedAt"] = Iso.Format(startedAt), ["settings"] = world },
            ["scenarioRequest"] = scenario,
        };
    }

    private static SimulationAgent Agent(FakeAnalyzer fake, string dir) => new(
        new HttpClient(fake) { BaseAddress = new Uri("http://analyzer") },
        new AgentOptions
        {
            Dir = dir, Init = [OrganizerPath], Poll = TimeSpan.FromMilliseconds(10), StatusEvery = TimeSpan.Zero,
            Sender = new SenderOptions
            {
                MinDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5), ClientErrorDelay = TimeSpan.FromMilliseconds(5),
                Idle = TimeSpan.FromMilliseconds(10), WatermarkEvery = TimeSpan.FromHours(1),
            },
        },
        NullLogger.Instance);

    private static async Task Until(SimulationAgent agent, Func<bool> done, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            await agent.StepAsync(CancellationToken.None);
            if (done()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"Не дождались: {what} (фаза {agent.Phase}, ошибка {agent.LastError})");
    }

    [Fact]
    public async Task EventsOnlyOff_AgentIsIdle_AndSendsNothing()
    {
        var dir = GenerationTests.TempDir();
        var fake = new FakeAnalyzer { Instructions = Instructions(eventsOnly: false, 1, DateTimeOffset.UtcNow.AddHours(-1)) };
        await using var agent = Agent(fake, dir);
        for (var i = 0; i < 3; i++) await agent.StepAsync(CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(AgentPhases.Idle, agent.Phase);
        Assert.Null(agent.Sim);
        Assert.Empty(fake.Batches);
        Assert.Empty(Directory.GetDirectories(dir, "world-*"));
        Assert.All(fake.Statuses, s => Assert.Equal("idle", s["phase"]!.GetValue<string>()));
        Assert.Equal(0, fake.Statuses[^1]["eventsGenerated"]!.GetValue<long>());
    }

    [Fact]
    public async Task RevisionChange_BuildsNewWorld_AndOldWorldIsDropped()
    {
        var dir = GenerationTests.TempDir();
        var fake = new FakeAnalyzer { Instructions = Instructions(eventsOnly: true, 1, DateTimeOffset.UtcNow.AddHours(-1)) };
        await using var agent = Agent(fake, dir);
        await Until(agent, () => agent.Phase == AgentPhases.Live, "snapshot системы 1 в live");

        var first = agent.Sim!.W.RunId;
        var firstDir = agent.WorldDir!;
        var sent = fake.Events;
        Assert.All(sent, x => Assert.Equal("1", x.World));
        var boot = sent.Where(x => x.Event["eventId"]!.GetValue<string>().Contains("-boot-", StringComparison.Ordinal)).Select(x => x.Event["type"]!.GetValue<string>()).ToList();
        Assert.Equal(10 + 10, boot.Count(t => t == "account")); // 10 из файла организаторов + 10 добавленных
        Assert.Equal(boot.OrderBy(t => t switch { "account" => 0, "service" => 1, _ => 2 }), boot);

        fake.Instructions = Instructions(eventsOnly: true, 2, DateTimeOffset.UtcNow.AddMinutes(-10), WorldSpec.FromGenerator, customers: 20, days: 2);
        await Until(agent, () => agent.Revision == 2 && agent.Phase == AgentPhases.Live, "snapshot системы 2 в live");

        Assert.NotEqual(first, agent.Sim!.W.RunId);
        Assert.False(Directory.Exists(firstDir));
        Assert.True(Directory.Exists(Path.Combine(dir, "world-2")));
        var second = fake.Events.Where(x => x.World == "2").ToList();
        Assert.NotEmpty(second);
        Assert.All(second, x => Assert.StartsWith(agent.Sim.W.RunId + "-", x.Event["eventId"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.Contains(second, x => x.Event["schemaVersion"]?.GetValue<int>() == 2); // история и жизнь — доменные события
        await Until(agent, () => fake.Statuses[^1]["backlog"]!.GetValue<long>() == 0, "всё отправлено");
        var status = fake.Statuses[^1];
        Assert.Equal(2, status["worldRevision"]!.GetValue<long>());
        Assert.Equal(agent.Sim.W.RunId, status["runId"]!.GetValue<string>());
        Assert.Equal("live", status["phase"]!.GetValue<string>());
        Assert.Equal(status["eventsGenerated"]!.GetValue<long>(), status["eventsSent"]!.GetValue<long>());
    }

    [Fact]
    public async Task ComposedEvents_AreAppliedOnce_ContinueAccountSeq_UnfitOnesRejected()
    {
        var dir = GenerationTests.TempDir();
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var fake = new FakeAnalyzer { Instructions = Instructions(eventsOnly: true, 1, startedAt, extra: 10) };
        await using var agent = Agent(fake, dir);
        await Until(agent, () => agent.Phase == AgentPhases.Live, "live");
        var account = agent.Sim!.W.Agents.Values.First(a => !a.Gone && a.ServiceIds.Count > 0);
        var seq = account.Seq;

        var withCommands = Instructions(eventsOnly: true, 1, startedAt, extra: 10);
        withCommands["commands"] = new JsonArray(
            new JsonObject { ["id"] = 1, ["type"] = "balance.topped_up", ["accountId"] = account.AccountId, ["data"] = new JsonObject { ["amount"] = 1500 } },
            new JsonObject { ["id"] = 2, ["type"] = "service.resumed", ["accountId"] = account.AccountId, ["serviceId"] = 999_999, ["data"] = new JsonObject() });
        fake.Instructions = withCommands;
        await Until(agent, () => fake.CommandReports.Count >= 2, "итоги событий из панели");

        var applied = fake.CommandReports.Single(r => r["id"]!.GetValue<long>() == 1);
        Assert.Equal("applied", applied["status"]!.GetValue<string>());
        var eventId = applied["eventIds"]![0]!.GetValue<string>();
        await Until(agent, () => fake.Events.Any(e => e.Event["eventId"]!.GetValue<string>() == eventId), "событие в потоке");
        var sent = fake.Events.Single(e => e.Event["eventId"]!.GetValue<string>() == eventId).Event;
        Assert.Equal("balance.topped_up", sent["type"]!.GetValue<string>());
        Assert.True(sent["accountSeq"]!.GetValue<long>() > seq); // номер продолжает последовательность аккаунта
        Assert.Equal(1500, sent["data"]!["amount"]!.GetValue<long>());
        var rejected = fake.CommandReports.Single(r => r["id"]!.GetValue<long>() == 2);
        Assert.Equal("rejected", rejected["status"]!.GetValue<string>());
        Assert.Contains("999999", rejected["message"]!.GetValue<string>());

        // Те же указания ещё раз — событие не применяется повторно
        for (var i = 0; i < 3; i++) await agent.StepAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.Single(fake.CommandReports, r => r["id"]!.GetValue<long>() == 1);
        Assert.Single(fake.Events, e => e.Event["eventId"]!.GetValue<string>() == eventId);
    }

    [Fact]
    public async Task ScenarioRequest_IsHandledOnce_PauseStopsTheWorld()
    {
        var dir = GenerationTests.TempDir();
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var fake = new FakeAnalyzer { Instructions = Instructions(eventsOnly: true, 1, startedAt, extra: 30) };
        await using var agent = Agent(fake, dir);
        await Until(agent, () => agent.Phase == AgentPhases.Live, "live");
        Assert.Equal(0, agent.ScenarioHandled);

        fake.Instructions = Instructions(eventsOnly: true, 1, startedAt, extra: 30, scenario: 1);
        await Until(agent, () => agent.ScenarioHandled == 1, "сценарий");
        Assert.StartsWith("Сценарий: клиент", agent.LastNarration);
        var narration = agent.LastNarration;
        await agent.StepAsync(CancellationToken.None);
        Assert.Equal(1, agent.ScenarioHandled);
        Assert.Equal(narration, agent.LastNarration); // второй раз не запускается, первый шаг — через 15 с
        Assert.Equal(1, fake.Statuses[^1]["scenarioHandled"]!.GetValue<long>());
        Assert.Equal(narration, fake.Statuses[^1]["lastNarration"]!.GetValue<string>());
        Assert.Contains("\"scenarioHandled\":1", File.ReadAllText(Path.Combine(dir, "agent.json")));

        fake.Instructions = Instructions(eventsOnly: true, 1, startedAt, extra: 30, scenario: 1, running: false);
        await agent.StepAsync(CancellationToken.None);
        var clock = agent.Sim!.W.Clock;
        await Task.Delay(1200);
        await agent.StepAsync(CancellationToken.None);
        Assert.Equal(AgentPhases.Paused, agent.Phase);
        Assert.Equal(clock, agent.Sim.W.Clock);

        fake.Instructions = Instructions(eventsOnly: true, 1, startedAt, extra: 30, scenario: 1);
        await Until(agent, () => agent.Sim.W.Clock > clock, "snapshot системы догоняет время после паузы");
        Assert.Equal(AgentPhases.Live, agent.Phase);
    }

    [Fact]
    public async Task Restart_ContinuesWorld_LostStateReplaysSameEvents()
    {
        var dir = GenerationTests.TempDir();
        var fake = new FakeAnalyzer { Instructions = Instructions(eventsOnly: true, 1, DateTimeOffset.UtcNow.AddHours(-1)) };
        string runId;
        List<string> bootIds;
        await using (var agent = Agent(fake, dir))
        {
            await Until(agent, () => agent.Phase == AgentPhases.Live, "live");
            runId = agent.Sim!.W.RunId;
            bootIds = BootIds(fake);
        }

        // Перезапуск: тот же snapshot системы, начальное состояние не отправляется заново
        await using (var agent = Agent(fake, dir))
        {
            await Until(agent, () => agent.Phase == AgentPhases.Live, "live после перезапуска");
            Assert.Equal(runId, agent.Sim!.W.RunId);
            Assert.Equal(bootIds, BootIds(fake));
        }

        // Потеря локального состояния: snapshot системы строится заново и отправляет те же события (анализатор отбросит повторы по eventId)
        Directory.Delete(Path.Combine(dir, "world-1"), recursive: true);
        await using (var agent = Agent(fake, dir))
        {
            await Until(agent, () => agent.Phase == AgentPhases.Live, "live после потери состояния");
            Assert.Equal(runId, agent.Sim!.W.RunId);
            Assert.Equal(bootIds.Concat(bootIds), BootIds(fake));
        }
    }

    private static List<string> BootIds(FakeAnalyzer fake) =>
        fake.Events.Select(x => x.Event["eventId"]!.GetValue<string>()).Where(id => id.Contains("-boot-", StringComparison.Ordinal)).ToList();

    [Fact]
    public void Instructions_AreParsedFromWorldOnly()
    {
        var json = Instructions(eventsOnly: true, 5, Start, WorldSpec.FromGenerator, customers: 300, days: 30);
        json["settings"]!["world"]!["customers"] = 999; // черновик настроек не влияет на действующий snapshot системы
        var i = AgentInstructions.Parse(json);
        Assert.True(i.EventsOnly);
        Assert.Equal(new WorldSpec(5, Start, WorldSpec.FromGenerator, 7, 10, 300, 30), i.World);
    }
}
