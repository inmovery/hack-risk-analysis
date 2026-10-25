using System.Text.Json.Nodes;

using Aeza.Risk.Core;

using Xunit;

namespace Aeza.Risk.Ml.Tests;

/// <summary>Метки клиента по неделям и проверка по годам</summary>
public sealed class CustomerLabelsTests
{
    private static readonly DateTimeOffset Monday = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    private static ServiceInput Svc(long id, long owner, string status, long price) =>
        new(id, owner, $"s{id}", "month", true, Monday.AddDays(-60), Monday.AddDays(20), status, price);

    [Fact]
    public void Churn_IsLossWithoutReturn_Contraction_IsDropWhileStaying_UnknownIsCensored()
    {
        var t = new CustomerTimeline(Monday.AddDays(-1));
        // 1: теряет все услуги через 10 дней и не возвращается
        t.Observe(1, Monday.AddDays(-1), [Svc(1, 1, "active", 1000)]);
        // 2: теряет услуги, но через 10 дней заказывает новую — не уход
        t.Observe(2, Monday.AddDays(-1), [Svc(2, 2, "active", 1000)]);
        // 3: выручка падает с €20 до €10 через 9 дней и остаётся — сокращение
        t.Observe(3, Monday.AddDays(-1), [Svc(3, 3, "active", 1000), Svc(4, 3, "active", 1000)]);

        t.SnapshotUntil(Monday.AddDays(1));
        t.Observe(1, Monday.AddDays(10), [Svc(1, 1, "deleted", 1000)]);
        t.Observe(2, Monday.AddDays(10), [Svc(2, 2, "deleted", 1000)]);
        t.Observe(3, Monday.AddDays(9), [Svc(3, 3, "active", 1000), Svc(4, 3, "deleted", 1000)]);
        t.Observe(2, Monday.AddDays(20), [Svc(2, 2, "deleted", 1000), Svc(5, 2, "active", 500)]);
        var end = Monday.AddDays(150);
        t.SnapshotUntil(end);
        var rows = t.Label(end);

        var first = rows.Where(r => r.At == Monday).ToDictionary(r => r.AccountId);
        Assert.Equal(3, first.Count);
        Assert.Equal((1, 1), (first[1].Churn30, first[1].Churn90));
        Assert.True(first[1].ChurnObserved);
        Assert.Equal(10.0 / 7, first[1].WeeksToChurn, 2);
        Assert.Equal((0, 0), (first[2].Churn30, first[2].Churn90)); // вернулся за 10 дней
        Assert.Equal(1, first[3].Contraction30);
        Assert.Equal(0, first[3].Churn30);
        Assert.Equal(2000, first[3].RevenueMonthMinor);
        // Ушедший клиент больше не попадает в срезы; у последних срезов исходы ещё неизвестны
        Assert.DoesNotContain(rows, r => r.AccountId == 1 && r.At > Monday.AddDays(10));
        var last = rows.Where(r => r.AccountId == 3).MaxBy(r => r.At)!;
        Assert.Equal(-1, last.Churn30);
        Assert.Equal(-1, last.Contraction30);
        Assert.False(last.ChurnObserved);
    }

    [Fact]
    public void Backtest_TrainLabelsMatureBeforeTheYear_TestIsThatYear()
    {
        var rows = Planted(3);
        foreach (var year in new[] { 2025, 2026 })
        {
            var (train, test) = Backtest.Split(rows, year, 5);
            var from = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
            Assert.NotEmpty(train);
            Assert.NotEmpty(test);
            Assert.All(train, r => Assert.True(r.ExpiresAt.AddDays(5) < from));
            Assert.All(test, r => Assert.Equal(year, r.DecisionAt.Year));
        }
    }

