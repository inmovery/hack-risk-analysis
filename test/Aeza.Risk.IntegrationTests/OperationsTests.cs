using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.Feature.Model;
using Aeza.Risk.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

using Aeza.Risk.IntegrationTests.Base;

namespace Aeza.Risk.IntegrationTests;

public sealed class OperationsTests(PostgresFixture pg) : DbTestBase(pg)
{
    [Fact]
    public async Task EmptyDatabase_IsReadyToReceiveSnapshot_AndReportsNoData()
    {
        await Factory.DisposeAsync();
        Factory = new AppFactory(Pg.ConnectionString(Database + "_empty"), seedFile: Path.Combine(Path.GetTempPath(), "does-not-exist.json")); // своя пустая БД
        using var c = Factory.NewClient();
        var ready = await c.GetAsync("/health/ready");
        Assert.True(ready.StatusCode == HttpStatusCode.OK, await ready.Content.ReadAsStringAsync());
        Assert.Equal("ready_no_data", (await ready.JsonAsync())["status"]!.GetValue<string>());

        using var admin = await Factory.LoginAsync();
        var summary = await admin.GetAsync("/api/v1/analytics/summary");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, summary.StatusCode);
        Assert.Equal("no_generation", (await summary.JsonAsync())["code"]!.GetValue<string>());

        // Первый снимок через API (без seed) → расчёт → готовность.
        var put = await admin.PutRawAsync("/api/v1/source/snapshot?sourceAsOf=2026-10-19T15:00:00Z", await File.ReadAllTextAsync(TestConst.OrganizerData));
        Assert.Equal(HttpStatusCode.Accepted, put.StatusCode);
        await AppFactory.WaitForGenerationAsync(admin, s => s["generation"] is not null);
        Assert.Equal("ready", (await (await c.GetAsync("/health/ready")).JsonAsync())["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task AppDbRole_HasDmlOnly()
    {
        using (var c = await Factory.LoginAsync()) await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Pg.ConnectionString(Database)).Options))
            await DbRoles.EnsureAppRoleAsync(db, "aeza_app_test", "app-role-pass-'quote\"", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var cs = new NpgsqlConnectionStringBuilder(Pg.ConnectionString(Database)) { Username = "aeza_app_test", Password = "app-role-pass-'quote\"" }.ConnectionString;
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM customer_results", conn))
            Assert.True((long)(await read.ExecuteScalarAsync())! > 0);
        await using (var write = new NpgsqlCommand("UPDATE app_state SET id = id WHERE id = 1", conn))
            Assert.Equal(1, await write.ExecuteNonQueryAsync());
        await using var ddl = new NpgsqlCommand("CREATE TABLE should_fail (x int)", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ddl.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege
    }

    [Fact]
    public void Merge_DropsWeightsOfFeaturesRemovedFromCatalog()
    {
        var current = ModelSettings.Default(FeatureCatalog.All) with
        {
            Weights = FeatureCatalog.All.ToDictionary(f => f.Key, f => f.DefaultWeight).Append(new KeyValuePair<string, decimal>("retired_feature", 15m)).ToDictionary(),
        };
        var merged = ModelSettingsRules.Merge(current, new SettingsPatch(1, null, null, null, null, new() { ["waiting_payment"] = 41m }, null));
        Assert.DoesNotContain("retired_feature", merged.Weights.Keys);
        Assert.Equal(41m, merged.Weights["waiting_payment"]);
        Assert.Empty(ModelSettingsValidator.Validate(merged, FeatureCatalog.All.ToList()));
    }

    [Fact]
    public async Task MlStatus_IsHonestlyNotTrained()
    {
        using var c = await Factory.LoginAsync();
        var s = await c.GetFromJsonAsync<JsonObject>("/api/v1/ml/status");
        Assert.Equal("not_trained", s!["status"]!.GetValue<string>());
    }
}
