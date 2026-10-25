using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.IntegrationTests.Base;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Aeza.Risk.IntegrationTests;

/// <summary>Доменные события v2: состояние из state, поведенческие события в журнале, дедупликация по eventId, пропуски accountSeq, водяной знак для исходов сроков, история раньше базы, именованные коннекторы — на реальном PostgreSQL</summary>
public sealed class DomainEventStreamTests(PostgresFixture pg) : DbTestBase(pg)
{
    private const string CrmToken = "crm-export-token-0123456789";

    protected override IReadOnlyDictionary<string, string>? Settings => new Dictionary<string, string>
    {
        ["Auth:IngestConnectors:crm-export"] = CrmToken,
    };

    private static JsonObject Record(string kind, long id)
    {
        var doc = JsonNode.Parse(File.ReadAllText(TestConst.OrganizerData))!;
        return doc[kind]!.AsArray().Single(x => x!["id"]!.GetValue<long>() == id)!.DeepClone().AsObject();
    }

    private static HttpClient Ingest(AppFactory f, string token = TestConst.IngestToken)
    {
        var c = f.NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static async Task<(HttpStatusCode Status, JsonObject Body)> Post(HttpClient c, params object[] events)
    {
        var r = await c.PostAsJsonAsync("/api/v1/source/events", new { events });
        return (r.StatusCode, await r.JsonAsync());
    }

    private static object V2(string eventId, string type, DateTimeOffset occurredAt, long? accountId = null, long? accountSeq = null,
        long? serviceId = null, object? data = null, object? state = null) =>
        new { schemaVersion = 2, eventId, type, occurredAt, accountId, accountSeq, serviceId, data = data ?? new { }, state };

    private async Task<JsonObject> Stream(HttpClient c) => (await c.GetFromJsonAsync<JsonObject>("/api/v1/source/stream"))!;

    [Fact]
    public async Task StateCarried_Applies_Journal_HasTypeAndOccurredAt_AndDuplicateIsIgnored()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var now = Factory.Time.GetUtcNow();
        var svc = Record("services", 1);
        svc["status"] = "prolong_wait";
        var occurred = now.AddMinutes(-2);
        using var ingest = Ingest(Factory);

        var (status, body) = await Post(ingest, V2("v2-exp-1", "service.expired", occurred, 1, 1, 1, new { reason = "no_funds" }, new { service = svc }));
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(1, body["applied"]!.GetValue<int>());
        Assert.Equal("applied", body["results"]![0]!["outcome"]!.GetValue<string>());
        Assert.Contains("status_changed", body["results"]![0]!["changes"]!.AsArray().Select(x => x!.GetValue<string>()));
        var seq = body["lastSeq"]!.GetValue<long>();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() == seq);
        var detail = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!;
        Assert.Equal("prolong_wait", detail["services"]!.AsArray().Single(s => s!["id"]!.GetValue<long>() == 1)!["status"]!.GetValue<string>());

        var item = (await c.GetFromJsonAsync<JsonObject>("/api/v1/source/events"))!["items"]![0]!;
        Assert.Equal("service.expired", item["eventType"]!.GetValue<string>());
        Assert.Equal(occurred, item["occurredAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(now, item["receivedAt"]!.GetValue<DateTimeOffset>());

        // Повтор того же eventId (ретрай источника) — дубликат, состояние и журнал не меняются
        var (dupStatus, dup) = await Post(ingest, V2("v2-exp-1", "service.expired", occurred, 1, 1, 1, new { reason = "no_funds" }, new { service = svc }));
        Assert.Equal(HttpStatusCode.OK, dupStatus);
        Assert.Equal(1, dup["duplicates"]!.GetValue<int>());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var domain = await db.SourceDomainEvents.SingleAsync(e => e.EventId == "v2-exp-1");
        Assert.Equal("applied", domain.Outcome);
        Assert.Equal("http-push", domain.Connector);
        Assert.Contains("no_funds", domain.Data);
        var change = await db.SourceEvents.SingleAsync(e => e.DomainSeq == domain.Seq);
        Assert.Null(change.EventId);
    }

    [Fact]
    public async Task Behavioral_And_UnknownTypes_AreRecorded_WithoutRecalc()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var now = Factory.Time.GetUtcNow();
        using var ingest = Ingest(Factory);

        var (status, body) = await Post(ingest,
            V2("u-1", "usage.daily", now.AddHours(-1), 2, 1, 2, new { cpuAvgPct = 4.5, netOutMb = 300 }),
            V2("t-1", "ticket.opened", now.AddMinutes(-30), 2, 2, null, new { category = "network", priority = "high" }),
            V2("x-1", "crm.note_added", now.AddMinutes(-10), 2, 3));
        Assert.Equal(HttpStatusCode.OK, status); // состояние не менялось — пересчёт не ставится
        Assert.Equal(3, body["recorded"]!.GetValue<int>());
        Assert.Equal(1, body["unknownTypes"]!.GetValue<int>());
        Assert.Null(body["jobId"]);
        Assert.All(body["results"]!.AsArray(), r => Assert.Equal("recorded", r!["outcome"]!.GetValue<string>()));

        var stream = await Stream(c);
        Assert.Equal(3, stream["quality"]!["domainEvents24h"]!.GetValue<int>());
        Assert.Equal(1, stream["quality"]!["unknownTypes24h"]!.GetValue<int>());
        var push = stream["connectors"]!.AsArray().Single(x => x!["key"]!.GetValue<string>() == "http-push")!;
        Assert.Equal("ok", push["status"]!.GetValue<string>());
        Assert.Equal(3, push["events"]!.GetValue<long>());
        Assert.Equal(600, push["lagSeconds"]!.GetValue<long>());
        Assert.Null(push["watermark"]);
    }

