using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.Core.Generations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using Aeza.Risk.IntegrationTests.Base;

namespace Aeza.Risk.IntegrationTests;

public sealed class RecalcAndSourceTests(PostgresFixture pg) : DbTestBase(pg)
{
    private static string Snapshot(Action<JsonObject>? mutate = null, DateTimeOffset? asOf = null)
    {
        var doc = JsonNode.Parse(File.ReadAllText(TestConst.OrganizerData))!.AsObject();
        var env = new JsonObject { ["schemaVersion"] = 1, ["sourceAsOf"] = (asOf ?? TestConst.SourceAsOf.AddHours(1)).ToString("O"), ["synthetic"] = true };
        foreach (var k in new[] { "accounts", "services", "transactions" }) env[k] = doc[k]!.DeepClone();
        mutate?.Invoke(env);
        return env.ToJsonString();
    }

    private static JsonObject Account(JsonObject env, long id) => env["accounts"]!.AsArray().Single(a => a!["id"]!.GetValue<long>() == id)!.AsObject();

    [Fact]
    public async Task Seed_ProducesGenerationFromOrganizerData()
    {
        using var c = await Factory.LoginAsync();
        var s = await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        Assert.Equal(10, s["customers"]!["total"]!.GetValue<int>());
        Assert.Equal(4, s["customers"]!["byLevel"]!["unknown"]!.GetValue<int>()); // аккаунты 2, 3, 7, 8 — только blocked/deleted
        Assert.Equal(TestConst.SourceAsOf, s["generation"]!["sourceAsOf"]!.GetValue<DateTimeOffset>());
        Assert.True(s["money"]!["atRiskMinor"]!.GetValue<long>() > 0);
        var detail = await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/5");
        Assert.Equal("high", detail!["detail"]!["riskLevel"]!.GetValue<string>());
        Assert.NotEmpty(detail["detail"]!["reasons"]!.AsArray());
        Assert.Equal(s["generation"]!["id"]!.GetValue<long>(), detail["generation"]!["id"]!.GetValue<long>());
    }

    [Fact]
    public async Task PatchSettings_Returns202_RecalculatesWithoutRestart_AndValidates()
    {
        using var c = await Factory.LoginAsync();
        var first = await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var before = await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4");
        var rev = await c.ModelRevisionAsync();

        var invalid = await c.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, thresholds = new { medium = 70, high = 60 } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var negative = await c.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, weights = new { waiting_payment = -5 } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, negative.StatusCode);
        Assert.Equal(rev, await c.ModelRevisionAsync());

