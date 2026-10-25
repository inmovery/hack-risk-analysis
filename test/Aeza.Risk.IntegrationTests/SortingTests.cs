using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

using Aeza.Risk.IntegrationTests.Base;

namespace Aeza.Risk.IntegrationTests;

/// <summary>8 обязательных направлений сортировки (ID, email, баланс, пополнения × asc/desc) по ВСЕМУ набору через постраничный обход</summary>
public sealed class SortingTests(PostgresFixture pg) : DbTestBase(pg)
{
    public override async ValueTask InitializeAsync()
    {
        // Отдельный seed: данные организаторов + синтетические краевые случаи (ID 2 vs 10 vs 100, регистр email, равные балансы).
        var doc = JsonNode.Parse(File.ReadAllText(TestConst.OrganizerData))!.AsObject();
        var accounts = doc["accounts"]!.AsArray();
        accounts.Add(new JsonObject { ["id"] = 100, ["email"] = "Zed@Example.test", ["balance"] = 3478, ["withdrawBalance"] = 0, ["totalReplenished"] = 3478, ["bonusBalance"] = 0, ["bonusState"] = "locked" });
        accounts.Add(new JsonObject { ["id"] = 20, ["email"] = "alpha@example.test", ["balance"] = 3478, ["withdrawBalance"] = 0, ["totalReplenished"] = 96922, ["bonusBalance"] = 0, ["bonusState"] = "locked" });
        accounts.Add(new JsonObject { ["id"] = 21, ["email"] = "ALPHA@example.test2", ["balance"] = null, ["withdrawBalance"] = 0, ["totalReplenished"] = null, ["bonusBalance"] = 0, ["bonusState"] = "unlocked" });
        var envelope = new JsonObject { ["schemaVersion"] = 1, ["sourceAsOf"] = TestConst.SourceAsOf.ToString("O"), ["synthetic"] = true, ["accounts"] = accounts.DeepClone(), ["services"] = doc["services"]!.DeepClone(), ["transactions"] = doc["transactions"]!.DeepClone() };
        var path = Path.Combine(Path.GetTempPath(), $"aeza-sort-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, envelope.ToJsonString());
        Factory = new AppFactory(Pg.ConnectionString(Database), seedFile: path);
    }

    private sealed record Row(long Id, string Email, long? Balance, long? Total);

    private static List<Row> Expected()
    {
        var doc = JsonNode.Parse(File.ReadAllText(TestConst.OrganizerData))!;
        var rows = doc["accounts"]!.AsArray().Select(a => new Row(a!["id"]!.GetValue<long>(), a["email"]!.GetValue<string>(), a["balance"]!.GetValue<long>(), a["totalReplenished"]!.GetValue<long>())).ToList();
        rows.Add(new Row(100, "Zed@Example.test", 3478, 3478));
        rows.Add(new Row(20, "alpha@example.test", 3478, 96922));
        rows.Add(new Row(21, "ALPHA@example.test2", null, null));
        return rows;
    }

    /// <summary>Ординальное сравнение байтов UTF-8 (как COLLATE "C" в PostgreSQL)</summary>
    private static int Bytes(string a, string b) => Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    public static TheoryData<string, string> Directions => new()
    {
        { "id", "asc" }, { "id", "desc" }, { "email", "asc" }, { "email", "desc" },
        { "balance", "asc" }, { "balance", "desc" }, { "totalReplenished", "asc" }, { "totalReplenished", "desc" },
    };

    [Theory]
    [MemberData(nameof(Directions))]
    public async Task RequiredSorts_OrderTheWholeDataset_AcrossPages(string sortBy, string direction)
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["customers"]!["total"]!.GetValue<int>() == 13);
        var ids = new List<long>();
        for (var page = 1; ; page++)
        {
            var r = await c.GetFromJsonAsync<JsonObject>($"/api/v1/customers?sortBy={sortBy}&sortDirection={direction}&pageSize=4&page={page}");
            var items = r!["items"]!.AsArray();
            ids.AddRange(items.Select(i => i!["id"]!.GetValue<long>()));
            if (page * 4 >= r["total"]!.GetValue<int>()) break;
        }

        var rows = Expected();
        var desc = direction == "desc";
        IEnumerable<Row> ordered = sortBy switch
        {
            "id" => desc ? rows.OrderByDescending(r => r.Id) : rows.OrderBy(r => r.Id),
            "email" => rows.Order(Comparer<Row>.Create((a, b) =>
            {
                var c1 = Bytes(a.Email.ToLowerInvariant(), b.Email.ToLowerInvariant());
                if (c1 == 0) c1 = Bytes(a.Email, b.Email);
                return c1 != 0 ? (desc ? -c1 : c1) : a.Id.CompareTo(b.Id);
            })),
            "balance" => rows.OrderBy(r => r.Balance is null).ThenBy(r => desc ? -(r.Balance ?? 0) : r.Balance ?? 0).ThenBy(r => r.Id),
            _ => rows.OrderBy(r => r.Total is null).ThenBy(r => desc ? -(r.Total ?? 0) : r.Total ?? 0).ThenBy(r => r.Id),
        };
        Assert.Equal(ordered.Select(r => r.Id).ToList(), ids);
        if (sortBy == "id") Assert.True(ids.IndexOf(2) < ids.IndexOf(10) == !desc, "ID сортируется численно: 2 перед 10");
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task RiskScore_UnknownIsLastInBothDirections(string direction)
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["customers"]!["total"]!.GetValue<int>() == 13);
        var r = await c.GetFromJsonAsync<JsonObject>($"/api/v1/customers?sortBy=riskScore&sortDirection={direction}&pageSize=200");
        var scores = r!["items"]!.AsArray().Select(i => i!["riskScore"]?.GetValue<decimal?>()).ToList();
        var firstNull = scores.FindIndex(s => s is null);
        Assert.True(firstNull > 0);
        Assert.All(scores.Skip(firstNull), s => Assert.Null(s));
        var known = scores.Take(firstNull).Select(s => s!.Value).ToList();
        Assert.Equal(direction == "asc" ? known.Order().ToList() : known.OrderDescending().ToList(), known);
    }

    [Fact]
    public async Task InvalidSortField_IsRejected_NotPassedToSql()
    {
        using var c = await Factory.LoginAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/customers?sortBy=email;drop table users")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/customers?sortDirection=sideways")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/customers?pageSize=500")).StatusCode);
    }

    [Fact]
    public async Task Filters_LevelSearchAndState()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["customers"]!["total"]!.GetValue<int>() == 13);
        var unknown = await c.GetFromJsonAsync<JsonObject>("/api/v1/customers?riskLevel=unknown&pageSize=200");
        Assert.All(unknown!["items"]!.AsArray(), i => Assert.Equal("unknown", i!["riskLevel"]!.GetValue<string>()));
        var search = await c.GetFromJsonAsync<JsonObject>("/api/v1/customers?search=ALPHA&pageSize=200");
        Assert.Equal(new long[] { 20, 21 }, search!["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Order().ToArray());
        var byId = await c.GetFromJsonAsync<JsonObject>("/api/v1/customers?search=100");
        Assert.Contains(byId!["items"]!.AsArray(), i => i!["id"]!.GetValue<long>() == 100);
    }
}