    [Fact]
    public async Task InvalidEvents_AreRejected_Individually()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var now = Factory.Time.GetUtcNow();
        using var ingest = Ingest(Factory);

        var (_, body) = await Post(ingest,
            V2("ok-1", "account.login", now, 3, 1),
            V2("bad-1", "service.renewed", now, 3, 2, 3),               // меняет услугу, но state нет
            V2("bad-2", "account.login", now.AddHours(1), 3, 3),        // из будущего
            new { schemaVersion = 2, type = "account.login", occurredAt = now, accountId = 3, accountSeq = 4, data = new { } }); // без eventId
        Assert.Equal(1, body["recorded"]!.GetValue<int>());
        Assert.Equal(3, body["rejected"]!.GetValue<int>());
        var errors = body["errors"]!.AsObject().Select(k => k.Key).ToList();
        Assert.Contains("events[1].state.service", errors);
        Assert.Contains("events[2].occurredAt", errors);
        Assert.Contains("events[3].eventId", errors);
    }

    [Fact]
    public async Task AccountSeq_Gaps_AreVisible_AndClosedByRedelivery()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var now = Factory.Time.GetUtcNow();
        using var ingest = Ingest(Factory);

        await Post(ingest, V2("g-1", "account.login", now, 4, 1), V2("g-2", "account.login", now, 4, 2), V2("g-5", "account.login", now, 4, 5));
        Assert.Equal(1, (await Stream(c))["quality"]!["accountsWithGaps"]!.GetValue<int>());

        // Источник переотправил потерянные (повтор g-2 — дубликат и не мешает)
        var (_, body) = await Post(ingest, V2("g-2", "account.login", now, 4, 2), V2("g-4", "account.login", now, 4, 4), V2("g-3", "account.login", now, 4, 3));
        Assert.Equal(1, body["duplicates"]!.GetValue<int>());
        Assert.Equal(0, (await Stream(c))["quality"]!["accountsWithGaps"]!.GetValue<int>());
    }

    [Fact]
    public async Task Reordered_Events_OlderStateIsStale_AndServiceWaitsForAccount()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var now = Factory.Time.GetUtcNow();
        using var ingest = Ingest(Factory);

        // Продление пришло раньше истечения, которое было до него: старое состояние не откатывает срок
        var renewed = Record("services", 11);
        var expires = renewed["expiresAt"]!.GetValue<DateTimeOffset>();
        renewed["expiresAt"] = expires.AddMonths(1).ToString("O");
        renewed["status"] = "active";
        var expired = Record("services", 11);
        expired["status"] = "prolong_wait";
        var owner = renewed["ownerId"]!.GetValue<long>();
        var (_, body) = await Post(ingest,
            V2("r-11", "service.renewed", now.AddMinutes(-1), owner, 21, 11, state: new { service = renewed }),
            V2("e-11", "service.expired", now.AddMinutes(-3), owner, 20, 11, state: new { service = expired }));
        Assert.Equal("applied", body["results"]![0]!["outcome"]!.GetValue<string>());
        Assert.Equal("stale", body["results"]![1]!["outcome"]!.GetValue<string>());
        Assert.Equal("expires_regressed", body["results"]![1]!["reason"]!.GetValue<string>());

        // Услуга нового клиента пришла раньше его аккаунта: ждёт без оценки, клиент не придумывается
        var svc = Record("services", 1);
        svc["id"] = 950;
        svc["ownerId"] = 78;
        var (_, ordered) = await Post(ingest, V2("o-950", "service.ordered", now.AddMinutes(-2), 78, 2, 950, state: new { service = svc }));
        Assert.Contains("owner_unknown", ordered["results"]![0]!["changes"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(1, (await Stream(c))["orphanServices"]!.GetValue<int>());

        var acc = Record("accounts", 1);
        acc["id"] = 78;
        acc["email"] = "user-0078@example.test";
        var (_, registered) = await Post(ingest, V2("a-78", "account.registered", now.AddMinutes(-5), 78, 1, state: new { account = acc }));
        var seq = registered["lastSeq"]!.GetValue<long>();
        var gen = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() == seq);
        Assert.Equal(11, gen["customers"]!["total"]!.GetValue<int>());
        Assert.Equal(0, (await Stream(c))["orphanServices"]!.GetValue<int>());
    }

    [Fact]
    public async Task Watermark_DecidesNotRenewed_InsteadOfLatestEventTime()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var t0 = Factory.Time.GetUtcNow();
        using var ingest = Ingest(Factory);

        // Новая услуга клиента 1 со сроком через час и подтверждение «всё до t0 отправлено»
        var svc = Record("services", 11);
        svc["id"] = 903;
        svc["ownerId"] = 1;
        svc["expiresAt"] = t0.AddHours(1).ToString("O");
        await Post(ingest,
            V2("o-903", "service.ordered", t0, 1, 1, 903, state: new { service = svc }),
            V2("wm-1", "stream.watermark", t0, data: new { upTo = t0 }));

        async Task<int> NotRenewed()
        {
            using var fresh = await Factory.LoginAsync();
            return (await fresh.GetFromJsonAsync<JsonObject>("/api/v1/analytics/history"))!["outcomes"]!["notRenewed"]!.GetValue<int>();
        }

        // Срок + 5 дней прошёл; свежие события идут, но водяной знак стоит на t0 — исход не фиксируется
        Factory.Time.Advance(TimeSpan.FromDays(7));
        var now = Factory.Time.GetUtcNow();
        await Post(ingest, V2("l-1", "account.login", now, 1, 2));
        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await NotRenewed());

        // Источник подтвердил полноту до «сейчас» — созревший срок закрывается как «не продлено»
        await Post(ingest, V2("wm-2", "stream.watermark", now, data: new { upTo = now }));
        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        int notRenewed;
        do
        {
            await Task.Delay(300);
            notRenewed = await NotRenewed();
        }
        while (notRenewed == 0 && DateTime.UtcNow < deadline);
        Assert.True(notRenewed >= 1);
        using var later = await Factory.LoginAsync();
        var push = (await Stream(later))["connectors"]!.AsArray().Single(x => x!["key"]!.GetValue<string>() == "http-push")!;
        Assert.Equal(now, push["watermark"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task HistoryBeforeBase_IsRecorded_ButDoesNotOverrideBase()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var svc = Record("services", 21);
        var original = svc["status"]!.GetValue<string>();
        svc["status"] = original == "suspended" ? "active" : "suspended";
        using var ingest = Ingest(Factory);

        // Событие из истории раньше даты тестовых данных: их результат уже в базе
        var (_, body) = await Post(ingest, V2("h-21", "service.suspended", TestConst.SourceAsOf.AddDays(-3), 1, 1, 21, state: new { service = svc }));
        Assert.Equal("stale", body["results"]![0]!["outcome"]!.GetValue<string>());
        Assert.Equal("before_base", body["results"]![0]!["reason"]!.GetValue<string>());
        Assert.Equal(0, body["applied"]!.GetValue<int>());
        var detail = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!;
        Assert.Equal(original, detail["services"]!.AsArray().Single(s => s!["id"]!.GetValue<long>() == 21)!["status"]!.GetValue<string>());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("stale", (await db.SourceDomainEvents.SingleAsync(e => e.EventId == "h-21")).Outcome); // для ML событие сохранено
    }

    [Fact]
    public async Task NamedConnector_HasOwnToken_State_AndNoReadAccess()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var now = Factory.Time.GetUtcNow();
        using var crm = Ingest(Factory, CrmToken);

        var (status, body) = await Post(crm, V2("crm-1", "nps.responded", now.AddMinutes(-1), 5, 1, null, new { score = 3 }));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body["recorded"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await crm.GetAsync("/api/v1/customers")).StatusCode);

        var connectors = (await Stream(c))["connectors"]!.AsArray();
        Assert.Equal(["http-push", "batch-import", "snapshot", "crm-export"], connectors.Select(x => x!["key"]!.GetValue<string>()));
        var named = connectors.Single(x => x!["key"]!.GetValue<string>() == "crm-export")!;
        Assert.True(named["enabled"]!.GetValue<bool>());
        Assert.Equal(1, named["events"]!.GetValue<long>());
        Assert.Equal(0, connectors.Single(x => x!["key"]!.GetValue<string>() == "http-push")!["events"]!.GetValue<long>());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("crm-export", (await db.SourceDomainEvents.SingleAsync(e => e.EventId == "crm-1")).Connector);
    }
}