        var ok = await c.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, weights = new { suspended_billing = 50 }, comment = "integration" });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var body = await ok.JsonAsync();
        Assert.Equal(rev + 1, body["modelRevision"]!.GetValue<long>());
        Assert.NotNull(body["jobId"]);

        var after = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["modelRevision"]!.GetValue<long>() == rev + 1);
        Assert.True(after["generation"]!["id"]!.GetValue<long>() > first["generation"]!["id"]!.GetValue<long>());
        var detail = await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4");
        Assert.True(detail!["detail"]!["riskScore"]!.GetValue<decimal>() > before!["detail"]!["riskScore"]!.GetValue<decimal>()); // suspended-клиент
        var stale = await c.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, weights = new { suspended_billing = 10 } });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task PutSnapshot_FullReplacement_Idempotency_AndAtomicRejects()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var before = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!;

        var json = Snapshot(env => Account(env, 1)["balance"] = 0);
        var accepted = await c.PutRawAsync("/api/v1/source/snapshot", json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var rev = (await accepted.JsonAsync())["sourceRevision"]!.GetValue<long>();
        Assert.Equal(2, rev);
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceRevision"]!.GetValue<long>() == 2);
        var after = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!;
        Assert.Equal(0, after["customer"]!["balance"]!.GetValue<long>());
        Assert.True(after["money"]!["unfundedMinor"]!.GetValue<long>() > before["money"]!["unfundedMinor"]!.GetValue<long>());
        Assert.True(after["riskScore"]!.GetValue<decimal>() > before["riskScore"]!.GetValue<decimal>());

        var replay = await c.PutRawAsync("/api/v1/source/snapshot", json);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.JsonAsync())["idempotent"]!.GetValue<bool>());

        var invalid = await c.PutRawAsync("/api/v1/source/snapshot", Snapshot(env =>
        {
            env["services"]!.AsArray()[0]!["ownerId"] = 9999;
            env["accounts"]!.AsArray()[1]!["balance"] = 10.5;
        }, TestConst.SourceAsOf.AddHours(2)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var problem = await invalid.JsonAsync();
        Assert.Contains("services[0].ownerId", problem["errors"]!.AsObject().Select(kv => kv.Key));
        Assert.Contains("accounts[1].balance", problem["errors"]!.AsObject().Select(kv => kv.Key));
        Assert.DoesNotContain("example.test", problem.ToJsonString());

        var stale = await c.PutRawAsync("/api/v1/source/snapshot", Snapshot(env => Account(env, 2)["balance"] = 1, TestConst.SourceAsOf.AddHours(-5)));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var precondition = await c.PutRawAsync("/api/v1/source/snapshot", Snapshot(env => Account(env, 2)["balance"] = 2, TestConst.SourceAsOf.AddHours(3)),
            req => req.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\"")));
        Assert.Equal(HttpStatusCode.PreconditionFailed, precondition.StatusCode);

        var current = await c.GetFromJsonAsync<JsonObject>("/api/v1/source/current");
        Assert.Equal(2, current!["revision"]!.GetValue<long>());
    }

    [Fact]
    public async Task OversizedSnapshot_Is413()
    {
        using var c = await Factory.LoginAsync();
        var big = new string(' ', 51 * 1024 * 1024);
        var r = await c.PutRawAsync("/api/v1/source/snapshot", "{" + big + "}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
    }

    [Fact]
    public async Task ObservedHistory_RecordsStatusChangesBetweenSnapshots_AsIntervals()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var next = Snapshot(env => env["services"]!.AsArray().Single(s => s!["id"]!.GetValue<long>() == 5)!["status"] = "deleted", TestConst.SourceAsOf.AddDays(1));
        Assert.Equal(HttpStatusCode.Accepted, (await c.PutRawAsync("/api/v1/source/snapshot", next)).StatusCode);
        var s = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["sourceRevision"]!.GetValue<long>() == 2);
        var ev = s["history"]!["recentEvents"]!.AsArray().Single(e => e!["serviceId"]!.GetValue<long>() == 5)!;
        Assert.Equal("prolong_wait", ev["fromStatus"]!.GetValue<string>());
        Assert.Equal("deleted", ev["toStatus"]!.GetValue<string>());
        Assert.Equal(TestConst.SourceAsOf, ev["observedFrom"]!.GetValue<DateTimeOffset>());
        Assert.Equal(TestConst.SourceAsOf.AddDays(1), ev["observedTo"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task TimeOnlyChange_ClockTickRecalculates()
    {
        using var c = await Factory.LoginAsync();
        var first = await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var asOf = first["generation"]!["asOf"]!.GetValue<DateTimeOffset>();
        Factory.Time.Advance(TimeSpan.FromMinutes(10)); // данные не менялись — только время
        var next = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["asOf"]!.GetValue<DateTimeOffset>() > asOf, 40);
        Assert.Equal(first["generation"]!["sourceRevision"]!.GetValue<long>(), next["generation"]!["sourceRevision"]!.GetValue<long>());
        Assert.Equal(first["generation"]!["modelRevision"]!.GetValue<long>(), next["generation"]!["modelRevision"]!.GetValue<long>());
    }

    [Fact]
    public async Task SettingsAndSource_SurviveRestart_SeedDoesNotOverwrite()
    {
        long modelRev, genId;
        using (var c = await Factory.LoginAsync())
        {
            await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
            var rev = await c.ModelRevisionAsync();
            Assert.Equal(HttpStatusCode.Accepted, (await c.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, weights = new { no_autoprolong = 44 } })).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await c.PutRawAsync("/api/v1/source/snapshot", Snapshot(env => Account(env, 6)["balance"] = 0))).StatusCode);
            var s = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["modelRevision"]!.GetValue<long>() == rev + 1 && s["generation"]!["sourceRevision"]!.GetValue<long>() == 2);
            modelRev = rev + 1;
            genId = s["generation"]!["id"]!.GetValue<long>();
        }
        await Factory.DisposeAsync();

        Factory = new AppFactory(Pg.ConnectionString(Database));
        using var c2 = await Factory.LoginAsync();
        var settings = await c2.GetFromJsonAsync<JsonObject>("/api/v1/model/settings");
        Assert.Equal(modelRev, settings!["latest"]!["revision"]!.GetValue<long>());
        Assert.Equal(44, settings["latest"]!["settings"]!["weights"]!["no_autoprolong"]!.GetValue<decimal>());
        var summary = await c2.GetFromJsonAsync<JsonObject>("/api/v1/analytics/summary");
        Assert.Equal(2, summary!["generation"]!["sourceRevision"]!.GetValue<long>());
        Assert.True(summary["generation"]!["id"]!.GetValue<long>() >= genId);
        var snapshots = await c2.GetFromJsonAsync<JsonArray>("/api/v1/source/snapshots");
        Assert.Equal(2, snapshots!.Count); // seed не выполнился повторно
    }

    [Fact]
    public async Task OrphanedRunningJob_IsRecoveredAfterRestart()
    {
        using (var c = await Factory.LoginAsync()) await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        await Factory.DisposeAsync();

        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Pg.ConnectionString(Database)).Options))
        {
            // Имитируем падение процесса после commit настроек: новая ревизия + job в статусе running без лидера.
            var latest = await db.ModelRevisions.OrderByDescending(r => r.Id).FirstAsync();
            db.ModelRevisions.Add(new ModelRevisionEntity { Id = latest.Id + 1, SettingsJson = latest.SettingsJson.Replace("\"waiting_payment\":40", "\"waiting_payment\":41"), SchemaVersion = 1, CreatedAt = TestConst.Now, CreatedBy = "crash-test" });
            var state = await db.AppState.SingleAsync();
            state.LatestModelRevisionId = latest.Id + 1;
            db.RecalcJobs.Add(new RecalcJob { Reason = "model", Status = JobStatus.Running, Attempts = 1, CreatedAt = TestConst.Now, AvailableAt = TestConst.Now, StartedAt = TestConst.Now, LeaseUntil = TestConst.Now.AddMinutes(2), ModelRevisionId = latest.Id + 1 });
            await db.SaveChangesAsync();
        }

        Factory = new AppFactory(Pg.ConnectionString(Database));
        using var c2 = await Factory.LoginAsync();
        var s = await AppFactory.WaitForGenerationAsync(c2, s => s["generation"]!["modelRevision"]!.GetValue<long>() >= 2);
        Assert.Equal(2, s["generation"]!["modelRevision"]!.GetValue<long>());
    }

    [Fact]
    public void StaleJob_CannotOverwriteNewerGeneration()
    {
        var active = new GenerationEntity { SourceRevision = 3, ModelRevisionId = 5, AsOf = TestConst.Now };
        Assert.False(GenerationPublishing.ShouldPublish(active, new GenerationEntity { SourceRevision = 2, ModelRevisionId = 6, AsOf = TestConst.Now.AddMinutes(1) }));
        Assert.False(GenerationPublishing.ShouldPublish(active, new GenerationEntity { SourceRevision = 3, ModelRevisionId = 4, AsOf = TestConst.Now.AddMinutes(1) }));
        Assert.False(GenerationPublishing.ShouldPublish(active, new GenerationEntity { SourceRevision = 3, ModelRevisionId = 5, AsOf = TestConst.Now.AddMinutes(-1) }));
        Assert.True(GenerationPublishing.ShouldPublish(active, new GenerationEntity { SourceRevision = 3, ModelRevisionId = 6, AsOf = TestConst.Now.AddDays(-3) })); // смена режима часов на «момент снимка»
        Assert.True(GenerationPublishing.ShouldPublish(null, active));
    }

    [Fact]
    public async Task ConcurrentPatches_OneWins_OtherGets409()
    {
        using var a = await Factory.LoginAsync();
        using var b = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(a, s => s["generation"] is not null);
        var rev = await a.ModelRevisionAsync();
        var results = await Task.WhenAll(
            a.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, weights = new { waiting_payment = 42 } }),
            b.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, weights = new { waiting_payment = 43 } }));
        Assert.Equal(new[] { HttpStatusCode.Accepted, HttpStatusCode.Conflict }, results.Select(r => r.StatusCode).Order().ToArray());
        Assert.Equal(rev + 1, await a.ModelRevisionAsync());
        using var scope = Factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().ModelRevisions.CountAsync(r => r.BasedOn == rev));
    }
}
