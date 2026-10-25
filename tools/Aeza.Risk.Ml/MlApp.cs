using Aeza.Risk.Core;

namespace Aeza.Risk.Ml;

/// <summary>Офлайн-ML: dataset — набор point-in-time из журнала (--dataset DIR файлы симулятора или --db строка подключения к БД анализатора), train — обучение, сравнение с правилами, отчёт, пакет модели для PUT /api/v1/ml/model, backtest — проверка по годам, train-customer — модели клиента (уход, сокращение выручки)</summary>
public static class MlApp
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0) return Usage();
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length - 1; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
        try
        {
            switch (args[0])
            {
                case "dataset":
                {
                    IJournal journal = opts.TryGetValue("db", out var db) ? new DbJournal(db) : new FileJournal(opts["dataset"]);
                    var grace = int.Parse(opts.GetValueOrDefault("grace", "5"), System.Globalization.CultureInfo.InvariantCulture);
                    var started = DateTime.UtcNow;
                    var build = new PointInTimeBuilder(grace).Build(journal);
                    var m = DatasetFile.Write(build, opts["out"], journal.Describe, journal.Synthetic, grace);
                    Console.WriteLine($"Набор: {build.Rows.Count} строк (не продлено {build.Positives}, продлено {build.Negatives}), исключено {build.Excluded}, " +
                                      $"цензура {build.Censored}, пропущено {build.Skipped}; событий {build.Events}; {(DateTime.UtcNow - started).TotalSeconds:0} с → {opts["out"]}");
                    Console.WriteLine($"Период {m["dataStart"]} … {m["dataEnd"]}, sha256 {m["sha256"]}");
                    if (m["customers"] is { } c)
                        Console.WriteLine($"Срезы клиентов: {c["rows"]} ({c["customers"]} клиентов); уход за 30 дн. {c["churn30"]!.ToJsonString()}, сокращение {c["contraction30"]!.ToJsonString()}");
                    return 0;
                }
                case "backtest":
                {
                    var (rows, manifest) = DatasetFile.Read(opts["data"]);
                    var seed = int.Parse(opts.GetValueOrDefault("seed", "42"), System.Globalization.CultureInfo.InvariantCulture);
                    var grace = manifest["nonRenewalDays"]?.GetValue<int>() ?? 5;
                    var kind = opts.GetValueOrDefault("model", "fasttree");
                    if (kind is not ("logistic" or "fasttree")) return Usage();
                    var started = DateTime.UtcNow;
                    var result = new Backtest(seed, grace, kind).Run(rows, manifest, DatasetFile.ReadCustomers(opts["data"]), opts["out"], Console.WriteLine);
                    Console.WriteLine($"Проверка по годам за {(DateTime.UtcNow - started).TotalSeconds:0} с; отчёт {Path.Combine(opts["out"], "backtest.md")}");
                    return result["summary"] is null ? 1 : 0;
                }
                case "train":
                {
                    var (rows, manifest) = DatasetFile.Read(opts["data"]);
                    var seed = int.Parse(opts.GetValueOrDefault("seed", "42"), System.Globalization.CultureInfo.InvariantCulture);
                    var grace = manifest["nonRenewalDays"]?.GetValue<int>() ?? 5;
                    var started = DateTime.UtcNow;
                    var kind = opts.GetValueOrDefault("model") is { } k && k != "auto" ? k : null;
                    if (kind is not (null or "logistic" or "fasttree")) return Usage();
                    var card = new Trainer(seed, grace, model: kind).Run(rows, manifest, opts["out"], opts.GetValueOrDefault("truth"), Console.WriteLine);
                    Console.WriteLine($"Модель {card["version"]} за {(DateTime.UtcNow - started).TotalSeconds:0} с; отчёт {Path.Combine(opts["out"], "report.md")}");
                    return 0;
                }
                case "train-customer":
                {
                    var (_, manifest) = DatasetFile.Read(opts["data"]);
                    var seed = int.Parse(opts.GetValueOrDefault("seed", "42"), System.Globalization.CultureInfo.InvariantCulture);
                    var target = opts.GetValueOrDefault("target", CustomerTrainer.Churn30);
                    if (!CustomerTrainer.IsTarget(target)) return Usage();
                    var started = DateTime.UtcNow;
                    var card = new CustomerTrainer(seed, target).Run(DatasetFile.ReadCustomers(opts["data"]), manifest, opts["out"], Console.WriteLine);
                    Console.WriteLine($"Модель клиента {card["version"]} за {(DateTime.UtcNow - started).TotalSeconds:0} с; отчёт {Path.Combine(opts["out"], "report.md")}");
                    return 0;
                }
                default:
                    return Usage();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("dataset --dataset DIR | --db CONNECTION --out DIR [--grace 5]\ntrain --data DIR --out DIR [--truth truth.jsonl] [--seed 42] [--model auto|logistic|fasttree]\n" +
                                "backtest --data DIR --out DIR [--seed 42] [--model fasttree|logistic]\n" +
                                "train-customer --data DIR --out DIR [--target churn30|churn90|contraction30] [--seed 42]");
        return 2;
    }
}
