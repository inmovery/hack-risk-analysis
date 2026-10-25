using System.IO.Compression;
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

/// <summary>Этап 3 / на реальном PostgreSQL</summary>
public sealed class BehaviorAndImportTests(PostgresFixture pg) : DbTestBase(pg)
{
    private static HttpClient Ingest(AppFactory f)
    {
        var c = f.NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestConst.IngestToken);
        return c;
    }

    private static object V2(string eventId, string type, DateTimeOffset at, long? accountId, long? seq, object data, long? serviceId = null, object? state = null) =>
        new { schemaVersion = 2, eventId, type, occurredAt = at, accountId, accountSeq = seq, serviceId, data, state };

    private static string Line(object e) => JsonSerializer(e);

    private static string JsonSerializer(object e) => System.Text.Json.JsonSerializer.Serialize(e);

    private static JsonObject Record(string kind, long id)
    {
        var doc = JsonNode.Parse(File.ReadAllText(TestConst.OrganizerData))!;
        return doc[kind]!.AsArray().Single(x => x!["id"]!.GetValue<long>() == id)!.DeepClone().AsObject();
    }

    private async Task<JsonObject> WaitImportAsync(HttpClient c, long id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(180); // при параллельных тест-классах импорт идёт медленнее
        JsonObject last;
        do
        {
            await Task.Delay(200);
            last = (await c.GetFromJsonAsync<JsonObject>($"/api/v1/source/imports/{id}"))!;
        }
        while (last["status"]!.GetValue<string>() is "pending" or "running" && DateTime.UtcNow < deadline);
        return last;
    }

    [Fact]
    public async Task Activity_IsProjectedByEventTime_DuplicatesNotCounted_AndShownInCard()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var now = Factory.Time.GetUtcNow();
        var yesterday = now.AddDays(-1);
        using var ingest = Ingest(Factory);
        var batch = new object[]
        {
            V2("a-1", "account.login", now.AddHours(-1), 3, 1, new { channel = "panel", success = true }),
            V2("a-2", "usage.daily", now.AddMinutes(-30), 3, 2, new { serviceId = 3, date = yesterday.ToString("yyyy-MM-dd"), cpuAvgPct = 40.0, trafficOutGb = 1.5 }, 3),
            V2("a-3", "payment.failed", now.AddMinutes(-20), 3, 3, new { serviceId = 3, amount = 960, reason = "insufficient_funds" }, 3,
                new { transaction = new { id = 9001, serviceId = 3, amount = 960, status = "not_performed", createdAt = now.AddMinutes(-20), type = "prolong" } }),
            V2("a-4", "ticket.closed", now.AddMinutes(-10), 3, 4, new { ticketId = 1, resolution = "unsolved", csat = 1, hours = 30 }),
        };
        Assert.Equal(HttpStatusCode.Accepted, (await ingest.PostAsJsonAsync("/api/v1/source/events", new { events = batch })).StatusCode);
        // Повтор пакета и запоздавшее событие позавчерашнего дня
        await ingest.PostAsJsonAsync("/api/v1/source/events", new { events = batch });
        await ingest.PostAsJsonAsync("/api/v1/source/events", new { events = new[] { V2("a-0", "account.login", now.AddDays(-2), 3, 5, new { success = true }) } });

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var days = await db.CustomerActivity.AsNoTracking().Where(a => a.AccountId == 3).OrderBy(a => a.Day).ToListAsync();
            Assert.Equal([DateOnly.FromDateTime(now.AddDays(-2).UtcDateTime), DateOnly.FromDateTime(yesterday.UtcDateTime), DateOnly.FromDateTime(now.UtcDateTime)],
                days.Select(d => d.Day));
            Assert.Equal((1, 40.0), (days[1].UsageSamples, days[1].CpuSum)); // телеметрия — в свой день, не в день получения
            var today = days[2];
            Assert.Equal((1, 1, 1, 1), (today.Logins, today.PaymentsFailed, today.TicketsUnsolved, today.CsatMin!.Value)); // дубликаты не удвоили
        }

        var activity = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/3/activity?days=30"))!;
        Assert.NotNull(activity["knownSince"]);
        Assert.Equal(3, activity["days"]!.AsArray().Count);
        Assert.Equal(40.0, activity["days"]![1]!["cpuAvgPct"]!.GetValue<double>());
        var feed = activity["events"]!.AsArray();
        Assert.Equal(5, feed.Count);
        Assert.Contains(feed, e => e!["text"]!.GetValue<string>().StartsWith("Списание не прошло €9,60", StringComparison.Ordinal));
        Assert.DoesNotContain(feed, e => e!.AsObject().ContainsKey("data"));
        var money = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/3/activity?domain=money"))!["events"]!.AsArray();
        Assert.Equal("payment.failed", Assert.Single(money)!["type"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.GetAsync("/api/v1/customers/3/activity?domain=crm")).StatusCode);

        // Клиент без событий v2 — «нет данных», а не нули
        var none = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1/activity"))!;
        Assert.Null(none["knownSince"]);
        Assert.Empty(none["days"]!.AsArray());
    }

    [Fact]
    public async Task BehaviorFeatures_WeightZero_Visible_ThenWeighted_ChangeScore_ActivitySeqInGeneration()
    {
        using var c = await Factory.LoginAsync();
        var first = await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var before = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4"))!["detail"]!;
        var now = Factory.Time.GetUtcNow();
        using var ingest = Ingest(Factory);
        // Клиент 4: три неудачных списания (поведение), без изменения состояния — пересчёт по часам подхватит активность
        var failed = Enumerable.Range(1, 3).Select(i => V2($"pf-{i}", "account.login", now.AddMinutes(-i), 4, i, new { success = false })).ToList<object>();
        failed.Add(V2("pf-x", "payment.failed", now.AddMinutes(-1), 4, 4, new { amount = 100 }, 4,
            new { transaction = new { id = 9100, serviceId = 4, amount = 100, status = "not_performed", createdAt = now.AddMinutes(-1), type = "prolong" } }));
        var r = await (await ingest.PostAsJsonAsync("/api/v1/source/events", new { events = failed })).JsonAsync();
        var seq = r["lastSeq"]!.GetValue<long>();
        var gen = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceSeq"]!.GetValue<long>() == seq);
        Assert.True(gen["generation"]!["activitySeq"]!.GetValue<long>() > first["generation"]!["activitySeq"]!.GetValue<long>());
        Assert.Equal("1.2.0", gen["generation"]!["engineVersion"]!.GetValue<string>());

        var detail = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4"))!["detail"]!;
        var feature = detail["services"]!.AsArray().First()!["features"]!.AsArray().Single(f => f!["key"]!.GetValue<string>() == "payment_failures")!;
        Assert.Equal((0.5m, 0m), (feature["value"]!.GetValue<decimal>(), feature["weight"]!.GetValue<decimal>())); // посчитан, но вес 0
        Assert.Equal(before["riskScore"]?.GetValue<decimal>(), detail["riskScore"]?.GetValue<decimal>());

        // Вес задан: балл клиента с поведением растёт, клиент без поведения сохраняет балл и получает флаг
        var other = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!;
        var rev = await c.ModelRevisionAsync();
        var patch = await c.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, weights = new Dictionary<string, decimal> { ["payment_failures"] = 100 } });
        Assert.Equal(HttpStatusCode.Accepted, patch.StatusCode);
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["modelRevision"]!.GetValue<long>() > rev);
        var weighted = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4"))!["detail"]!;
        Assert.True(weighted["riskScore"]!.GetValue<decimal>() >= 50m);
        var reasons = weighted["reasons"]!.AsArray().Where(x => x!["code"]!.GetValue<string>() == "payment_failures").ToList();
        Assert.Single(reasons);
        var unaffected = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!;
        Assert.Equal(other["riskScore"]?.GetValue<decimal>(), unaffected["riskScore"]?.GetValue<decimal>());
        Assert.Contains("BEHAVIOR_DATA_MISSING", unaffected["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Fact]
    public async Task Import_Gzip_InChunks_OneRecalc_RejectsBrokenLines_AndRepeatIsDuplicates()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var now = Factory.Time.GetUtcNow();
        // 12 000 входов клиентов 1–10 (3 куска), одно изменение состояния и две испорченные строки
        var lines = new List<string>();
        var seq = new Dictionary<long, long>();
        for (var i = 0; i < 12_000; i++)
        {
            var acc = (long)(i % 10) + 1;
            seq[acc] = seq.GetValueOrDefault(acc) + 1;
            lines.Add(Line(V2($"imp-{i}", "account.login", now.AddDays(-40).AddMinutes(i * 4), acc, seq[acc], new { success = true })));
        }
        var svc = Record("services", 1);
        svc["autoProlong"] = !svc["autoProlong"]!.GetValue<bool>();
        lines.Add(Line(V2("imp-state", "service.autoprolong_changed", now.AddMinutes(-1), 1, seq[1] + 1, new { enabled = svc["autoProlong"]!.GetValue<bool>() }, 1, new { service = svc })));
        lines.Insert(500, "{not json");
        lines.Insert(7000, Line(new { schemaVersion = 2, type = "account.login", occurredAt = now, accountId = 2, accountSeq = 1, data = new { } })); // без eventId
        byte[] gz;
        using (var ms = new MemoryStream())
        {
            using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
            gz = ms.ToArray();
        }

        var jobsBefore = await JobsAsync("import");
        using var content = new ByteArrayContent(gz);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        var post = await c.PostAsync("/api/v1/source/events/import", content);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var accepted = await post.JsonAsync();
        Assert.Equal((3, 12_003L), (accepted["chunksTotal"]!.GetValue<int>(), accepted["eventsTotal"]!.GetValue<long>()));
        var done = await WaitImportAsync(c, accepted["id"]!.GetValue<long>());
        Assert.Equal("succeeded", done["status"]!.GetValue<string>());
        Assert.Equal((3, 12_000L, 1L, 2L), (done["chunksDone"]!.GetValue<int>(), done["recorded"]!.GetValue<long>(), done["applied"]!.GetValue<long>(), done["rejected"]!.GetValue<long>()));
        var errors = done["errors"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Contains("line 501: invalid_json", errors);
        Assert.Contains(errors, e => e.StartsWith("line 7001:", StringComparison.Ordinal) && e.Contains("eventId"));
        Assert.Equal(jobsBefore + 1, await JobsAsync("import")); // один пересчёт на весь импорт
        Assert.NotNull(done["jobId"]);

        // Тот же файл ещё раз — всё дубликаты, состояние не меняется
        using var again = new ByteArrayContent(gz);
        again.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        var repeat = await WaitImportAsync(c, (await (await c.PostAsync("/api/v1/source/events/import", again)).JsonAsync())["id"]!.GetValue<long>());
        Assert.Equal((12_001L, 0L, 0L), (repeat["duplicates"]!.GetValue<long>(), repeat["applied"]!.GetValue<long>(), repeat["recorded"]!.GetValue<long>()));

        var stream = (await c.GetFromJsonAsync<JsonObject>("/api/v1/source/stream"))!;
        var importConnector = stream["connectors"]!.AsArray().Single(x => x!["key"]!.GetValue<string>() == "batch-import")!;
        Assert.Equal(12_001, importConnector["events"]!.GetValue<long>());
        Assert.Equal(12_001, importConnector["duplicates"]!.GetValue<long>());
        Assert.Equal(4, importConnector["rejected"]!.GetValue<long>());
        Assert.Equal("succeeded", importConnector["lastImport"]!["status"]!.GetValue<string>());
        Assert.Equal(2, (await c.GetFromJsonAsync<JsonArray>("/api/v1/source/imports"))!.Count);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.SourceImportChunks.CountAsync()); // куски удалены после выполнения
        Assert.Equal(12_000, await db.CustomerActivity.SumAsync(a => a.Logins));
    }

    [Fact]
    public async Task Import_Rejects_EmptyFile_AndRequiresWriter()
    {
        using var c = await Factory.LoginAsync();
        var empty = await c.PostAsync("/api/v1/source/events/import", new StringContent("\n\n", Encoding.UTF8, "application/x-ndjson"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal("empty", (await empty.JsonAsync())["code"]!.GetValue<string>());
        using var analyst = await Factory.LoginAsync(TestConst.AnalystLogin, TestConst.AnalystPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await analyst.PostAsync("/api/v1/source/events/import", new StringContent("{}"))).StatusCode);
        using var ingest = Ingest(Factory);
        var ok = await ingest.PostAsync("/api/v1/source/events/import",
            new StringContent(Line(V2("tok-1", "account.login", Factory.Time.GetUtcNow(), 1, 1, new { success = true })), Encoding.UTF8, "application/x-ndjson"));
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
    }

    [Fact]
    public async Task Connector_CountsRejectedAndDuplicates_WithLastErrorPath()
    {
        using var c = await Factory.LoginAsync();
        using var ingest = Ingest(Factory);
        var now = Factory.Time.GetUtcNow();
        await ingest.PostAsJsonAsync("/api/v1/source/events", new { events = new object[] { V2("cc-1", "account.login", now, 1, 1, new { }), new { schemaVersion = 2, eventId = "cc-2", type = "account.login", accountId = 1, accountSeq = 2, data = new { } } } });
        await ingest.PostAsJsonAsync("/api/v1/source/events", new { events = new[] { V2("cc-1", "account.login", now, 1, 1, new { }) } });
        var push = (await c.GetFromJsonAsync<JsonObject>("/api/v1/source/stream"))!["connectors"]!.AsArray().Single(x => x!["key"]!.GetValue<string>() == "http-push")!;
        Assert.Equal((1L, 1L, 1L), (push["events"]!.GetValue<long>(), push["rejected"]!.GetValue<long>(), push["duplicates"]!.GetValue<long>()));
        Assert.Equal("events[1].occurredAt", push["lastError"]!.GetValue<string>());
    }

    [Fact]
    public async Task Gap_BlocksNotRenewed_UntilSnapshotReconciles()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var t0 = Factory.Time.GetUtcNow();
        using var ingest = Ingest(Factory);
        var svc = Record("services", 11);
        svc["id"] = 904;
        svc["ownerId"] = 2;
        svc["expiresAt"] = t0.AddHours(1).ToString("O");
        // accountSeq 1, затем 3: события 2 нет — пропуск у аккаунта 2
        await ingest.PostAsJsonAsync("/api/v1/source/events", new { events = new[]
        {
            V2("g-904", "service.ordered", t0, 2, 1, new { }, 904, new { service = svc }),
            V2("g-904b", "account.login", t0, 2, 3, new { success = true }),
        } });
        Factory.Time.Advance(TimeSpan.FromDays(7));
        var now = Factory.Time.GetUtcNow();
        await ingest.PostAsJsonAsync("/api/v1/source/events", new { events = new[] { V2("wm-gap", "stream.watermark", now, null, null, new { upTo = now }) } });
        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(3));
        async Task<string?> Outcome()
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.ExpiryOutcomes.AsNoTracking().Where(o => o.ServiceId == 904).Select(o => o.Outcome).SingleAsync();
        }
        Assert.Null(await Outcome()); // возможно, продление потеряно — не решаем

        // Сверка тестовыми данными: пропуски закрыты, созревший срок решается
        using var admin = await Factory.LoginAsync();
        var doc = JsonNode.Parse(File.ReadAllText(TestConst.OrganizerData))!.AsObject();
        doc["schemaVersion"] = 1;
        doc["sourceAsOf"] = now.AddMinutes(-1).ToString("O");
        doc["services"]!.AsArray().Add(svc.DeepClone());
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PutRawAsync("/api/v1/source/snapshot", doc.ToJsonString())).StatusCode);
        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string? outcome;
        do
        {
            await Task.Delay(300);
            outcome = await Outcome();
        }
        while (outcome is null && DateTime.UtcNow < deadline);
        Assert.Equal("not_renewed", outcome);
    }

    private async Task<int> JobsAsync(string reason)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RecalcJobs.CountAsync(j => j.Reason == reason);
    }
}
