using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Aeza.Risk.Core;

using Microsoft.ML;

using RankingMetrics = Aeza.Risk.Core.RankingMetrics;

namespace Aeza.Risk.Ml;

/// <summary>Модели клиента: M2 — уход целиком за 30 (или 90) дней, M3 — сокращение выручки за 30 дней. Проверка по годам против балла
/// правил клиента; финальная модель учится на всём до последнего года и проверяется на нём</summary>
public sealed class CustomerTrainer(int seed = 42, string target = CustomerTrainer.Churn30)
{
    public const string Churn30 = "churn30";
    public const string Churn90 = "churn90";
    public const string Contraction30 = "contraction30";
    private const int MinTrainPositives = 100;
    private const int MinTestPositives = 30;

    public static bool IsTarget(string? t) => t is Churn30 or Churn90 or Contraction30;

    private int Label(CustomerWeekRow r) => target switch
    {
        Churn90 => r.Churn90,
        Contraction30 => r.Contraction30,
        _ => r.Churn30,
    };

    /// <summary>Через сколько дней после среза исход известен: горизонт плюс ожидание возврата для ухода</summary>
    private int MatureDays => target switch
    {
        Churn90 => 90 + CustomerTimeline.ReturnDays,
        Contraction30 => 30,
        _ => 30 + CustomerTimeline.ReturnDays,
    };

    private string Describe => target switch
    {
        Churn90 => "уход клиента целиком за 90 дней (нет действующих услуг и нет возврата 30 дней)",
        Contraction30 => "сокращение выручки на 30 % и больше за 30 дней при оставшихся услугах",
        _ => "уход клиента целиком за 30 дней (нет действующих услуг и нет возврата 30 дней)",
    };

