using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Aeza.Risk.Core;

using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Trainers;
using Microsoft.ML.Trainers.FastTree;

using RankingMetrics = Aeza.Risk.Core.RankingMetrics;

namespace Aeza.Risk.Ml;

/// <summary>Обучение и честная проверка: разбиение по времени с зазором k + N, отдельно — клиенты вне обучения</summary>
public sealed class Trainer(int seed = 42, int graceDays = 5, int testDays = 90, string? model = null)
{
    public const string BundleFile = "model-bundle.zip";
    public const string OnnxInput = "Features";
    public const string OnnxOutput = "Probability.output";
    private const int MinTrainPositives = 100;
    private const int MinTestPositives = 30;

    private sealed class MlRow
    {
        public bool Label { get; set; }
        public float[] Features { get; set; } = [];
    }

    private sealed class MlInput
    {
        public float[] Features { get; set; } = [];
    }

    private static readonly Dictionary<string, string[]> Expected = new()
    {
        ["dissatisfaction"] = ["unsolved_30", "csat_min_30", "incident_min_30_log", "tickets_30", "nps_min_90", "cpu_ratio", "logins_7", "cpu_7"],
        ["decay"] = ["cpu_ratio", "cpu_7", "logins_7", "logins_30", "days_since_login", "usage_days_7"],
        ["finance"] = ["failed_payments_30", "funds_cover", "topups_30", "topups_90_eur_log", "downgrades_60", "balance_eur_log"],
        ["price"] = ["bonus_expired_45", "downgrades_60", "autoprolong_off_30", "auto_prolong", "funds_cover", "bonus_unlocked"],
        ["neglect"] = ["logins_30", "logins_7", "days_since_login", "cpu_7", "funds_cover", "topups_90_eur_log", "balance_eur_log"],
        ["non_payment"] = ["funds_cover", "balance_eur_log", "failed_payments_30", "topups_30", "topups_90_eur_log", "auto_prolong"],
        ["experiment"] = ["deletions_90", "service_age_days_log", "live_services", "term_week"],
    };

