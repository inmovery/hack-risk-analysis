using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

using Aeza.Risk.IntegrationTests.Base;

namespace Aeza.Risk.IntegrationTests;

public sealed partial class MetricsAndContractTests(PostgresFixture pg) : DbTestBase(pg)
{
    [GeneratedRegex(@"^(?<name>[a-zA-Z_:][a-zA-Z0-9_:]*)(\{(?<labels>(?:[^""}]|""(?:[^""\\]|\\.)*"")*)\})?\s+(?<value>[-+0-9.eE]+|NaN|\+Inf|-Inf)(\s+-?\d+)?$")]
    private static partial Regex SampleLine();

    [GeneratedRegex(@"(?<k>[a-zA-Z_][a-zA-Z0-9_]*)=""(?<v>(?:[^""\\]|\\.)*)""")]
    private static partial Regex LabelPair();

    /// <summary>Мини-парсер text exposition: каждая строка — комментарий или пример, иначе тест падает</summary>
    private static Dictionary<string, decimal> Parse(string body)
    {
        var samples = new Dictionary<string, decimal>();
        foreach (var line in body.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var m = SampleLine().Match(line);
            Assert.True(m.Success, $"Невалидная строка Prometheus: {line}");
            var labels = m.Groups["labels"].Success
                ? LabelPair().Matches(m.Groups["labels"].Value).Where(l => !l.Groups["k"].Value.StartsWith("otel_scope_", StringComparison.Ordinal))
                    .Select(l => $"{l.Groups["k"].Value}=\"{l.Groups["v"].Value}\"").Order(StringComparer.Ordinal).ToList()
                : [];
            var key = m.Groups["name"].Value + (labels.Count > 0 ? "{" + string.Join(",", labels) + "}" : "");
            if (decimal.TryParse(m.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) samples[key] = v;
        }
        return samples;
    }

    [Fact]
    public async Task Metrics_AreParseable_ConsistentWithApiOnSameGeneration_AndWithoutPii()
    {
        using var c = await Factory.LoginAsync();
        var summary = await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        using var m = Factory.NewClient(); // без авторизации
        var r = await WaitForMetricsAsync(m, summary["generation"]!["id"]!.GetValue<long>());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        var gen = long.Parse(r.Headers.GetValues("X-Risk-Generation").Single(), CultureInfo.InvariantCulture);
        Assert.Equal(summary["generation"]!["id"]!.GetValue<long>(), gen);

        var x = Parse(body);
        foreach (var level in new[] { "high", "medium", "low", "unknown" })
            Assert.Equal(summary["customers"]!["byLevel"]![level]!.GetValue<int>(), (int)x[$"aeza_customers{{risk_level=\"{level}\"}}"]);
        Assert.Equal(summary["customers"]!["total"]!.GetValue<int>(), (int)x["aeza_customers_count"]);
        Assert.Equal(x["aeza_customers_count"], new[] { "high", "medium", "low", "unknown" }.Sum(l => x[$"aeza_customers{{risk_level=\"{l}\"}}"]));
        Assert.Equal(summary["money"]!["atRiskMinor"]!.GetValue<long>() / 100m, x["aeza_money_at_risk_euros"]);
        Assert.Equal(summary["money"]!["plannedRenewalsMinor"]!.GetValue<long>() / 100m, x["aeza_money_planned_renewals_euros"]);
        Assert.Equal(summary["money"]!["unfundedMinor"]!.GetValue<long>() / 100m, x["aeza_money_unfunded_euros"]);
        Assert.Equal(summary["customers"]!["scoreSum"]!.GetValue<decimal>(), x["aeza_risk_score_sum"]);
        Assert.Equal(gen, (long)x["aeza_generation"]);
        Assert.DoesNotMatch("(?m)^(process_|dotnet_|http_|aspnetcore_|kestrel_)", body); // системные метрики не публикуются
        Assert.DoesNotContain("@", body);
        Assert.DoesNotContain("example.test", body);
        Assert.DoesNotMatch(new Regex("customer_id|email|service_id"), body);
    }

    private static readonly (string Method, string Path)[] Required =
    [
        ("post", "/api/v1/auth/login"), ("get", "/api/v1/auth/me"), ("post", "/api/v1/auth/logout"),
        ("get", "/api/v1/customers"), ("get", "/api/v1/customers/{id}"), ("get", "/api/v1/analytics/summary"),
        ("get", "/api/v1/model/settings"), ("patch", "/api/v1/model/settings"), ("put", "/api/v1/source/snapshot"),
    ];

    [Fact]
    public async Task OpenApi_ContainsAllRequiredRoutes_AndMatchesExportedContract()
    {
        using var c = await Factory.LoginAsync();
        var r = await c.GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        var doc = JsonNode.Parse(text)!.AsObject();
        var paths = doc["paths"]!.AsObject();
        foreach (var (method, path) in Required)
            Assert.True(paths[path]?[method] is not null, $"{method.ToUpperInvariant()} {path} отсутствует в OpenAPI");
        // GET /metrics обязателен, но это служебная точка Prometheus: доступна по HTTP и не входит в Swagger
        Assert.Null(paths["/metrics"]);
        using var anonymous = Factory.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/metrics")).StatusCode);
        Assert.NotNull(doc["components"]!["securitySchemes"]!["cookieAuth"]);

        // Swagger UI на обязательном пути отдаёт рабочую страницу после входа
        Assert.Null(paths["/api/v1/docs"]);
        var ui = await c.GetAsync("/api/v1/docs/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("swagger", (await ui.Content.ReadAsStringAsync()).ToLowerInvariant());
        var redirect = await c.GetAsync("/api/v1/docs");
        Assert.True(redirect.StatusCode is HttpStatusCode.OK or HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect);

        // Экспорт: contracts/openapi.json должен совпадать с runtime (обновить: AEZA_UPDATE_OPENAPI=1).
        var repoRoot = FindRepoRoot();
        var exported = Path.Combine(repoRoot, "contracts", "openapi.json");
        var normalized = JsonNode.Parse(text)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("AEZA_UPDATE_OPENAPI") == "1" || !File.Exists(exported))
            await File.WriteAllTextAsync(exported, normalized + "\n");
        Assert.Equal(normalized.Trim(), (await File.ReadAllTextAsync(exported)).Replace("\r\n", "\n").Trim());
    }

    [Fact]
    public async Task OpenApi_TryItOut_WorksWithSessionAndCsrfCookie()
    {
        // Swagger UI подставляет X-CSRF-TOKEN из cookie XSRF-TOKEN: проверяем, что cookie выдаётся при открытии docs и подходит для PATCH.
        using var c = await Factory.LoginAsync();
        c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var ui = await c.GetAsync("/api/v1/docs/index.html");
        var xsrf = ui.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("XSRF-TOKEN=")).Split(';')[0]["XSRF-TOKEN=".Length..];
        var rev = await c.ModelRevisionAsync();
        var req = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/model/settings") { Content = JsonContent.Create(new { baseRevision = rev, weights = new { unpaid_transaction = 12 } }) };
        req.Headers.Add("X-CSRF-TOKEN", Uri.UnescapeDataString(xsrf));
        Assert.Equal(HttpStatusCode.Accepted, (await c.SendAsync(req)).StatusCode);
    }

    /// <summary>Срез метрик обновляется после публикации поколения (асинхронно) — ждём то же поколение, что видит API</summary>
    private static async Task<HttpResponseMessage> WaitForMetricsAsync(HttpClient m, long generation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var r = await m.GetAsync("/metrics");
            if (r.Headers.TryGetValues("X-Risk-Generation", out var v) && v.Single() == generation.ToString(CultureInfo.InvariantCulture)) return r;
            if (DateTime.UtcNow > deadline) return r;
            await Task.Delay(200);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Aeza.Risk.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
