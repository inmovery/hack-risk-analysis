using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.IntegrationTests.Base;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;

namespace Aeza.Risk.IntegrationTests;

/// <summary>Режим работы только на событиях: пустая база, настройки симуляции, связь с симулятором, пересоздание snapshot системы и отсечение пакетов старого snapshot системы</summary>
public sealed class SimulationTests(PostgresFixture pg) : DbTestBase(pg)
{
    protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string> { ["Simulation:EventsOnly"] = "true" };

    private static JsonObject Record(string kind, long id)
    {
        var doc = JsonNode.Parse(File.ReadAllText(TestConst.OrganizerData))!;
        return doc[kind]!.AsArray().Single(x => x!["id"]!.GetValue<long>() == id)!.DeepClone().AsObject();
    }

    private static HttpClient Agent(AppFactory f)
    {
        var c = f.NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestConst.IngestToken);
        return c;
    }

    private static Task<HttpResponseMessage> PostWorld(HttpClient c, long? world, params object[] events)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/source/events") { Content = JsonContent.Create(new { synthetic = true, events }) };
        if (world is { } w) req.Headers.Add("X-Simulation-World", w.ToString());
        return c.SendAsync(req);
    }

    /// <summary>Аккаунт 1 и его услуги — так симулятор передаёт начальный snapshot системы</summary>
    private static object[] Customer1()
    {
        var doc = JsonNode.Parse(File.ReadAllText(TestConst.OrganizerData))!;
        var services = doc["services"]!.AsArray().Where(s => s!["ownerId"]!.GetValue<long>() == 1).Select(s => s!.DeepClone().AsObject()).ToList();
        return [new { type = "account", data = Record("accounts", 1) }, .. services.Select(s => new { type = "service", data = s })];
    }

    private static object ValidSettings(int tick = 7, string source = "organizer", int extra = 50) => new
    {
        running = true, tickSeconds = tick, scenarioStepSeconds = 15, chaos = false,
        world = new { source, seed = 42, extraCustomers = extra, customers = 300, historyDays = 90 },
    };

    [Fact]
    public async Task EventsOnly_StartsEmpty_AndBuildsCustomersFromEvents()
    {
        using var c = await Factory.LoginAsync();
        var empty = await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        Assert.Equal(0, empty["generation"]!["sourceSeq"]!.GetValue<long>());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/customers/1")).StatusCode);

        var sim = (await c.GetFromJsonAsync<JsonObject>("/api/v1/simulation"))!;
        Assert.True(sim["eventsOnly"]!.GetValue<bool>());
        Assert.Equal(1, sim["world"]!["revision"]!.GetValue<long>());

        using var agent = Agent(Factory);
        var r = await PostWorld(agent, 1, Customer1());
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var seq = (await r.JsonAsync())["lastSeq"]!.GetValue<long>();

        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() == seq);
        var detail = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!;
        Assert.NotEmpty(detail["services"]!.AsArray());
    }

    [Fact]
    public async Task Settings_AreValidated_Persisted_AndWorldChangeIsPending()
    {
        using var c = await Factory.LoginAsync();
        var bad = await c.PutAsJsonAsync("/api/v1/simulation/settings", ValidSettings(tick: 0, extra: 99999));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var errors = (await bad.JsonAsync())["errors"]!.AsObject();
        Assert.True(errors.ContainsKey("tickSeconds"));
        Assert.True(errors.ContainsKey("world.extraCustomers"));

        var ok = await c.PutAsJsonAsync("/api/v1/simulation/settings", ValidSettings(tick: 7, source: "generated"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var view = (await c.GetFromJsonAsync<JsonObject>("/api/v1/simulation"))!;
        Assert.Equal(7, view["settings"]!["tickSeconds"]!.GetValue<int>());
        Assert.Equal("admin", view["updatedBy"]!.GetValue<string>());
        Assert.True(view["pending"]!["worldChanged"]!.GetValue<bool>());
        Assert.Equal("organizer", view["world"]!["settings"]!["source"]!.GetValue<string>());

        using var agent = Agent(Factory);
        var instructions = (await agent.GetFromJsonAsync<JsonObject>("/api/v1/simulation/agent"))!;
        Assert.Equal(7, instructions["settings"]!["tickSeconds"]!.GetValue<int>());
        Assert.Equal("organizer", instructions["world"]!["settings"]!["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reset_WipesSourceData_AndRejectsOldWorldBatches()
    {
        using var c = await Factory.LoginAsync();
        using var agent = Agent(Factory);
        Assert.Equal(HttpStatusCode.Accepted, (await PostWorld(agent, 1, Customer1())).StatusCode);
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() > 0);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/customers/1")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/v1/simulation/settings", ValidSettings(source: "generated"))).StatusCode);
        var reset = await c.PostAsync("/api/v1/simulation/reset", null);
        Assert.Equal(HttpStatusCode.Accepted, reset.StatusCode);
        var view = await reset.JsonAsync();
        Assert.Equal(2, view["world"]!["revision"]!.GetValue<long>());
        Assert.Equal("generated", view["world"]!["settings"]!["source"]!.GetValue<string>());
        Assert.False(view["pending"]!["worldChanged"]!.GetValue<bool>());

        // Пакет старого snapshot системы, отправленный после пересоздания, не применяется
        var stale = await PostWorld(agent, 1, Customer1());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("simulation_world_changed", (await stale.JsonAsync())["code"]!.GetValue<string>());

        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() == 0);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/v1/customers/1")).StatusCode);

        var fresh = await PostWorld(agent, 2, Customer1());
        Assert.Equal(HttpStatusCode.Accepted, fresh.StatusCode);
        var seq = (await fresh.JsonAsync())["lastSeq"]!.GetValue<long>();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() == seq);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/customers/1")).StatusCode);
    }

    [Fact]
    public async Task Reset_WorksUnderDmlOnlyAppRole()
    {
        using (var c = await Factory.LoginAsync()) await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Pg.ConnectionString(Database)).Options))
            await DbRoles.EnsureAppRoleAsync(db, "aeza_app_sim", "app-role-sim-pass", NullLogger.Instance);
        var cs = new NpgsqlConnectionStringBuilder(Pg.ConnectionString(Database)) { Username = "aeza_app_sim", Password = "app-role-sim-pass" }.ConnectionString;
        await using var f = new AppFactory(cs, settings: new Dictionary<string, string> { ["Simulation:EventsOnly"] = "true", ["App:AutoMigrate"] = "false" });

        using var admin = await f.LoginAsync();
        using var agent = Agent(f);
        Assert.Equal(HttpStatusCode.Accepted, (await PostWorld(agent, 1, Customer1())).StatusCode);
        var reset = await admin.PostAsync("/api/v1/simulation/reset", null);
        Assert.True(reset.StatusCode == HttpStatusCode.Accepted, await reset.Content.ReadAsStringAsync());
        await AppFactory.WaitForGenerationAsync(admin, s => s["generation"]!["sourceSeq"]!.GetValue<long>() == 0);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/customers/1")).StatusCode);
    }

    [Fact]
    public async Task AgentEndpoints_RequireIngestToken_AndReportIsShownToPanel()
    {
        using var anon = Factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/simulation/agent")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/simulation")).StatusCode);

        using var admin = await Factory.LoginAsync();
        Assert.NotEqual(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/simulation/agent")).StatusCode);

        using var agent = Agent(Factory);
        // Токен симулятора не открывает панель и управление
        Assert.NotEqual(HttpStatusCode.OK, (await agent.GetAsync("/api/v1/simulation")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Accepted, (await agent.PostAsync("/api/v1/simulation/reset", null)).StatusCode);

        var badPhase = await agent.PostAsJsonAsync("/api/v1/simulation/agent/status", Status("dancing"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badPhase.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsJsonAsync("/api/v1/simulation/agent/status", Status("live"))).StatusCode);

        var view = (await admin.GetFromJsonAsync<JsonObject>("/api/v1/simulation"))!;
        Assert.True(view["agent"]!["online"]!.GetValue<bool>());
        Assert.Equal("live", view["agent"]!["status"]!["phase"]!.GetValue<string>());
        Assert.Equal(120, view["agent"]!["status"]!["eventsSent"]!.GetValue<long>());

        Factory.Time.Advance(TimeSpan.FromMinutes(5));
        view = (await admin.GetFromJsonAsync<JsonObject>("/api/v1/simulation"))!;
        Assert.False(view["agent"]!["online"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Analyst_ReadsSimulation_ButCannotChangeIt()
    {
        using var analyst = await Factory.LoginAsync(TestConst.AnalystLogin, TestConst.AnalystPassword);
        Assert.Equal(HttpStatusCode.OK, (await analyst.GetAsync("/api/v1/simulation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PutAsJsonAsync("/api/v1/simulation/settings", ValidSettings())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsync("/api/v1/simulation/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsync("/api/v1/simulation/scenario", null)).StatusCode);
    }

    [Fact]
    public async Task ControlRequiresCsrf_AndScenarioRequestReachesAgent()
    {
        using var forged = await Factory.LoginAsync();
        forged.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        forged.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "forged");
        Assert.Equal(HttpStatusCode.Forbidden, (await forged.PostAsync("/api/v1/simulation/scenario", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await forged.PostAsync("/api/v1/simulation/reset", null)).StatusCode);

        using var admin = await Factory.LoginAsync();

        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/v1/simulation/scenario", null)).StatusCode);
        using var agent = Agent(Factory);
        var instructions = (await agent.GetFromJsonAsync<JsonObject>("/api/v1/simulation/agent"))!;
        Assert.Equal(1, instructions["scenarioRequest"]!.GetValue<long>());
    }

    [Fact]
    public async Task WithoutEventsOnly_SeedIsLoaded_AndResetIsRefused()
    {
        await using var f = new AppFactory(Pg.ConnectionString(Database + "_seed"));
        using var c = await f.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/customers/1")).StatusCode);
        var view = (await c.GetFromJsonAsync<JsonObject>("/api/v1/simulation"))!;
        Assert.False(view["eventsOnly"]!.GetValue<bool>());
        var reset = await c.PostAsync("/api/v1/simulation/reset", null);
        Assert.Equal(HttpStatusCode.Conflict, reset.StatusCode);
        Assert.Equal("events_only_disabled", (await reset.JsonAsync())["code"]!.GetValue<string>());
    }

    private static object Status(string phase) => new
    {
        agentVersion = "test", worldRevision = 1, runId = "w1", phase, simClock = TestConst.Now, customers = 10, services = 20, liveServices = 15,
        eventsGenerated = 150, eventsSent = 120, backlog = 30, lastSentAt = TestConst.Now, lastError = (string?)null, lastNarration = "шаг 1",
        scenarioHandled = 0,
    };

    [Fact]
    public async Task ComposedEvent_QueuesForSimulator_ResultIsReported_ResetRejectsPending()
    {
        using var admin = await Factory.LoginAsync();
        using var agent = Agent(Factory);
        Assert.Equal(HttpStatusCode.Accepted, (await PostWorld(agent, 1, Customer1())).StatusCode);
        await AppFactory.WaitForGenerationAsync(admin, s => s["generation"]!["sourceSeq"]!.GetValue<long>() > 0);

        var types = (await admin.GetFromJsonAsync<JsonArray>("/api/v1/simulation/events/types"))!;
        Assert.Contains(types, t => t!["type"]!.GetValue<string>() == "balance.topped_up");

        // Не собрано: нет суммы, нет услуги для события услуги, чужой клиент
        var bad = await admin.PostAsJsonAsync("/api/v1/simulation/events", new { type = "balance.topped_up", accountId = 1, data = new { } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.True((await bad.JsonAsync())["errors"]!.AsObject().ContainsKey("data.amount"));
        var noService = await admin.PostAsJsonAsync("/api/v1/simulation/events", new { type = "service.suspended", accountId = 1, data = new { } });
        Assert.True((await noService.JsonAsync())["errors"]!.AsObject().ContainsKey("serviceId"));
        var stranger = await admin.PostAsJsonAsync("/api/v1/simulation/events", new { type = "account.login", accountId = 999_999, data = new { } });
        Assert.True((await stranger.JsonAsync())["errors"]!.AsObject().ContainsKey("accountId"));

        using var analyst = await Factory.LoginAsync(TestConst.AnalystLogin, TestConst.AnalystPassword);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await analyst.PostAsJsonAsync("/api/v1/simulation/events", new { type = "account.login", accountId = 1, data = new { } })).StatusCode);

        var ok = await admin.PostAsJsonAsync("/api/v1/simulation/events", new { type = "balance.topped_up", accountId = 1, data = new { amount = 12.5 } });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var command = await ok.JsonAsync();
        var id = command["id"]!.GetValue<long>();
        Assert.Equal("pending", command["status"]!.GetValue<string>());
        Assert.Equal(1250, command["data"]!["amount"]!.GetValue<long>()); // евро → еврокопейки

        // Симулятор видит событие в указаниях и сообщает итог
        var instructions = (await agent.GetFromJsonAsync<JsonObject>("/api/v1/simulation/agent"))!;
        var queued = instructions["commands"]!.AsArray().Single()!;
        Assert.Equal(id, queued["id"]!.GetValue<long>());
        Assert.Equal("balance.topped_up", queued["type"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsJsonAsync("/api/v1/simulation/agent/commands",
            new[] { new { id, status = "applied", eventIds = new[] { "w1-000000042" }, message = (string?)null } })).StatusCode);
        var list = (await admin.GetFromJsonAsync<JsonArray>("/api/v1/simulation/events"))!;
        var applied = list.Single(c => c!["id"]!.GetValue<long>() == id)!;
        Assert.Equal("applied", applied["status"]!.GetValue<string>());
        Assert.Equal("w1-000000042", applied["eventIds"]![0]!.GetValue<string>());
        Assert.Empty((await agent.GetFromJsonAsync<JsonObject>("/api/v1/simulation/agent"))!["commands"]!.AsArray());

        // Пересоздание snapshot системы: ждущие события больше не применятся
        var waiting = await (await admin.PostAsJsonAsync("/api/v1/simulation/events", new { type = "account.login", accountId = 1, data = new { } })).JsonAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/v1/simulation/reset", null)).StatusCode);
        var afterReset = (await admin.GetFromJsonAsync<JsonArray>("/api/v1/simulation/events"))!.Single(c => c!["id"]!.GetValue<long>() == waiting["id"]!.GetValue<long>())!;
        Assert.Equal("rejected", afterReset["status"]!.GetValue<string>());
    }
}