    public JsonObject Run(List<DatasetRow> rows, JsonObject dataset, string outDir, string? truthPath, Action<string> log)
    {
        Directory.CreateDirectory(outDir);
        var ml = new MLContext(seed);
        var maxT = rows.Max(r => r.DecisionAt);
        var testFrom = new DateTimeOffset(maxT.UtcDateTime.Date, TimeSpan.Zero).AddDays(-testDays);
        var test = rows.Where(r => r.DecisionAt >= testFrom).ToList();
        // Метка обучающей строки созрела до начала проверки: E + N < начала, отсюда зазор не меньше k + N между решениями
        var train = rows.Where(r => r.ExpiresAt.AddDays(graceDays) < testFrom).ToList();
        var trainPos = train.Count(r => r.Label == 1);
        var testPos = test.Count(r => r.Label == 1);
        if (trainPos < MinTrainPositives || testPos < MinTestPositives)
            throw new InvalidOperationException($"Мало исходов для честной проверки: «не продлено» в обучении {trainPos} (нужно ≥ {MinTrainPositives}), в проверке {testPos} (≥ {MinTestPositives})");
        var lastTrainDecision = train.Max(r => r.DecisionAt);
        log($"Обучение: {train.Count} строк ({trainPos} «не продлено») до {DatasetFile.Iso(lastTrainDecision)}; проверка: {test.Count} ({testPos}) с {DatasetFile.Iso(testFrom)}");

        static bool InGroup(long account) => (ulong)account * 2654435761UL % 5 == 0; // 20% клиентов — вне обучения во второй проверке
        var labels = test.Select(r => r.Label).ToList();
        var accounts = test.Select(r => r.AccountId).ToList();
        // Две базовые линии правил: как в панели (окно срочности 7 дн.) и с окном 30 дн.; модель сравнивается с лучшей из них
        var rulesEval = RankingMetrics.Evaluate(labels, test.Select(RulesDefault).ToList(), probabilities: false);
        var rulesWideEval = RankingMetrics.Evaluate(labels, test.Select(RulesWide).ToList(), probabilities: false);
        Func<DatasetRow, double> baseline = rulesWideEval.PrAuc > rulesEval.PrAuc ? RulesWide : RulesDefault;
        var baselineName = rulesWideEval.PrAuc > rulesEval.PrAuc ? "rulesWide" : "rules";
        var bestRules = rulesWideEval.PrAuc > rulesEval.PrAuc ? rulesWideEval : rulesEval;
        var rules = test.Select(baseline).ToList();

        var results = new JsonObject();
        var models = new Dictionary<string, (ITransformer Model, double[] Probs, RankMetrics Eval)>();
        foreach (var name in new[] { "logistic", "fasttree" })
        {
            var model = Pipeline(ml, name).Fit(Load(ml, train));
            var probs = Predict(model, ml, test);
            var eval = RankingMetrics.Evaluate(labels, probs, probabilities: true);
            models[name] = (model, probs, eval);
            // Клиенты, которых не было в обучении
            var gTrain = train.Where(r => !InGroup(r.AccountId)).ToList();
            var gTest = test.Where(r => InGroup(r.AccountId)).ToList();
            JsonNode? group = null;
            if (gTest.Count(r => r.Label == 1) >= 10)
            {
                var gModel = Pipeline(ml, name).Fit(Load(ml, gTrain));
                var gEval = RankingMetrics.Evaluate(gTest.Select(r => r.Label).ToList(), Predict(gModel, ml, gTest), true);
                var gRules = RankingMetrics.Evaluate(gTest.Select(r => r.Label).ToList(), gTest.Select(baseline).ToList(), false);
                group = new JsonObject { ["model"] = Json(gEval), ["rules"] = Json(gRules) };
            }
            var prCi = RankingMetrics.Bootstrap(accounts, labels, probs, rules, m => m.PrAuc, 200, seed);
            var pkCi = RankingMetrics.Bootstrap(accounts, labels, probs, rules, m => m.PrecisionAtK, 200, seed + 1);
            results[name] = new JsonObject
            {
                ["time"] = Json(eval),
                ["prAucCi95"] = new JsonArray(Round(prCi.Lo), Round(prCi.Hi)),
                ["prAucMinusRulesCi95"] = new JsonArray(Round(prCi.DiffLo), Round(prCi.DiffHi)),
                ["precisionAtKCi95"] = new JsonArray(Round(pkCi.Lo), Round(pkCi.Hi)),
                ["precisionAtKMinusRulesCi95"] = new JsonArray(Round(pkCi.DiffLo), Round(pkCi.DiffHi)),
                ["customersOutsideTraining"] = group,
                ["calibration"] = new JsonArray(RankingMetrics.Calibration(labels, probs).Select(c => (JsonNode)new JsonObject
                {
                    ["from"] = c.From, ["to"] = c.To, ["rows"] = c.Rows, ["predicted"] = Round(c.Predicted), ["observed"] = Round(c.Observed),
                }).ToArray()),
            };
            log($"{name}: PR-AUC {eval.PrAuc:0.000} [{prCi.Lo:0.000}; {prCi.Hi:0.000}], P@{eval.K} {eval.PrecisionAtK:0.000}; лучшие правила ({baselineName}) PR-AUC {bestRules.PrAuc:0.000}, P@K {bestRules.PrecisionAtK:0.000}");
        }
        // Выбор — по PR-AUC на отложенном периоде; --model фиксирует вид (например, компактная логистическая модель для фикстуры)
        var chosen = model ?? models.OrderByDescending(m => m.Value.Eval.PrAuc).First().Key;
        var best = models[chosen];

        // Утечки: модель на перемешанных метках не должна быть лучше случайной
        var rnd = new Random(seed);
        var shuffled = train.Select(r => r.Label).OrderBy(_ => rnd.Next()).ToList();
        var shuffleModel = Pipeline(ml, "fasttree").Fit(Load(ml, train.Select((r, i) => r with { Label = shuffled[i] }).ToList()));
        var shuffleEval = RankingMetrics.Evaluate(labels, Predict(shuffleModel, ml, test), true);

        // Перестановочная важность: на сколько падает PR-AUC, если перемешать признак
        var importance = new List<(string Name, double Drop)>();
        for (var j = 0; j < MlFeatures.Names.Count; j++)
        {
            var perm = Enumerable.Range(0, test.Count).ToArray();
            new Random(seed + j).Shuffle(perm);
            var permuted = test.Select((r, i) =>
            {
                var f = (float[])r.Features.Clone();
                f[j] = test[perm[i]].Features[j];
                return r with { Features = f };
            }).ToList();
            importance.Add((MlFeatures.Names[j], best.Eval.PrAuc - RankingMetrics.Evaluate(labels, Predict(best.Model, ml, permuted), true).PrAuc));
        }
        var top = importance.OrderByDescending(x => x.Drop).Take(15).ToList();

        var causes = truthPath is not null && File.Exists(truthPath) ? CauseCheck(test, best.Probs, rules, truthPath) : null;

        // ONNX: экспорт и сверка с ML.NET
        var onnxPath = Path.Combine(outDir, "model.onnx");
        using (var f = File.Create(onnxPath))
            ml.Model.ConvertToOnnx(best.Model, ml.Data.LoadFromEnumerable([new MlInput { Features = new float[MlFeatures.Names.Count] }], Schema<MlInput>()), f);
        var parity = Parity(onnxPath, test, best.Probs);
        if (parity > 1e-4) throw new InvalidOperationException($"ONNX расходится с ML.NET: {parity}");
        // Структура деревьев для точных вкладов (TreeSHAP) в анализаторе; покрытие узлов — по обучающим строкам
        var probe = Math.Min(test.Count, 2000);
        var trees = TreeExport.From(best.Model, MlFeatures.Names, train.Select(r => r.Features).ToList(), test.Take(probe).Select(r => r.Features).ToList(),
            best.Probs.Take(probe).ToList());
        var treesPath = Path.Combine(outDir, TreeEnsembleJson.FileName);
        if (trees is not null) File.WriteAllText(treesPath, TreeEnsembleJson.Serialize(trees));
        else File.Delete(treesPath);

        var createdAt = DateTimeOffset.UtcNow;
        var onnxSha = DatasetFile.Sha256(onnxPath);
        var card = new JsonObject
        {
            ["format"] = 1,
            ["version"] = $"{createdAt:yyyyMMdd-HHmm}-{chosen}-{onnxSha[..8]}",
            ["createdAt"] = DatasetFile.Iso(createdAt),
            ["featureSchema"] = MlFeatures.Schema,
            ["features"] = new JsonArray(MlFeatures.Names.Select(n => (JsonNode)n).ToArray()),
            ["input"] = OnnxInput,
            ["output"] = OnnxOutput,
            ["model"] = chosen,
            ["trainer"] = chosen == "fasttree"
                ? new JsonObject { ["kind"] = "FastTree (градиентный бустинг)", ["trees"] = 200, ["leaves"] = 20, ["minLeaf"] = 20, ["learningRate"] = 0.1 }
                : new JsonObject { ["kind"] = "L-BFGS логистическая регрессия", ["l2"] = 1.0, ["normalization"] = "min-max" },
            ["seed"] = seed,
            ["target"] = dataset["target"]?.DeepClone(),
            ["decisions"] = dataset["decisions"]?.DeepClone(),
            ["synthetic"] = dataset["synthetic"]?.DeepClone(),
            ["dataset"] = new JsonObject
            {
                ["source"] = dataset["source"]?.DeepClone(), ["sha256"] = dataset["sha256"]?.DeepClone(), ["rows"] = rows.Count,
                ["positives"] = rows.Count(r => r.Label == 1), ["dataStart"] = dataset["dataStart"]?.DeepClone(), ["dataEnd"] = dataset["dataEnd"]?.DeepClone(),
            },
            ["split"] = new JsonObject
            {
                ["testFrom"] = DatasetFile.Iso(testFrom), ["lastTrainDecision"] = DatasetFile.Iso(lastTrainDecision),
                ["gapDays"] = Math.Round((testFrom - lastTrainDecision).TotalDays, 1), ["trainRows"] = train.Count, ["trainPositives"] = trainPos,
                ["testRows"] = test.Count, ["testPositives"] = testPos, ["customersOutsideTrainingShare"] = 0.2,
                ["capacityShare"] = RankingMetrics.CapacityShare,
            },
            ["baseline"] = baselineName,
            ["metrics"] = new JsonObject { ["rules"] = Json(rulesEval), ["rulesWide"] = Json(rulesWideEval), ["logistic"] = results["logistic"]!.DeepClone(), ["fasttree"] = results["fasttree"]!.DeepClone() },
            ["topFeatures"] = new JsonArray(top.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["prAucDrop"] = Round(t.Drop) }).ToArray()),
            ["leakage"] = new JsonObject
            {
                ["shuffledLabelsPrAuc"] = Round(shuffleEval.PrAuc), ["shuffledLabelsRocAuc"] = Round(shuffleEval.RocAuc),
                ["baseRate"] = Round((double)testPos / test.Count), ["featuresWithoutIds"] = true,
            },
            ["causes"] = causes,
            ["onnxSha256"] = onnxSha,
            ["parityMaxAbsDiff"] = parity,
            ["explain"] = trees is null ? "occlusion" : "treeshap",
            ["treesSha256"] = trees is null ? null : DatasetFile.Sha256(Path.Combine(outDir, TreeEnsembleJson.FileName)),
            ["note"] = dataset["synthetic"]?.GetValue<bool>() == true
                ? "Обучено на журнале симулятора."
                : "Модель работает только в тени: продвижение — после проверки на реальных исходах и согласия по бизнес-цели.",
        };
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(outDir, "model.json"), card.ToJsonString(opts));
        File.WriteAllText(Path.Combine(outDir, "report.md"), Report.Markdown(card));
        var bundle = Path.Combine(outDir, BundleFile);
        if (File.Exists(bundle)) File.Delete(bundle);
        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Create))
            foreach (var file in new[] { "model.onnx", "model.json", "report.md", TreeEnsembleJson.FileName }.Where(f => File.Exists(Path.Combine(outDir, f))))
                zip.CreateEntryFromFile(Path.Combine(outDir, file), file);
        log($"Выбрана {chosen}; ONNX {new FileInfo(onnxPath).Length} байт, расхождение с ML.NET {parity:E1}; пакет {bundle}");
        return card;
    }

    internal static IEstimator<ITransformer> Pipeline(MLContext ml, string name) => name == "logistic"
        ? ml.Transforms.NormalizeMinMax("Features").Append(ml.BinaryClassification.Trainers.LbfgsLogisticRegression(
            new LbfgsLogisticRegressionBinaryTrainer.Options { L2Regularization = 1f, MaximumNumberOfIterations = 200 }))
        : ml.BinaryClassification.Trainers.FastTree(new FastTreeBinaryTrainer.Options
        {
            NumberOfLeaves = 20, NumberOfTrees = 200, MinimumExampleCountPerLeaf = 20, LearningRate = 0.1, NumberOfThreads = 1,
        });

    private static SchemaDefinition Schema<T>(int size)
    {
        var s = SchemaDefinition.Create(typeof(T));
        s["Features"].ColumnType = new VectorDataViewType(NumberDataViewType.Single, size);
        return s;
    }

    private static SchemaDefinition Schema<T>() => Schema<T>(MlFeatures.Names.Count);

    internal static IDataView Load(MLContext ml, IEnumerable<DatasetRow> rows) =>
        ml.Data.LoadFromEnumerable(rows.Select(r => new MlRow { Label = r.Label == 1, Features = r.Features }).ToList(), Schema<MlRow>());

    internal static double[] Predict(ITransformer model, MLContext ml, IEnumerable<DatasetRow> rows) =>
        model.Transform(Load(ml, rows)).GetColumn<float>("Probability").Select(p => (double)p).ToArray();

    /// <summary>Строки произвольной схемы признаков (размер вектора — <paramref name="size"/>)</summary>
    internal static IDataView LoadVectors(MLContext ml, IEnumerable<(bool Label, float[] Features)> rows, int size) =>
        ml.Data.LoadFromEnumerable(rows.Select(r => new MlRow { Label = r.Label, Features = r.Features }).ToList(), Schema<MlRow>(size));

    internal static double[] PredictVectors(ITransformer model, MLContext ml, IEnumerable<float[]> rows, int size) =>
        model.Transform(LoadVectors(ml, rows.Select(f => (false, f)), size)).GetColumn<float>("Probability").Select(p => (double)p).ToArray();

    /// <summary>Экспорт в ONNX для схемы размера <paramref name="size"/></summary>
    internal static void ExportOnnx(MLContext ml, ITransformer model, int size, string path)
    {
        using var f = File.Create(path);
        ml.Model.ConvertToOnnx(model, ml.Data.LoadFromEnumerable([new MlInput { Features = new float[size] }], Schema<MlInput>(size)), f);
    }

    internal static double OnnxParity(string onnx, IReadOnlyList<float[]> rows, IReadOnlyList<double> expected, int size)
    {
        using var session = new InferenceSession(onnx);
        var n = Math.Min(rows.Count, 2000);
        var t = new DenseTensor<float>(rows.Take(n).SelectMany(r => r).ToArray(), [n, size]);
        using var res = session.Run([NamedOnnxValue.CreateFromTensor(OnnxInput, t)]);
        var p = res.Single(r => r.Name == OnnxOutput).AsEnumerable<float>().ToArray();
        return Enumerable.Range(0, n).Max(i => Math.Abs(p[i] - expected[i]));
    }

    private static double Parity(string onnx, IReadOnlyList<DatasetRow> rows, IReadOnlyList<double> expected)
    {
        using var session = new InferenceSession(onnx);
        var n = Math.Min(rows.Count, 2000);
        var t = new DenseTensor<float>(rows.Take(n).SelectMany(r => r.Features).ToArray(), [n, MlFeatures.Names.Count]);
        using var res = session.Run([NamedOnnxValue.CreateFromTensor(OnnxInput, t)]);
        var p = res.Single(r => r.Name == OnnxOutput).AsEnumerable<float>().ToArray();
        return Enumerable.Range(0, n).Max(i => Math.Abs(p[i] - expected[i]));
    }

    /// <summary>Заложенные причины (truth.jsonl): полнота top-K по причинам и признаки, сильнее всего отличающие такие уходы</summary>
    private JsonObject CauseCheck(List<DatasetRow> test, double[] model, List<double> rules, string truthPath)
    {
        var lost = new Dictionary<long, List<(DateTimeOffset At, string Cause)>>();
        foreach (var line in File.ReadLines(truthPath))
        {
            if (!line.Contains("\"lost_all_services\"", StringComparison.Ordinal)) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            var a = r.GetProperty("a").GetInt64();
            if (!lost.TryGetValue(a, out var l)) lost[a] = l = [];
            l.Add((DateTimeOffset.Parse(r.GetProperty("at").GetString()!, CultureInfo.InvariantCulture), r.GetProperty("cause").GetString()!));
        }
        string CauseOf(DatasetRow r) => lost.TryGetValue(r.AccountId, out var l)
            ? l.Where(x => x.At >= r.DecisionAt.AddDays(-1) && x.At <= r.ExpiresAt.AddDays(graceDays + 3)).Select(x => x.Cause).FirstOrDefault() ?? "partial"
            : "partial";
        var k = Math.Max(1, (int)Math.Ceiling(test.Count * RankingMetrics.CapacityShare));
        var topModel = Enumerable.Range(0, test.Count).OrderByDescending(i => model[i]).ThenBy(i => i).Take(k).ToHashSet();
        var topRules = Enumerable.Range(0, test.Count).OrderByDescending(i => rules[i]).ThenBy(i => i).Take(k).ToHashSet();
        var negatives = Enumerable.Range(0, test.Count).Where(i => test[i].Label == 0).ToList();
        var result = new JsonObject();
        foreach (var g in Enumerable.Range(0, test.Count).Where(i => test[i].Label == 1).GroupBy(i => CauseOf(test[i])).OrderByDescending(g => g.Count()))
        {
            var idx = g.ToList();
            var signals = MlFeatures.Names.Select((name, j) =>
            {
                double Mean(IEnumerable<int> s) => s.Average(i => test[i].Features[j]);
                var all = idx.Concat(negatives).Select(i => (double)test[i].Features[j]).ToList();
                var mean = all.Average();
                var sd = Math.Sqrt(all.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, all.Count - 1));
                return (Name: name, Smd: sd < 1e-9 ? 0 : (Mean(idx) - Mean(negatives)) / sd);
            }).OrderByDescending(x => Math.Abs(x.Smd)).Take(5).ToList();
            var expected = Expected.GetValueOrDefault(g.Key) ?? [];
            result[g.Key] = new JsonObject
            {
                ["positives"] = idx.Count,
                ["recallAtKModel"] = Round((double)idx.Count(topModel.Contains) / idx.Count),
                ["recallAtKRules"] = Round((double)idx.Count(topRules.Contains) / idx.Count),
                ["signals"] = new JsonArray(signals.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["smd"] = Round(s.Smd) }).ToArray()),
                ["expected"] = new JsonArray(expected.Select(e => (JsonNode)e).ToArray()),
                ["signalsMatchExpected"] = expected.Length == 0 ? null : signals.Any(s => expected.Contains(s.Name)),
            };
        }
        return result;
    }

    internal static double RulesDefault(DatasetRow r) => (double)(r.RulesScore ?? 0);

    internal static double RulesWide(DatasetRow r) => (double)(r.RulesScoreWide ?? 0);

    internal static JsonObject Json(RankMetrics m) => new()
    {
        ["rows"] = m.Rows, ["positives"] = m.Positives, ["prAuc"] = Round(m.PrAuc), ["rocAuc"] = Round(m.RocAuc), ["k"] = m.K,
        ["precisionAtK"] = Round(m.PrecisionAtK), ["recallAtK"] = Round(m.RecallAtK), ["brier"] = m.Brier is { } b ? Round(b) : null,
    };

    internal static double Round(double x) => double.IsNaN(x) ? 0 : Math.Round(x, 4);
}
