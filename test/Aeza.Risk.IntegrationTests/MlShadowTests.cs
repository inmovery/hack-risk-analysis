using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Aeza.Risk.Core.Entities;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.Infrastructure.Data.Repositories;
using Aeza.Risk.IntegrationTests.Base;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Aeza.Risk.IntegrationTests;

/// <summary>Этап 4 / на реальном PostgreSQL</summary>
public sealed class MlShadowTests(PostgresFixture pg) : DbTestBase(pg)
{
    private static string BundlePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "ml", "model-bundle.zip");

    private static Task<HttpResponseMessage> PutBundle(HttpClient c, byte[] bundle)
    {
        var content = new ByteArrayContent(bundle);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return c.PutAsync("/api/v1/ml/model", content);
    }

    /// <summary>Пакет фикстуры с изменённой карточкой и/или ONNX</summary>
    private static byte[] Tamper(Action<JsonObject>? card = null, Func<byte[], byte[]>? onnx = null, bool fixHash = true)
    {
        using var src = ZipFile.OpenRead(BundlePath);
        var json = JsonNode.Parse(src.GetEntry("model.json")!.Open())!.AsObject();
        using var ms = new MemoryStream();
        src.GetEntry("model.onnx")!.Open().CopyTo(ms);
        var model = onnx?.Invoke(ms.ToArray()) ?? ms.ToArray();
        card?.Invoke(json);
        if (fixHash) json["onnxSha256"] = Convert.ToHexStringLower(SHA256.HashData(model));
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = zip.CreateEntry("model.onnx").Open()) w.Write(model);
            using (var w = zip.CreateEntry("model.json").Open()) w.Write(Encoding.UTF8.GetBytes(json.ToJsonString()));
        }
        return output.ToArray();
    }

    private static async Task<Dictionary<long, decimal?>> ScoresAsync(HttpClient c)
    {
        var r = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers?sortBy=id&sortDirection=asc&pageSize=200"))!;
        return r["items"]!.AsArray().ToDictionary(i => i!["id"]!.GetValue<long>(), i => i!["riskScore"]?.GetValue<decimal?>());
    }

    [Fact]
    public async Task Upload_ActivatesShadow_ScoresUnchanged_PredictionsSaved()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        // По умолчанию основной балл — ML; тень проверяется в режиме правил
        var rules = await c.PutAsJsonAsync("/api/v1/ml/scoring", new { primary = "rules" });
        Assert.Equal(HttpStatusCode.Accepted, rules.StatusCode);
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["scoring"]?.GetValue<string>() == "rules");
        var before = await ScoresAsync(c);
        Assert.Equal("not_trained", (await c.GetFromJsonAsync<JsonObject>("/api/v1/ml/status"))!["status"]!.GetValue<string>());

        var r = await PutBundle(c, await File.ReadAllBytesAsync(BundlePath));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var activated = await r.JsonAsync();
        var modelId = activated["model"]!["id"]!.GetValue<long>();
        Assert.Equal("logistic", activated["model"]!["kind"]!.GetValue<string>());
        Assert.True(activated["model"]!["synthetic"]!.GetValue<bool>());

        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["mlModelId"]?.GetValue<long?>() == modelId);
        // Тень не меняет баллы, уровни и сортировки
        Assert.Equal(before, await ScoresAsync(c));

        // Клиент 4: две услуги в окне модели (квартал — срок через 14 дн., неделя — через 1 день)
        var detail = (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4"))!["detail"]!;
        var shadow = detail["mlShadow"]!;
        Assert.Equal("scored", shadow["state"]!.GetValue<string>());
        Assert.Equal(modelId, shadow["modelId"]!.GetValue<long>());
        var p = shadow["probability"]!.GetValue<double>();
        Assert.InRange(p, 0, 1);
        Assert.Equal([4L, 14L], shadow["services"]!.AsArray().Select(s => s!["serviceId"]!.GetValue<long>()).Order());
        Assert.Equal("out_of_scope", (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/1"))!["detail"]!["mlShadow"]!["state"]!.GetValue<string>());

        var status = (await c.GetFromJsonAsync<JsonObject>("/api/v1/ml/status"))!;
        Assert.Equal("shadow", status["status"]!.GetValue<string>());
        var s = status["shadow"]!;
        Assert.Equal(2, s["generation"]!["servicesInScope"]!.GetValue<int>());
        Assert.Equal(1, s["generation"]!["customersScored"]!.GetValue<int>());
        Assert.Equal("logistic", s["offline"]!["model"]!.GetValue<string>());
        Assert.True(s["offline"]!["synthetic"]!.GetValue<bool>());
        Assert.NotEmpty(s["offline"]!["topFeatures"]!.AsArray());
        Assert.True(s["offline"]!["modelMetrics"]!["prAuc"]!.GetValue<double>() > s["offline"]!["rules"]!["prAuc"]!.GetValue<double>());
        Assert.Equal(2, s["online"]!["predictions"]!.GetValue<int>());
        Assert.Null(s["online"]!["model"]); // исходов ещё нет: качество на реальных данных не показывается
        Assert.Contains("Прогнозы в тени не меняют", s["note"]!.GetValue<string>(), StringComparison.Ordinal);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var predictions = await db.MlShadowPredictions.AsNoTracking().Where(x => x.ModelId == modelId).OrderBy(x => x.ServiceId).ToListAsync();
            Assert.Equal([(4L, 14), (14L, 3)], predictions.Select(x => (x.ServiceId, x.OffsetDays)));
            Assert.All(predictions, x => Assert.Equal(4, x.AccountId));
            Assert.Contains(await db.Audit.AsNoTracking().Select(a => a.Action).ToListAsync(), a => a == "ml_model_activated");
        }

        // Повторный пересчёт по времени не дублирует прогноз в той же точке решения
        Factory.Time.Advance(TimeSpan.FromMinutes(10));
        await AppFactory.WaitForGenerationAsync(c, x => DateTimeOffset.Parse(x["generation"]!["asOf"]!.GetValue<string>()) >= Factory.Time.GetUtcNow().AddMinutes(-1), 40);
        using (var scope = Factory.Services.CreateScope())
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<AppDbContext>().MlShadowPredictions.CountAsync());
    }

    [Fact]
    public async Task Upload_RejectsInvalidBundles_AndNonAdmins_KeepsShadowOff()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);

        async Task Rejected(byte[] bundle, string code)
        {
            var r = await PutBundle(c, bundle);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal(code, (await r.JsonAsync())["code"]!.GetValue<string>());
        }

        await Rejected(Encoding.UTF8.GetBytes("not a zip"), "bundle_invalid");
        await Rejected(Tamper(card => card["featureSchema"] = "aeza-ml-features-v0"), "schema_mismatch");
        await Rejected(Tamper(card => card["features"]!.AsArray().RemoveAt(0)), "features_mismatch");
        await Rejected(Tamper(card => card["onnxSha256"] = new string('0', 64), fixHash: false), "onnx_hash_mismatch");
        await Rejected(Tamper(onnx: b => b.Take(b.Length / 2).ToArray()), "onnx_invalid");

        using var analyst = await Factory.LoginAsync(TestConst.AnalystLogin, TestConst.AnalystPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutBundle(analyst, await File.ReadAllBytesAsync(BundlePath))).StatusCode);
        using var noCsrf = await Factory.LoginAsync();
        noCsrf.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.Forbidden, (await PutBundle(noCsrf, await File.ReadAllBytesAsync(BundlePath))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutBundle(Factory.NewClient(), await File.ReadAllBytesAsync(BundlePath))).StatusCode);

        Assert.Equal("not_trained", (await c.GetFromJsonAsync<JsonObject>("/api/v1/ml/status"))!["status"]!.GetValue<string>());
        using var scope = Factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().MlModels.CountAsync());
    }

    [Fact]
    public async Task Delete_TurnsShadowOff_WithRecalculation()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var activated = await (await PutBundle(c, await File.ReadAllBytesAsync(BundlePath))).JsonAsync();
        var modelId = activated["model"]!["id"]!.GetValue<long>();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["mlModelId"]?.GetValue<long?>() == modelId);

        Assert.Equal(HttpStatusCode.Accepted, (await c.DeleteAsync("/api/v1/ml/model")).StatusCode);
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["mlModelId"] is null);
        Assert.Null((await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4"))!["detail"]!["mlShadow"]);
        Assert.Equal("not_trained", (await c.GetFromJsonAsync<JsonObject>("/api/v1/ml/status"))!["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/v1/ml/model")).StatusCode);
    }

    /// <summary>ONNX, который не открывается (повреждён в БД): поколение публикуется по правилам, тень — failed</summary>
    [Fact]
    public async Task BrokenModelInDatabase_FallsBackToRules()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var before = await ScoresAsync(c);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var model = new MlModelEntity
            {
                Version = "broken", FeatureSchema = Core.MlFeatures.Schema, Kind = "logistic", Synthetic = true, CardJson = "{}", Onnx = [1, 2, 3],
                OnnxSha256 = new string('0', 64), UploadedAt = Factory.Time.GetUtcNow(), UploadedBy = "test",
            };
            db.MlModels.Add(model);
            await db.SaveChangesAsync();
            var state = await db.AppState.SingleAsync();
            state.ActiveMlModelId = model.Id;
            state.MlRevision++;
            RecalcQueue.Enqueue(db, "ml_model", Factory.Time.GetUtcNow(), "test");
            await db.SaveChangesAsync();
        }

        var summary = await AppFactory.WaitForGenerationAsync(c, s => s["generation"]!["mlModelId"] is not null);
        Assert.Equal(before, await ScoresAsync(c));
        var status = (await c.GetFromJsonAsync<JsonObject>("/api/v1/ml/status"))!;
        Assert.Equal("failed", status["shadow"]!["generation"]!["state"]!.GetValue<string>());
        Assert.Equal("inference_error", status["shadow"]!["generation"]!["errorCode"]!.GetValue<string>());
        Assert.Equal("failed", (await c.GetFromJsonAsync<JsonObject>("/api/v1/customers/4"))!["detail"]!["mlShadow"]!["state"]!.GetValue<string>());
        Assert.NotNull(summary["generation"]);
    }
}
