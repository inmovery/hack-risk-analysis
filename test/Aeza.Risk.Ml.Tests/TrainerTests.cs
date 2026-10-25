using System.IO.Compression;
using System.Text.Json.Nodes;

using Aeza.Risk.Core;

using Microsoft.ML.OnnxRuntime;

using Xunit;

namespace Aeza.Risk.Ml.Tests;

/// <summary>Обучение и проверка на наборе с заложенным сигналом</summary>
public sealed class TrainerTests
{
    private static (List<DatasetRow> Rows, JsonObject Manifest) Planted(int n = 6000, int seed = 3)
    {
        var rnd = new Random(seed);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var login = MlFeatures.Index["days_since_login"];
        var cover = MlFeatures.Index["funds_cover"];
        var rows = new List<DatasetRow>();
        for (var i = 0; i < n; i++)
        {
            var f = new float[MlFeatures.Names.Count];
            for (var j = 0; j < f.Length; j++) f[j] = (float)rnd.NextDouble();
            f[login] = (float)(rnd.NextDouble() * 60);
            f[cover] = (float)(rnd.NextDouble() * 3);
            // Сигнал: долго без входа и не хватает денег → «не продлено»
            var logit = -4.0 + 0.09 * f[login] - 1.2 * f[cover];
            var label = rnd.NextDouble() < 1 / (1 + Math.Exp(-logit)) ? 1 : 0;
            var at = start.AddHours(i * 1.2);
            rows.Add(new DatasetRow(1 + i % 700, 10_000 + i, at, at.AddDays(7), 7, "month", label, rnd.Next(0, 60), rnd.Next(0, 80), f));
        }
        var manifest = new JsonObject
        {
            ["featureSchema"] = MlFeatures.Schema, ["target"] = "test", ["decisions"] = "test", ["synthetic"] = true, ["source"] = "planted",
            ["sha256"] = "", ["dataStart"] = DatasetFile.Iso(start), ["dataEnd"] = DatasetFile.Iso(rows[^1].DecisionAt),
        };
        return (rows, manifest);
    }