    [Fact]
    public void Backtest_ReportsEachYear_ModelBeatsRulesOnPlantedSignal()
    {
        var rows = Planted(3);
        var manifest = new JsonObject { ["source"] = "planted", ["sha256"] = "", ["synthetic"] = true, ["dataStart"] = "", ["dataEnd"] = "" };
        var dir = PointInTimeTests.TempDir();

        var result = new Backtest(seed: 1).Run(rows, manifest, [], dir, _ => { });

        var years = result["years"]!.AsArray().Select(y => y!["year"]!.GetValue<int>()).ToList();
        Assert.Equal([2024, 2025, 2026], years);
        Assert.NotNull(result["years"]![0]!["skipped"]); // первый год: учиться не на чем
        var summary = result["summary"]!;
        Assert.Equal(2, summary["yearsScored"]!.GetValue<int>());
        Assert.Equal(2, summary["yearsModelBetter"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(dir, "backtest.md")));
    }

    [Fact]
    public void CustomerModel_FindsMigrationSignal_BeatsRulesEveryYear_ExportsTrees()
    {
        var rnd = new Random(9);
        var n = MlCustomerFeatures.Names.Count;
        var dns = MlCustomerFeatures.Index["dns_external_30"];
        var cover = MlCustomerFeatures.Index["funds_cover_month"];
        var rows = new List<CustomerWeekRow>();
        var start = new DateTimeOffset(2023, 1, 2, 0, 0, 0, TimeSpan.Zero);
        for (var week = 0; week < 52 * 3; week++)
            for (var c = 0; c < 150; c++)
            {
                var f = new float[n];
                f[dns] = rnd.NextDouble() < 0.08 ? 1 : 0;
                f[cover] = (float)(rnd.NextDouble() * 3);
                // Уход: перевёл DNS к другому провайдеру или кончились деньги
                var churn = rnd.NextDouble() < 1 / (1 + Math.Exp(-(-3.0 + 3.5 * f[dns] - 1.0 * f[cover]))) ? 1 : 0;
                rows.Add(new CustomerWeekRow(c, start.AddDays(7 * week), 1, 1000, churn, churn, 0, 1, churn == 1, null, rnd.Next(0, 60), f));
            }
        var dir = PointInTimeTests.TempDir();
        var manifest = new JsonObject { ["source"] = "planted", ["synthetic"] = true, ["dataStart"] = "", ["dataEnd"] = "" };

        var card = new CustomerTrainer(seed: 3).Run(rows, manifest, dir, _ => { });

        Assert.Equal(MlCustomerFeatures.Schema, card["featureSchema"]!.GetValue<string>());
        var bt = card["backtest"]!;
        Assert.True(bt["yearsScored"]!.GetValue<int>() >= 2);
        Assert.Equal(bt["yearsScored"]!.GetValue<int>(), bt["yearsModelBetter"]!.GetValue<int>());
        Assert.Equal("treeshap", card["explain"]!.GetValue<string>());
        Assert.Contains("dns_external_30", card["topFeatures"]!.AsArray().Take(3).Select(t => t!["name"]!.GetValue<string>()));
        Assert.True(card["parityMaxAbsDiff"]!.GetValue<double>() < 1e-4);
        using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(dir, Trainer.BundleFile));
        Assert.Contains(TreeEnsembleJson.FileName, zip.Entries.Select(e => e.FullName));
    }

    /// <summary>Решения за <paramref name="years"/> года с 2024-го: сигнал — долго без входа и мало денег; правила его не видят</summary>
    private static List<DatasetRow> Planted(int years)
    {
        var rnd = new Random(5);
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var login = MlFeatures.Index["days_since_login"];
        var cover = MlFeatures.Index["funds_cover"];
        var rows = new List<DatasetRow>();
        for (var at = start; at < start.AddYears(years); at = at.AddHours(3))
        {
            var f = new float[MlFeatures.Names.Count];
            f[login] = (float)(rnd.NextDouble() * 60);
            f[cover] = (float)(rnd.NextDouble() * 3);
            var label = rnd.NextDouble() < 1 / (1 + Math.Exp(-(-4.0 + 0.09 * f[login] - 1.2 * f[cover]))) ? 1 : 0;
            rows.Add(new DatasetRow(1 + rows.Count % 900, 10_000 + rows.Count, at, at.AddDays(7), 7, "month", label, rnd.Next(0, 60), rnd.Next(0, 80), f));
        }
        return rows;
    }
}
