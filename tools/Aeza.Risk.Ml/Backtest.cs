using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Aeza.Risk.Core;

using Microsoft.ML;

using RankingMetrics = Aeza.Risk.Core.RankingMetrics;

namespace Aeza.Risk.Ml;

/// <summary>Проверка по годам: для каждого года модель учится только на сроках, исход которых известен до начала года, и проверяется на решениях
/// этого года. Показывает, держится ли выигрыш у правил год за годом, а не на одном удачном отрезке</summary>
public sealed class Backtest(int seed = 42, int graceDays = 5, string model = "fasttree")
{
    private const int MinTrainPositives = 100;
    private const int MinTestPositives = 30;

    /// <summary>Обучающие и проверочные строки года <paramref name="year"/></summary>
    public static (List<DatasetRow> Train, List<DatasetRow> Test) Split(IReadOnlyList<DatasetRow> rows, int year, int graceDays)
    {
        var from = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddYears(1);
        return (rows.Where(r => r.ExpiresAt.AddDays(graceDays) < from).ToList(), rows.Where(r => r.DecisionAt >= from && r.DecisionAt < to).ToList());
    }

    public JsonObject Run(List<DatasetRow> rows, JsonObject manifest, IReadOnlyList<CustomerWeekRow> customers, string outDir, Action<string> log)
    {
        Directory.CreateDirectory(outDir);
        var ml = new MLContext(seed);
        var years = new JsonArray();
        foreach (var year in rows.Select(r => r.DecisionAt.Year).Distinct().Order())
        {
            var (train, test) = Split(rows, year, graceDays);
            var trainPos = train.Count(r => r.Label == 1);
            var testPos = test.Count(r => r.Label == 1);
            var y = new JsonObject
            {
                ["year"] = year, ["trainRows"] = train.Count, ["trainPositives"] = trainPos, ["testRows"] = test.Count, ["testPositives"] = testPos,
                ["baseRate"] = test.Count == 0 ? null : Trainer.Round((double)testPos / test.Count),
            };
            AddCustomers(y, customers, year);
            if (trainPos < MinTrainPositives || testPos < MinTestPositives)
            {
                y["skipped"] = $"мало исходов: обучение {trainPos} (нужно ≥ {MinTrainPositives}), проверка {testPos} (≥ {MinTestPositives})";
                years.Add(y);
                log($"{year}: пропущен — {y["skipped"]}");
                continue;
            }
            var labels = test.Select(r => r.Label).ToList();
            var fitted = Trainer.Pipeline(ml, model).Fit(Trainer.Load(ml, train));
            var probs = Trainer.Predict(fitted, ml, test);
            var m = RankingMetrics.Evaluate(labels, probs, probabilities: true);
            var rules = RankingMetrics.Evaluate(labels, test.Select(Trainer.RulesDefault).ToList(), probabilities: false);
            var wide = RankingMetrics.Evaluate(labels, test.Select(Trainer.RulesWide).ToList(), probabilities: false);
            var best = wide.PrAuc > rules.PrAuc ? wide : rules;
            var bins = RankingMetrics.Calibration(labels, probs);
            var ece = bins.Sum(b => (double)b.Rows / test.Count * Math.Abs(b.Predicted - b.Observed));
            y["model"] = Trainer.Json(m);
            y["rules"] = Trainer.Json(best);
            y["rulesKind"] = wide.PrAuc > rules.PrAuc ? "rulesWide" : "rules";
            y["prAucGain"] = Trainer.Round(m.PrAuc - best.PrAuc);
            y["ece"] = Trainer.Round(ece);
            y["money"] = MoneyCoverage(test, probs);
            years.Add(y);
            log($"{year}: обучение {train.Count} ({trainPos}), проверка {test.Count} ({testPos}); PR-AUC модели {m.PrAuc:0.000}, правил {best.PrAuc:0.000}; " +
                $"P@K {m.PrecisionAtK:0.000} против {best.PrecisionAtK:0.000}; Brier {m.Brier:0.000}, ECE {ece:0.000}");
        }

        var scored = years.OfType<JsonObject>().Where(y => y["model"] is not null).ToList();
        var result = new JsonObject
        {
            ["kind"] = "backtest",
            ["model"] = model,
            ["seed"] = seed,
            ["split"] = "по календарным годам: обучение — сроки, исход которых известен до 1 января (срок + N дней), проверка — решения этого года",
            ["dataset"] = new JsonObject
            {
                ["source"] = manifest["source"]?.DeepClone(), ["sha256"] = manifest["sha256"]?.DeepClone(), ["synthetic"] = manifest["synthetic"]?.DeepClone(),
                ["dataStart"] = manifest["dataStart"]?.DeepClone(), ["dataEnd"] = manifest["dataEnd"]?.DeepClone(), ["rows"] = rows.Count,
            },
            ["years"] = years,
            ["summary"] = scored.Count == 0 ? null : new JsonObject
            {
                ["yearsScored"] = scored.Count,
                ["yearsModelBetter"] = scored.Count(y => y["prAucGain"]!.GetValue<double>() > 0),
                ["meanPrAucModel"] = Trainer.Round(scored.Average(y => y["model"]!["prAuc"]!.GetValue<double>())),
                ["meanPrAucRules"] = Trainer.Round(scored.Average(y => y["rules"]!["prAuc"]!.GetValue<double>())),
                ["minPrAucGain"] = Trainer.Round(scored.Min(y => y["prAucGain"]!.GetValue<double>())),
                ["meanEce"] = Trainer.Round(scored.Average(y => y["ece"]!.GetValue<double>())),
                ["moneyMonths"] = scored.Sum(y => y["money"]!["months"]!.GetValue<int>()),
                ["moneyCovered"] = scored.Sum(y => y["money"]!["covered"]!.GetValue<int>()),
                ["moneyCoverage"] = Trainer.Round((double)scored.Sum(y => y["money"]!["covered"]!.GetValue<int>())
                                                  / Math.Max(1, scored.Sum(y => y["money"]!["months"]!.GetValue<int>()))),
                ["expectedEur"] = Math.Round(scored.Sum(y => y["money"]!["expectedEur"]!.GetValue<double>()), 2),
                ["realizedEur"] = Math.Round(scored.Sum(y => y["money"]!["realizedEur"]!.GetValue<double>()), 2),
            },
            ["note"] = manifest["synthetic"]?.GetValue<bool>() == true
                ? "Данные — журнал симулятора."
                : "Реальные данные источника.",
        };
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(outDir, "backtest.json"), result.ToJsonString(opts));
        File.WriteAllText(Path.Combine(outDir, "backtest.md"), Markdown(result));
        return result;
    }

    /// <summary>Деньги: по каждому месяцу сроков — ожидаемая потеря от непродлений с 90 % интервалом против реальной. Берётся последнее решение
    /// по каждому сроку (без двойного счёта), сумма — цена периода</summary>
    private static JsonObject MoneyCoverage(IReadOnlyList<DatasetRow> test, IReadOnlyList<double> probs)
    {
        var price = MlFeatures.Index["price_month_eur"];
        static double Months(string term) => term switch { "week" => 7 / 30.0, "quarter_year" => 3, "half_year" => 6, "year" => 12, _ => 1 };
        var latest = test.Select((r, i) => (Row: r, P: probs[i])).GroupBy(x => (x.Row.ServiceId, x.Row.ExpiresAt)).Select(g => g.MinBy(x => x.Row.OffsetDays)).ToList();
        int months = 0, covered = 0;
        double expected = 0, realized = 0;
        foreach (var month in latest.GroupBy(x => (x.Row.ExpiresAt.Year, x.Row.ExpiresAt.Month)))
        {
            var items = month.Select(x => (Amount: (long)Math.Round(x.Row.Features[price] * Months(x.Row.Term) * 100), x.P, x.Row.Label)).ToList();
            var interval = ExpectedLoss.Of(items.Select(x => (x.Amount, x.P)));
            var lost = items.Where(x => x.Label == 1).Sum(x => x.Amount);
            months++;
            if (lost >= interval.LowMinor && lost <= interval.HighMinor) covered++;
            expected += interval.ExpectedMinor / 100.0;
            realized += lost / 100.0;
        }
        return new JsonObject
        {
            ["months"] = months, ["covered"] = covered, ["coverage"] = months == 0 ? null : Trainer.Round((double)covered / months),
            ["expectedEur"] = Math.Round(expected, 2), ["realizedEur"] = Math.Round(realized, 2),
        };
    }

    /// <summary>Метки клиентов года: доля ушедших за 30 и 90 дней и сокративших выручку (по известным исходам)</summary>
    private static void AddCustomers(JsonObject y, IReadOnlyList<CustomerWeekRow> customers, int year)
    {
        var rows = customers.Where(c => c.At.Year == year).ToList();
        if (rows.Count == 0) return;
        static double? Rate(IEnumerable<int> labels)
        {
            var known = labels.Where(l => l >= 0).ToList();
            return known.Count == 0 ? null : Trainer.Round(known.Average());
        }
        y["customerWeeks"] = rows.Count;
        y["customers"] = rows.Select(r => r.AccountId).Distinct().Count();
        y["churn30Rate"] = Rate(rows.Select(r => r.Churn30));
        y["churn90Rate"] = Rate(rows.Select(r => r.Churn90));
        y["contraction30Rate"] = Rate(rows.Select(r => r.Contraction30));
    }

    private static string Markdown(JsonObject r)
    {
        static string N(JsonNode? n, string format = "0.000") => n is null ? "—" : n.GetValue<double>().ToString(format, CultureInfo.InvariantCulture);
        static string Pct(JsonNode? n) => n is null ? "—" : (n.GetValue<double>() * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        var sb = new StringBuilder();
        sb.AppendLine($"# Проверка по годам ({r["model"]})").AppendLine();
        sb.AppendLine($"Набор: {r["dataset"]!["source"]}, {r["dataset"]!["dataStart"]} … {r["dataset"]!["dataEnd"]}, строк {r["dataset"]!["rows"]}.").AppendLine();
        sb.AppendLine($"Разбиение: {r["split"]}.").AppendLine();
        sb.AppendLine("| Год | Проверка (не продлено) | Доля | PR-AUC модели | PR-AUC правил | Выигрыш | P@5% модели | P@5% правил | Brier | ECE | Деньги в 90 % интервале | Клиентов | Уход 30 дн. | Уход 90 дн. | Сокращение 30 дн. |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var y in r["years"]!.AsArray().OfType<JsonObject>())
        {
            var skipped = y["skipped"] is not null;
            sb.AppendLine($"| {y["year"]} | {y["testRows"]} ({y["testPositives"]}) | {Pct(y["baseRate"])} | " +
                          (skipped ? "— | — | — | — | — | — | — | — " : $"{N(y["model"]!["prAuc"])} | {N(y["rules"]!["prAuc"])} | {N(y["prAucGain"], "+0.000;−0.000")} | " +
                                                                    $"{N(y["model"]!["precisionAtK"])} | {N(y["rules"]!["precisionAtK"])} | {N(y["model"]!["brier"])} | {N(y["ece"])} | " +
                                                                    $"{y["money"]!["covered"]} из {y["money"]!["months"]} мес.") +
                          $" | {y["customers"]?.ToString() ?? "—"} | {Pct(y["churn30Rate"])} | {Pct(y["churn90Rate"])} | {Pct(y["contraction30Rate"])} |");
        }
        if (r["summary"] is JsonObject s)
        {
            sb.AppendLine().AppendLine($"Модель лучше правил в {s["yearsModelBetter"]} из {s["yearsScored"]} лет; средний PR-AUC {N(s["meanPrAucModel"])} против {N(s["meanPrAucRules"])}, " +
                                       $"худший год — выигрыш {N(s["minPrAucGain"], "+0.000;−0.000")}; средняя ошибка калибровки ECE {N(s["meanEce"])}.");
            sb.AppendLine().AppendLine($"Деньги: реальная потеря от непродлений попала в 90 % интервал модели в {s["moneyCovered"]} из {s["moneyMonths"]} месяцев " +
                                       $"({Pct(s["moneyCoverage"])}); ожидалось €{s["expectedEur"]}, потеряно €{s["realizedEur"]}. Интервал считает суммы независимыми — " +
                                       "общие причины (рост цен, аварии) расширяют реальный разброс.");
        }
        sb.AppendLine().AppendLine(r["note"]!.GetValue<string>());
        return sb.ToString();
    }
}