    [Fact]
    public void Trainer_FindsPlantedSignal_BeatsRules_ExportsMatchingOnnx()
    {
        var (rows, manifest) = Planted();
        var dir = PointInTimeTests.TempDir();
        var card = new Trainer(seed: 7).Run(rows, manifest, dir, truthPath: null, _ => { });

        var model = card["model"]!.GetValue<string>();
        var metrics = card["metrics"]![model]!;
        var rulesWide = card["metrics"]!["rulesWide"]!["prAuc"]!.GetValue<double>();
        Assert.True(metrics["time"]!["prAuc"]!.GetValue<double>() > rulesWide + 0.1);
        Assert.True(metrics["prAucMinusRulesCi95"]![0]!.GetValue<double>() > 0, "интервал разницы с правилами должен быть выше 0");
        var top = card["topFeatures"]!.AsArray().Take(2).Select(x => x!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("days_since_login", top);
        Assert.True(card["leakage"]!["shuffledLabelsRocAuc"]!.GetValue<double>() < 0.6, "на перемешанных метках модель должна быть случайной");
        Assert.True(card["parityMaxAbsDiff"]!.GetValue<double>() < 1e-4);
        Assert.Equal(MlFeatures.Names, card["features"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(7, card["seed"]!.GetValue<int>());

        using var zip = ZipFile.OpenRead(Path.Combine(dir, Trainer.BundleFile));
        var expectedEntries = card["explain"]!.GetValue<string>() == "treeshap"
            ? new[] { "model.onnx", "model.json", "report.md", TreeEnsembleJson.FileName }
            : ["model.onnx", "model.json", "report.md"];
        Assert.Equal(expectedEntries, zip.Entries.Select(e => e.FullName));
        var report = File.ReadAllText(Path.Combine(dir, "report.md"));
        Assert.Contains("журналу симулятора", report, StringComparison.Ordinal);
        Assert.Contains("перемешанных метках", report, StringComparison.Ordinal);
    }

    [Fact]
    public void FastTree_ExportsTrees_MatchingOnnx_ShapSumsToLogit()
    {
        var (rows, manifest) = Planted();
        var dir = PointInTimeTests.TempDir();
        var card = new Trainer(seed: 7, model: "fasttree").Run(rows, manifest, dir, truthPath: null, _ => { });
        Assert.Equal("treeshap", card["explain"]!.GetValue<string>());

        var trees = TreeEnsembleJson.Parse(File.ReadAllText(Path.Combine(dir, TreeEnsembleJson.FileName)));
        Assert.Equal(MlFeatures.Names, trees.Features);
        Assert.All(trees.Trees, t => Assert.Equal(t.NodeCover[0], t.LeafCover.Sum()));
        var sample = rows.Take(300).Select(r => r.Features).ToList();
        using var session = new InferenceSession(Path.Combine(dir, "model.onnx"));
        var tensor = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(sample.SelectMany(f => f).ToArray(), [sample.Count, MlFeatures.Names.Count]);
        using var res = session.Run([NamedOnnxValue.CreateFromTensor(Trainer.OnnxInput, tensor)]);
        var onnx = res.Single(r => r.Name == Trainer.OnnxOutput).AsEnumerable<float>().ToArray();
        for (var i = 0; i < sample.Count; i++)
        {
            Assert.True(Math.Abs(trees.Probability(sample[i]) - onnx[i]) < 1e-4, $"строка {i}: деревья {trees.Probability(sample[i])}, ONNX {onnx[i]}");
            var shap = TreeShap.Explain(trees, sample[i]);
            Assert.Equal(trees.Logit(sample[i]), shap.Base + shap.Phi.Sum(), 6);
        }
        // Заложенный сигнал — в признаках с наибольшими вкладами
        var mean = new double[MlFeatures.Names.Count];
        foreach (var x in sample)
        {
            var phi = TreeShap.Explain(trees, x).Phi;
            for (var j = 0; j < mean.Length; j++) mean[j] += Math.Abs(phi[j]) / sample.Count;
        }
        var top = Enumerable.Range(0, mean.Length).OrderByDescending(j => mean[j]).Take(2).Select(j => MlFeatures.Names[j]).ToList();
        Assert.Contains("days_since_login", top);
        Assert.Contains("funds_cover", top);
    }

    [Fact]
    public void Trainer_SplitsByTime_TrainLabelsMatureBeforeTest()
    {
        var (rows, manifest) = Planted();
        var card = new Trainer(seed: 1, model: "logistic").Run(rows, manifest, PointInTimeTests.TempDir(), null, _ => { });
        var split = card["split"]!;
        var testFrom = DateTimeOffset.Parse(split["testFrom"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        var last = DateTimeOffset.Parse(split["lastTrainDecision"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        // Решение E − 7, исход созревает к E + 5: между последним решением обучения и проверкой не меньше k + N = 12 дней
        Assert.True((testFrom - last).TotalDays >= 12, $"зазор {(testFrom - last).TotalDays} дн.");
        Assert.Equal("logistic", card["model"]!.GetValue<string>());
        Assert.Equal(rows.Count(r => r.DecisionAt >= testFrom), split["testRows"]!.GetValue<int>());
    }

    [Fact]
    public void Trainer_RefusesTooFewOutcomes()
    {
        var (rows, manifest) = Planted(400);
        Assert.Throws<InvalidOperationException>(() => new Trainer().Run(rows, manifest, PointInTimeTests.TempDir(), null, _ => { }));
    }

    /// <summary>Фикстура интеграционных тестов анализатора обучена текущей схемой признаков и даёт вероятности</summary>
    [Fact]
    public void IntegrationFixture_MatchesCurrentFeatureSchema()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "model-bundle.zip");
        using var zip = ZipFile.OpenRead(path);
        var card = JsonNode.Parse(zip.GetEntry("model.json")!.Open())!;
        Assert.Equal(MlFeatures.Schema, card["featureSchema"]!.GetValue<string>());
        Assert.Equal(MlFeatures.Names, card["features"]!.AsArray().Select(x => x!.GetValue<string>()));
        using var ms = new MemoryStream();
        zip.GetEntry("model.onnx")!.Open().CopyTo(ms);
        using var session = new InferenceSession(ms.ToArray());
        Assert.Equal(MlFeatures.Names.Count, session.InputMetadata[Trainer.OnnxInput].Dimensions[1]);
        Assert.Contains(Trainer.OnnxOutput, session.OutputMetadata.Keys);
    }
}
