using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.IntegrationTests.Base;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Aeza.Risk.IntegrationTests;

/// <summary>Поток событий источника: приём, правила монотонности, журнал, текущее состояние, пересчёт, снимок поверх событий, факты и исходы сроков, выгрузка и метрики — на реальном PostgreSQL</summary>
public sealed class EventStreamTests(PostgresFixture pg) : DbTestBase(pg)
{
    private static JsonObject Record(string kind, long id)
    {
        var doc = JsonNode.Parse(File.ReadAllText(TestConst.OrganizerData))!;
        return doc[kind]!.AsArray().Single(x => x!["id"]!.GetValue<long>() == id)!.DeepClone().AsObject();
    }

    private static object Event(string type, JsonObject data, string? eventId = null) =>
        eventId is null ? new { type, data } : new { eventId, type, data };

    private static HttpClient Ingest(AppFactory f)
    {
        var c = f.NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestConst.IngestToken);
        return c;
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, params object[] events) =>
        c.PostAsJsonAsync("/api/v1/source/events", new { events });

    [Fact]
    public async Task PostEvents_RequiresIngestTokenOrAdmin()
    {
        using var anon = Factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anon)).StatusCode);

        using var wrong = Factory.NewClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-an-ingest-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(wrong)).StatusCode);

        using var analyst = await Factory.LoginAsync(TestConst.AnalystLogin, TestConst.AnalystPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(analyst)).StatusCode);

        using var ingest = Ingest(Factory);
        Assert.Equal(HttpStatusCode.OK, (await Post(ingest)).StatusCode); // пустой пакет — ничего не применено
        var noEvents = await ingest.PostAsync("/api/v1/source/events", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noEvents.StatusCode);
    }

    [Fact]
    public async Task Event_ChangesCurrentState_Journal_AndRecalculates()
    {
        using var c = await Factory.LoginAsync();
        var first = await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var before = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!;
        Assert.Equal(0, first["generation"]!["sourceSeq"]!.GetValue<long>());
        Assert.Equal("snapshot", first["generation"]!["sourceKind"]!.GetValue<string>());

        var svc = Record("services", 1);
        svc["status"] = "prolong_wait";
        using var ingest = Ingest(Factory);
        var r = await Post(ingest, Event("service", svc, "evt-1"));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var body = await r.JsonAsync();
        Assert.Equal(1, body["applied"]!.GetValue<int>());
        Assert.Contains("status_changed", body["results"]![0]!["changes"]!.AsArray().Select(x => x!.GetValue<string>()));
        var seq = body["lastSeq"]!.GetValue<long>();

        var after = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() == seq);
        Assert.Equal("stream", after["generation"]!["sourceKind"]!.GetValue<string>());
        var detail = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!;
        Assert.Equal("prolong_wait", detail["services"]!.AsArray().Single(s => s!["id"]!.GetValue<long>() == 1)!["status"]!.GetValue<string>());
        Assert.True(detail["riskScore"]!.GetValue<decimal>() > before["riskScore"]!.GetValue<decimal>());

        var journal = await c.GetFromJsonAsync<JsonObject>("/api/v1/source/events");
        var item = journal!["items"]![0]!;
        Assert.Equal("applied", item["outcome"]!.GetValue<string>());
        Assert.Equal("prolong_wait", item["effect"]!["statusTo"]!.GetValue<string>());
        Assert.Null(item["payload"]); // состояние записи наружу не отдаётся
        var timeline = await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1/events");
        Assert.Single(timeline!["items"]!.AsArray());
    }

    [Fact]
    public async Task Duplicates_NoChange_Stale_AndRejected_AreSeparated()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        using var ingest = Ingest(Factory);
        var renewed = Record("services", 11);
        var expires = renewed["expiresAt"]!.GetValue<DateTimeOffset>();
        renewed["expiresAt"] = expires.AddMonths(1).ToString("O");
        var older = Record("services", 11);
        older["expiresAt"] = expires.AddDays(-1).ToString("O");
        var broken = Record("accounts", 1);
        broken["balance"] = "много";

        var r = await (await Post(ingest,
            Event("service", renewed, "e-1"),
            Event("service", renewed, "e-1"),
            Event("service", renewed),
            Event("service", older),
            Event("account", broken))).JsonAsync();

        Assert.Equal(1, r["applied"]!.GetValue<int>());
        Assert.Equal(1, r["duplicates"]!.GetValue<int>());
        Assert.Equal(1, r["noChange"]!.GetValue<int>());
        Assert.Equal(1, r["stale"]!.GetValue<int>());
        Assert.Equal(1, r["rejected"]!.GetValue<int>());
        Assert.Equal("expires_regressed", r["results"]![3]!["reason"]!.GetValue<string>());
        Assert.Contains("events[4].data.balance", r["errors"]!.AsObject().Select(k => k.Key));

        var again = await (await Post(ingest, Event("service", renewed, "e-1"))).JsonAsync();
        Assert.Equal(1, again["duplicates"]!.GetValue<int>());
        Assert.Equal(0, again["applied"]!.GetValue<int>());
    }

    [Fact]
    public async Task UnknownOwner_IsParked_UntilAccountArrives_WithoutPull()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var svc = Record("services", 1);
        svc["id"] = 900;
        svc["ownerId"] = 77;
        using var ingest = Ingest(Factory);
        var r = await (await Post(ingest, Event("service", svc))).JsonAsync();
        Assert.Contains("owner_unknown", r["results"]![0]!["changes"]!.AsArray().Select(x => x!.GetValue<string>()));
        var stream = await c.GetFromJsonAsync<JsonObject>("/api/v1/source/stream");
        Assert.Equal(1, stream!["orphanServices"]!.GetValue<int>());
        Assert.NotNull(stream["quality"]!["oldestOrphanAt"]);
        // Анализатор к источнику не ходит: только входящие коннекторы, API дозапроса нет
        var keys = stream["connectors"]!.AsArray().Select(x => x!["key"]!.GetValue<string>()).ToList();
        Assert.Equal(["http-push", "batch-import", "snapshot"], keys);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/v1/source/pull", new { accountId = 77 })).StatusCode);
        var after = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() > 0);
        Assert.Equal(10, after["customers"]!["total"]!.GetValue<int>()); // аккаунта 77 нет — клиент не придуман

        // Аккаунт пришёл потоком — запаркованная услуга начинает оцениваться
        var acc = Record("accounts", 1);
        acc["id"] = 77;
        acc["email"] = "user-0077@example.test";
        var seq = (await (await Post(ingest, Event("account", acc))).JsonAsync())["lastSeq"]!.GetValue<long>();
        var withOwner = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() == seq);
        Assert.Equal(11, withOwner["customers"]!["total"]!.GetValue<int>());
        Assert.Equal(0, (await c.GetFromJsonAsync<JsonObject>("/api/v1/source/stream"))!["orphanServices"]!.GetValue<int>());
    }

    [Fact]
    public async Task Snapshot_AfterEvents_ReplaysNewerEvents()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        using var ingest = Ingest(Factory);
        var svc = Record("services", 21);
        svc["status"] = "suspended";
        await Post(ingest, Event("service", svc));

        // Снимок со старым состоянием, выгруженный раньше события: событие не должно откатиться
        var doc = JsonNode.Parse(File.ReadAllText(TestConst.OrganizerData))!.AsObject();
        doc["schemaVersion"] = 1;
        doc["sourceAsOf"] = TestConst.SourceAsOf.AddMinutes(30).ToString("O");
        doc["accounts"]!.AsArray().Single(a => a!["id"]!.GetValue<long>() == 2)!["balance"] = 12345;
        var put = await c.PutRawAsync("/api/v1/source/snapshot", doc.ToJsonString());
        Assert.Equal(HttpStatusCode.Accepted, put.StatusCode);

        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceRevision"]!.GetValue<long>() == 2);
        var detail = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!;
        Assert.Equal("suspended", detail["services"]!.AsArray().Single(s => s!["id"]!.GetValue<long>() == 21)!["status"]!.GetValue<string>());
        var two = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/2"))!["detail"]!;
        Assert.Equal(12345, two["customer"]!["balance"]!.GetValue<long>());
    }

    [Fact]
    public async Task DeletingLastServices_IsFullChurnFact_AndChurnedState()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        using var ingest = Ingest(Factory);
        var events = new[] { 5L, 15L, 25L }.Select(id =>
        {
            var s = Record("services", id);
            s["status"] = "deleted";
            return Event("service", s);
        }).ToArray();
        var r = await (await Post(ingest, events)).JsonAsync();
        Assert.Contains("customer_lost_all_services", r["results"]![2]!["changes"]!.AsArray().Select(x => x!.GetValue<string>()));
        var seq = r["lastSeq"]!.GetValue<long>();

        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() == seq);
        var list = await c.GetFromJsonAsync<JsonObject>("/api/v1/customers?fullChurnState=churned&sortBy=id&sortDirection=asc");
        Assert.Contains(5L, list!["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()));
        Assert.Equal("churned", list["items"]!.AsArray().Single(i => i!["id"]!.GetValue<long>() == 5)!["churn"]!["fullChurn"]!["state"]!.GetValue<string>());

        var history = await c.GetFromJsonAsync<JsonObject>("/api/v1/analytics/history?days=7");
        var today = history!["days"]!.AsArray().Last()!;
        Assert.Equal(3, today["deleted"]!.GetValue<int>());
        Assert.Equal(1, today["customersLostAll"]!.GetValue<int>());
        Assert.NotNull(history["coverage"]!["streamStartedAt"]);
    }

    [Fact]
    public async Task ExpiryOutcome_RenewedWithinGrace_AndNotRenewedOnlyWithFreshSource()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        using var ingest = Ingest(Factory);
        var now = Factory.Time.GetUtcNow();

        // Две новые услуги клиента 1 со сроком через час: одна продлится, другая — нет
        var a = Record("services", 1);
        a["id"] = 901;
        a["expiresAt"] = now.AddHours(1).ToString("O");
        var b = Record("services", 11);
        b["id"] = 902;
        b["expiresAt"] = now.AddHours(1).ToString("O");
        await Post(ingest, Event("service", a), Event("service", b));

        // Сессия живёт 8 ч, а время уходит на дни вперёд — история читается новой сессией
        async Task<JsonObject> History()
        {
            using var fresh = await Factory.LoginAsync();
            return (await fresh.GetFromJsonAsync<JsonObject>("/api/v1/analytics/history"))!;
        }

        Factory.Time.Advance(TimeSpan.FromDays(2));
        a["expiresAt"] = now.AddHours(1).AddMonths(1).ToString("O");
        await Post(ingest, Event("service", a));
        var mid = await History();
        Assert.True(mid!["outcomes"]!["renewed"]!.GetValue<int>() >= 1);

        // Срок + 5 дней прошёл, но источник молчит — исход «не продлено» не фиксируется
        Factory.Time.Advance(TimeSpan.FromDays(5));
        await Task.Delay(TimeSpan.FromSeconds(3));
        var silent = await History();
        Assert.Equal(0, silent!["outcomes"]!["notRenewed"]!.GetValue<int>());

        // Источник снова прислал событие (свежий) — созревший срок закрывается как «не продлено»
        var account = Record("accounts", 4);
        account["balance"] = account["balance"]!.GetValue<long>() + 1;
        await Post(ingest, Event("account", account));
        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        int notRenewed;
        do
        {
            await Task.Delay(300);
            var h = await History();
            notRenewed = h!["outcomes"]!["notRenewed"]!.GetValue<int>();
        }
        while (notRenewed == 0 && DateTime.UtcNow < deadline);
        Assert.True(notRenewed >= 1);
    }

    [Fact]
    public async Task Export_Csv_IsManual_ForSessionUsersOnly()
    {
        using var analyst = await Factory.LoginAsync(TestConst.AnalystLogin, TestConst.AnalystPassword);
        var gen = await AppFactory.WaitForGenerationAsync(analyst, s => s["generation"] is not null);
        var csv = await analyst.GetAsync("/api/v1/export/customers?riskLevel=high&sortBy=riskScore");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Equal(gen["generation"]!["id"]!.GetValue<long>().ToString(), csv.Headers.GetValues("X-Risk-Generation").Single());
        var text = (await csv.Content.ReadAsStringAsync()).TrimStart('﻿');
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("generation_id,as_of,customer_id,email,risk_score,risk_level", lines[0]);
        Assert.Equal(gen["customers"]!["byLevel"]!["high"]!.GetValue<int>(), lines.Length - 1);

        // Выгрузка ручная: только пользователь панели; машинного доступа нет
        using var anonymous = Factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/export/customers")).StatusCode);
        using var ingest = Ingest(Factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ingest.GetAsync("/api/v1/export/customers")).StatusCode);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Audit.CountAsync(a => a.Action == "customers_exported") >= 1);
    }

    [Fact]
    public async Task Metrics_AreBusinessOnly_WithChurnIndicatorsAndFacts()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        using var ingest = Ingest(Factory);
        var svc = Record("services", 1);
        svc["autoProlong"] = !svc["autoProlong"]!.GetValue<bool>();
        await Post(ingest, Event("service", svc));
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() > 0);

        using var prom = Factory.NewClient();
        var text = await prom.GetStringAsync("/metrics");
        Assert.Matches("aeza_customers_full_churn\\{[^}]*risk_level=\"high\"", text);
        Assert.Matches("aeza_customers_payment_trend\\{[^}]*trend=\"insufficient_history\"", text);
        Assert.Matches("aeza_expiry_outcomes\\{[^}]*outcome=\"pending\"", text);
        Assert.Contains("aeza_source_age_seconds", text);
        // Только бизнес-метрики: ни процесса, ни runtime, ни HTTP
        Assert.DoesNotMatch("(?m)^(process_|dotnet_|http_|aspnetcore_|kestrel_|signalr_)", text);
        Assert.DoesNotContain("@example.test", text);
    }

    [Fact]
    public async Task ChurnSettings_ArePersisted_AndValidated()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var rev = await c.ModelRevisionAsync();
        var bad = await c.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, churn = new { paymentWindowDays = 3 } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var ok = await c.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, churn = new { nonRenewalDays = 7, paymentWindowDays = 60 } });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var settings = await c.GetFromJsonAsync<JsonObject>("/api/v1/model/settings");
        Assert.Equal(7, settings!["latest"]!["settings"]!["churn"]!["nonRenewalDays"]!.GetValue<int>());
        Assert.Equal(30m, settings["latest"]!["settings"]!["churn"]!["paymentDeclinePct"]!.GetValue<decimal>());
        await AppFactory.WaitForGenerationAsync(c, s => s["churn"]?["paymentTrend"]?["windowDays"]?.GetValue<int>() == 60);
    }
}
