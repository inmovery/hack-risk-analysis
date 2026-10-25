using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Aeza.Risk.IntegrationTests.Base;

using Xunit;

namespace Aeza.Risk.IntegrationTests;

/// <summary>Переключатель основного балла «правила / только ML» на реальном PostgreSQL и настоящей ONNX-модели</summary>
public sealed class MlScoringTests(PostgresFixture pg) : DbTestBase(pg)
{
    private static string BundlePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ml", "model-bundle.zip");

    /// <summary>FastTree с деревьями (trees.json): вклады по TreeSHAP</summary>
    private static string FastTreePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ml", "model-bundle-fasttree.zip");

    private static Task<HttpResponseMessage> Scoring(HttpClient c, string? primary) => c.PutAsJsonAsync("/api/v1/ml/scoring", new { primary });

    private static async Task<long> UploadModelAsync(HttpClient c, string? path = null)
    {
        var content = new ByteArrayContent(await File.ReadAllBytesAsync(path ?? BundlePath));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var r = await c.PutAsync("/api/v1/ml/model", content);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        return (await r.JsonAsync())["model"]!["id"]!.GetValue<long>();
    }

    private static async Task<string> MetricsAsync(HttpClient c, long generation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var r = await c.GetAsync("/metrics");
            var body = await r.Content.ReadAsStringAsync();
            if (r.Headers.TryGetValues("X-Risk-Generation", out var v) && v.Single() == generation.ToString(CultureInfo.InvariantCulture)) return body;
            if (DateTime.UtcNow > deadline) return body;
            await Task.Delay(200);
        }
    }

    /// <summary>Значение aeza_scoring{mode=…} (служебные метки otel_scope_* пропускаются)</summary>
    private static decimal? Gauge(string body, string mode) => body.Split('\n')
        .Where(l => l.StartsWith("aeza_scoring{", StringComparison.Ordinal) && l.Contains($"mode=\"{mode}\"", StringComparison.Ordinal))
        .Select(l => decimal.Parse(l[(l.IndexOf('}') + 1)..].Trim().Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture))
        .Cast<decimal?>().FirstOrDefault();

    [Fact]
    public async Task Switch_ToMl_ScoresLevelsMoneyFromModel_AndBackToRules()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var rulesDetail4 = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4"))!["detail"]!;

        // По умолчанию выбран ML; пока модели нет, поколение посчитано правилами
        var initial = (await c.GetFromJsonAsync<JsonObject>("/api/v1/ml/status"))!["scoring"]!;
        Assert.Equal("ml", initial["primary"]!.GetValue<string>());
        Assert.Equal("rules", initial["generation"]!["used"]!.GetValue<string>());
        Assert.Equal("no_model", initial["generation"]!["fallback"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await Scoring(c, "ml")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Scoring(c, "rules")).StatusCode);

        // Без модели режим ML не включается
        var noModel = await Scoring(c, "ml");
        Assert.Equal(HttpStatusCode.Conflict, noModel.StatusCode);
        Assert.Equal("ml_model_required", (await noModel.JsonAsync())["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Scoring(c, "neural")).StatusCode);

        var modelId = await UploadModelAsync(c);
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["mlModelId"]?.GetValue<long?>() == modelId);

        using var analyst = await Factory.LoginAsync(TestConst.AnalystLogin, TestConst.AnalystPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await Scoring(analyst, "ml")).StatusCode);

        var on = await Scoring(c, "ml");
        Assert.Equal(HttpStatusCode.Accepted, on.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Scoring(c, "ml")).StatusCode); // уже включено — без нового пересчёта
        var summary = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["scoring"]?.GetValue<string>() == "ml");
        Assert.Contains("ML", summary["definitions"]!["score"]!.GetValue<string>(), StringComparison.Ordinal);

        // Клиент 4: две услуги в окне модели — балл = максимум вероятностей × 100, причина — прогноз модели
        var detail = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4"))!["detail"]!;
        var p = detail["mlShadow"]!["probability"]!.GetValue<double>();
        Assert.Equal(Math.Round((decimal)p * 100m, 2), Math.Round(detail["riskScore"]!.GetValue<decimal>(), 2));
        // Причины — факторы модели со значениями клиента (или прогноз, если ни один фактор не выше типичного)
        var primaryCode = detail["primaryReason"]!["code"]!.GetValue<string>();
        Assert.True(primaryCode.StartsWith("ml.", StringComparison.Ordinal) || primaryCode == "ml_probability", primaryCode);
        Assert.All(detail["reasons"]!.AsArray(), r => Assert.StartsWith("ml.", r!["code"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.All(detail["reasons"]!.AsArray(), r => Assert.True(r!["contribution"]!.GetValue<decimal>() > 0));
        Assert.NotNull(detail["mlShadow"]!["services"]![0]!["drivers"]);
        var atRisk = detail["services"]!.AsArray().Where(s => s!["scored"]!.GetValue<bool>())
            .Sum(s => s!["money"]!["plannedRenewalsMinor"]!.GetValue<long>() * s["score"]!.GetValue<decimal>() / 100m);
        Assert.Equal((long)Math.Round(atRisk, 0, MidpointRounding.ToEven), detail["money"]!["atRiskMinor"]!.GetValue<long>());
        Assert.NotEqual(rulesDetail4["riskScore"]?.ToJsonString(), detail["riskScore"]?.ToJsonString());

        // Клиент 1: нет услуг в окне модели — «нет оценки», а не 0
        var c1 = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!;
        Assert.Null(c1["riskScore"]);
        Assert.Equal("unknown", c1["riskLevel"]!.GetValue<string>());
        Assert.Equal("ml_out_of_scope", c1["primaryReason"]!["code"]!.GetValue<string>());

        var status = (await c.GetFromJsonAsync<JsonObject>("/api/v1/ml/status"))!;
        Assert.Equal("ml", status["scoring"]!["primary"]!.GetValue<string>());
        Assert.Equal("ml", status["scoring"]!["generation"]!["used"]!.GetValue<string>());
        Assert.Equal(modelId, status["scoring"]!["generation"]!["modelId"]!.GetValue<long>());

        var gen = summary["generation"]!["id"]!.GetValue<long>();
        var metrics = await MetricsAsync(Factory.NewClient(), gen);
        Assert.Equal(1m, Gauge(metrics, "ml"));
        Assert.Equal(0m, Gauge(metrics, "rules"));

        // Список и сортировка по баллу — по баллам ML
        var list = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers?sortBy=riskScore&sortDirection=desc&pageSize=1"))!;
        Assert.Equal(4, list["items"]![0]!["id"]!.GetValue<long>());

        // Выключение модели возвращает правила явно
        Assert.Equal(HttpStatusCode.Accepted, (await c.DeleteAsync("/api/v1/ml/model")).StatusCode);
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["scoring"]?.GetValue<string>() == "rules" && s["generation"]!["mlModelId"] is null);
        Assert.Equal("rules", (await c.GetFromJsonAsync<JsonObject>("/api/v1/ml/status"))!["scoring"]!["primary"]!.GetValue<string>());
        var back = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4"))!["detail"]!;
        Assert.Equal(rulesDetail4["riskScore"]?.ToJsonString(), back["riskScore"]?.ToJsonString());
    }

    [Fact]
    public async Task FastTreeBundle_ReasonsAreTreeShap_MoneyHasInterval_TamperedTreesRejected()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);

        // Деревья, которые не совпадают с ONNX, не принимаются: вклады посчитались бы не для этой модели
        var tampered = Tamper(File.ReadAllBytes(FastTreePath));
        var bad = await c.PutAsync("/api/v1/ml/model", new ByteArrayContent(tampered) { Headers = { ContentType = new MediaTypeHeaderValue("application/zip") } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal("trees_mismatch", (await bad.JsonAsync())["code"]!.GetValue<string>());

        var modelId = await UploadModelAsync(c, FastTreePath);
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["mlModelId"]?.GetValue<long?>() == modelId);
        // ML выбран по умолчанию: загруженная модель сразу считает основной балл
        Assert.Equal(HttpStatusCode.OK, (await Scoring(c, "ml")).StatusCode);
        var summary = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["scoring"]?.GetValue<string>() == "ml");

        var detail = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4"))!["detail"]!;
        Assert.Equal("treeshap", detail["mlShadow"]!["explain"]!.GetValue<string>());
        // Вклады TreeSHAP в пунктах вероятности: в сумме — прогноз минус средний прогноз модели
        var service = detail["mlShadow"]!["services"]!.AsArray().First()!;
        var drivers = service["drivers"]!.AsArray();
        Assert.NotEmpty(drivers);
        Assert.All(detail["reasons"]!.AsArray(), r => Assert.StartsWith("ml.", r!["code"]!.GetValue<string>(), StringComparison.Ordinal));

        var interval = summary["money"]!["atRiskInterval"]!;
        var atRisk = summary["money"]!["atRiskMinor"]!.GetValue<long>();
        Assert.InRange(atRisk, interval["lowMinor"]!.GetValue<long>(), interval["highMinor"]!.GetValue<long>());
        Assert.Equal(0.9, interval["level"]!.GetValue<double>());

        Assert.Equal(HttpStatusCode.Accepted, (await Scoring(c, "rules")).StatusCode);
        var rules = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["scoring"]?.GetValue<string>() == "rules");
        Assert.Null(rules["money"]!["atRiskInterval"]);
    }

    /// <summary>Пакет с изменённым значением одного листа в trees.json</summary>
    private static byte[] Tamper(byte[] bundle)
    {
        using var input = new MemoryStream(bundle);
        using var src = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var e in src.Entries)
            {
                using var target = zip.CreateEntry(e.FullName).Open();
                using var source = e.Open();
                if (e.FullName != "trees.json")
                {
                    source.CopyTo(target);
                    continue;
                }
                var trees = JsonNode.Parse(source)!;
                foreach (var t in trees["trees"]!.AsArray()) t!["leafValue"]![0] = t["leafValue"]![0]!.GetValue<double>() + 0.5;
                target.Write(System.Text.Encoding.UTF8.GetBytes(trees.ToJsonString()));
            }
        return output.ToArray();
    }
}