    public JsonObject Run(IReadOnlyList<CustomerWeekRow> all, JsonObject manifest, string outDir, Action<string> log)
    {
        Directory.CreateDirectory(outDir);
        var size = MlCustomerFeatures.Names.Count;
        var rows = all.Where(r => Label(r) >= 0 && r.Features is not null).ToList();
        if (rows.Count == 0) throw new InvalidOperationException("Нет срезов клиентов с известным исходом: пересоберите набор (customers.csv)");
        var ml = new MLContext(seed);
        var years = new JsonArray();
        (ITransformer Model, List<CustomerWeekRow> Train, List<CustomerWeekRow> Test, double[] Probs, RankMetrics Eval, RankMetrics Rules, int Year)? last = null;
        foreach (var year in rows.Select(r => r.At.Year).Distinct().Order())
        {
            var from = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var train = rows.Where(r => r.At.AddDays(MatureDays) < from).ToList();
            var test = rows.Where(r => r.At >= from && r.At < from.AddYears(1)).ToList();
            var trainPos = train.Count(r => Label(r) == 1);
            var testPos = test.Count(r => Label(r) == 1);
            var y = new JsonObject
            {
                ["year"] = year, ["trainRows"] = train.Count, ["trainPositives"] = trainPos, ["testRows"] = test.Count, ["testPositives"] = testPos,
                ["baseRate"] = test.Count == 0 ? null : Trainer.Round((double)testPos / test.Count),
            };
            if (trainPos < MinTrainPositives || testPos < MinTestPositives)
            {
                y["skipped"] = $"мало исходов: обучение {trainPos} (нужно ≥ {MinTrainPositives}), проверка {testPos} (≥ {MinTestPositives})";
                years.Add(y);
                log($"{target} {year}: пропущен — {y["skipped"]}");
                continue;
            }
            var model = Trainer.Pipeline(ml, "fasttree").Fit(Trainer.LoadVectors(ml, train.Select(r => (Label(r) == 1, r.Features!)), size));
            var probs = Trainer.PredictVectors(model, ml, test.Select(r => r.Features!), size);
            var labels = test.Select(Label).ToList();
            var eval = RankingMetrics.Evaluate(labels, probs, probabilities: true);
            var rules = RankingMetrics.Evaluate(labels, test.Select(r => (double)(r.RulesScore ?? 0)).ToList(), probabilities: false);
            var bins = RankingMetrics.Calibration(labels, probs);
            var ece = bins.Sum(b => (double)b.Rows / test.Count * Math.Abs(b.Predicted - b.Observed));
            y["model"] = Trainer.Json(eval);
            y["rules"] = Trainer.Json(rules);
            y["prAucGain"] = Trainer.Round(eval.PrAuc - rules.PrAuc);
            y["ece"] = Trainer.Round(ece);
            years.Add(y);
            log($"{target} {year}: обучение {train.Count} ({trainPos}), проверка {test.Count} ({testPos}); PR-AUC {eval.PrAuc:0.000} против правил {rules.PrAuc:0.000}; " +
                $"P@K {eval.PrecisionAtK:0.000} против {rules.PrecisionAtK:0.000}; ECE {ece:0.000}");
            last = (model, train, test, probs, eval, rules, year);
        }
        if (last is not { } final) throw new InvalidOperationException($"{target}: ни один год не набрал исходов для честной проверки");

        // Финальная модель — обученная на всём до последнего года; её проверка — последний год
        var onnxPath = Path.Combine(outDir, "model.onnx");
        Trainer.ExportOnnx(ml, final.Model, size, onnxPath);
        var testFeatures = final.Test.Select(r => r.Features!).ToList();
        var parity = Trainer.OnnxParity(onnxPath, testFeatures, final.Probs, size);
        if (parity > 1e-4) throw new InvalidOperationException($"ONNX расходится с ML.NET: {parity}");
        var probe = Math.Min(testFeatures.Count, 2000);
        var trees = TreeExport.From(final.Model, MlCustomerFeatures.Names, final.Train.Select(r => r.Features!).ToList(), testFeatures.Take(probe).ToList(),
            final.Probs.Take(probe).ToList());
        var treesPath = Path.Combine(outDir, TreeEnsembleJson.FileName);
        if (trees is not null) File.WriteAllText(treesPath, TreeEnsembleJson.Serialize(trees));
        else File.Delete(treesPath);

        // Сильнее всего влияющие признаки — средний модуль вклада TreeSHAP на проверке
        var top = new JsonArray();
        if (trees is not null)
        {
            var mean = new double[size];
            foreach (var x in testFeatures.Take(3000))
            {
                var phi = TreeShap.Explain(trees, x).Phi;
                for (var j = 0; j < size; j++) mean[j] += Math.Abs(phi[j]);
            }
            var n = Math.Min(testFeatures.Count, 3000);
            foreach (var j in Enumerable.Range(0, size).OrderByDescending(j => mean[j]).Take(12))
                top.Add(new JsonObject { ["name"] = MlCustomerFeatures.Names[j], ["meanAbsShap"] = Trainer.Round(mean[j] / n) });
        }

        var scored = years.OfType<JsonObject>().Where(y => y["model"] is not null).ToList();
        var createdAt = DateTimeOffset.UtcNow;
        var onnxSha = DatasetFile.Sha256(onnxPath);
        var card = new JsonObject
        {
            ["format"] = 1,
            ["kind"] = "customer",
            ["version"] = $"{createdAt:yyyyMMdd-HHmm}-{target}-{onnxSha[..8]}",
            ["createdAt"] = DatasetFile.Iso(createdAt),
            ["target"] = Describe,
            ["targetKey"] = target,
            ["featureSchema"] = MlCustomerFeatures.Schema,
            ["features"] = new JsonArray(MlCustomerFeatures.Names.Select(n => (JsonNode)n).ToArray()),
            ["input"] = Trainer.OnnxInput,
            ["output"] = Trainer.OnnxOutput,
            ["model"] = "fasttree",
            ["seed"] = seed,
            ["synthetic"] = manifest["synthetic"]?.DeepClone(),
            ["dataset"] = new JsonObject
            {
                ["source"] = manifest["source"]?.DeepClone(), ["sha256"] = manifest["customers"]?["sha256"]?.DeepClone(), ["rows"] = rows.Count,
                ["dataStart"] = manifest["dataStart"]?.DeepClone(), ["dataEnd"] = manifest["dataEnd"]?.DeepClone(),
            },
            ["split"] = new JsonObject
            {
                ["kind"] = "по годам: обучение — срезы, исход которых известен до 1 января, проверка — срезы года",
                ["testYear"] = final.Year, ["trainRows"] = final.Train.Count, ["testRows"] = final.Test.Count,
                ["testPositives"] = final.Test.Count(r => Label(r) == 1),
            },
            ["metrics"] = new JsonObject { ["model"] = Trainer.Json(final.Eval), ["rules"] = Trainer.Json(final.Rules) },
            ["backtest"] = new JsonObject
            {
                ["years"] = years,
                ["yearsScored"] = scored.Count,
                ["yearsModelBetter"] = scored.Count(y => y["prAucGain"]!.GetValue<double>() > 0),
                ["meanPrAucModel"] = Trainer.Round(scored.Average(y => y["model"]!["prAuc"]!.GetValue<double>())),
                ["meanPrAucRules"] = Trainer.Round(scored.Average(y => y["rules"]!["prAuc"]!.GetValue<double>())),
                ["meanEce"] = Trainer.Round(scored.Average(y => y["ece"]!.GetValue<double>())),
            },
            ["topFeatures"] = top,
            ["onnxSha256"] = onnxSha,
            ["parityMaxAbsDiff"] = parity,
            ["explain"] = trees is null ? "none" : "treeshap",
            ["note"] = manifest["synthetic"]?.GetValue<bool>() == true
                ? "Обучено на журнале симулятора."
                : "Реальные данные источника.",
        };
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(outDir, "model.json"), card.ToJsonString(opts));
        File.WriteAllText(Path.Combine(outDir, "report.md"), Markdown(card));
        var bundle = Path.Combine(outDir, Trainer.BundleFile);
        if (File.Exists(bundle)) File.Delete(bundle);
        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Create))
            foreach (var file in new[] { "model.onnx", "model.json", "report.md", TreeEnsembleJson.FileName }.Where(f => File.Exists(Path.Combine(outDir, f))))
                zip.CreateEntryFromFile(Path.Combine(outDir, file), file);
        log($"{target}: модель {card["version"]}, проверка {final.Year}: PR-AUC {final.Eval.PrAuc:0.000} против правил {final.Rules.PrAuc:0.000}; пакет {bundle}");
        return card;
    }

    private static string Markdown(JsonObject c)
    {
        static string N(JsonNode? n) => n is null ? "—" : n.GetValue<double>().ToString("0.000", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine($"# Модель клиента {c["version"]}").AppendLine();
        sb.AppendLine($"Цель: {c["target"]}. Признаки: {c["featureSchema"]} ({c["features"]!.AsArray().Count}). {c["note"]}").AppendLine();
        sb.AppendLine("| Год | Проверка (исходов) | Доля | PR-AUC модели | PR-AUC правил | P@5% модели | P@5% правил | ECE |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var y in c["backtest"]!["years"]!.AsArray().OfType<JsonObject>())
            sb.AppendLine(y["skipped"] is not null
                ? $"| {y["year"]} | {y["testRows"]} ({y["testPositives"]}) | {N(y["baseRate"])} | — | — | — | — | — |"
                : $"| {y["year"]} | {y["testRows"]} ({y["testPositives"]}) | {N(y["baseRate"])} | {N(y["model"]!["prAuc"])} | {N(y["rules"]!["prAuc"])} | " +
                  $"{N(y["model"]!["precisionAtK"])} | {N(y["rules"]!["precisionAtK"])} | {N(y["ece"])} |");
        var b = c["backtest"]!;
        sb.AppendLine().AppendLine($"Модель лучше правил в {b["yearsModelBetter"]} из {b["yearsScored"]} лет; средний PR-AUC {N(b["meanPrAucModel"])} против {N(b["meanPrAucRules"])}; " +
                                   $"средняя ECE {N(b["meanEce"])}.");
        if (c["topFeatures"] is JsonArray { Count: > 0 } top)
            sb.AppendLine().AppendLine("Сильнее всего влияют (средний модуль вклада TreeSHAP в логит): " +
                                       string.Join(", ", top.Select(t => $"{t!["name"]} {N(t["meanAbsShap"])}")) + ".");
        return sb.ToString();
    }
}
