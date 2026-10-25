using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Aeza.Risk.Core;

namespace Aeza.Risk.Ml;

/// <summary>Набор на диске: dataset.csv (строки) + dataset.json (схема, счётчики, период, источник, SHA-256)</summary>
public static class DatasetFile
{
    public const string CsvFile = "dataset.csv";
    public const string CustomersFile = "customers.csv";
    public const string ManifestFile = "dataset.json";
    private static readonly string[] CustomerKeys =
        ["account_id", "at", "live_services", "revenue_month_minor", "churn_30", "churn_90", "contraction_30", "weeks_to_churn", "churn_observed", "exit_reason",
         "rules_score"];
    private static readonly string[] Keys = ["account_id", "service_id", "decision_at", "expires_at", "offset_days", "term", "label", "rules_score", "rules_score_w30"];

    public static JsonObject Write(DatasetBuild build, string dir, string source, bool synthetic, int graceDays)
    {
        Directory.CreateDirectory(dir);
        var csv = Path.Combine(dir, CsvFile);
        using (var w = new StreamWriter(csv, false, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            w.WriteLine(string.Join(',', Keys.Concat(MlFeatures.Names)));
            foreach (var r in build.Rows)
            {
                var sb = new StringBuilder();
                sb.Append(r.AccountId).Append(',').Append(r.ServiceId).Append(',').Append(Iso(r.DecisionAt)).Append(',').Append(Iso(r.ExpiresAt)).Append(',')
                  .Append(r.OffsetDays).Append(',').Append(r.Term).Append(',').Append(r.Label).Append(',')
                  .Append(r.RulesScore?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                  .Append(r.RulesScoreWide?.ToString(CultureInfo.InvariantCulture) ?? "");
                foreach (var f in r.Features) sb.Append(',').Append(f.ToString("R", CultureInfo.InvariantCulture));
                w.WriteLine(sb.ToString());
            }
        }
        var manifest = new JsonObject
        {
            ["featureSchema"] = MlFeatures.Schema,
            ["features"] = new JsonArray(MlFeatures.Names.Select(n => (JsonNode)n).ToArray()),
            ["target"] = $"не продлено в течение {graceDays} дн. после срока (как expiry_outcomes); blocked исключён; незрелые сроки — цензура",
            ["decisions"] = "за 14 и 7 дней до срока (месяц и дольше), за 3 дня (неделя); почасовые не входят",
            ["nonRenewalDays"] = graceDays,
            ["baselines"] = "rules_score — правила с настройками по умолчанию (окно срочности 7 дн.); rules_score_w30 — те же веса, окно 30 дн.",
            ["source"] = source,
            ["synthetic"] = synthetic,
            ["rows"] = build.Rows.Count,
            ["positives"] = build.Positives,
            ["negatives"] = build.Negatives,
            ["excluded"] = build.Excluded,
            ["censored"] = build.Censored,
            ["skipped"] = build.Skipped,
            ["decisionsTotal"] = build.Decisions,
            ["events"] = build.Events,
            ["dataStart"] = Iso(build.DataStart),
            ["dataEnd"] = Iso(build.DataEnd),
            ["sha256"] = Sha256(csv),
        };
        if (build.Customers is { Count: > 0 } customers) manifest["customers"] = WriteCustomers(customers, dir);
        File.WriteAllText(Path.Combine(dir, ManifestFile), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return manifest;
    }

    /// <summary>Еженедельные срезы клиентов: customers.csv и сводка меток по годам для манифеста</summary>
    private static JsonObject WriteCustomers(IReadOnlyList<CustomerWeekRow> rows, string dir)
    {
        var path = Path.Combine(dir, CustomersFile);
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            w.WriteLine(string.Join(',', CustomerKeys.Concat(MlCustomerFeatures.Names)));
            foreach (var r in rows)
            {
                var sb = new StringBuilder(string.Join(',', r.AccountId.ToString(CultureInfo.InvariantCulture), Iso(r.At), r.LiveServices.ToString(CultureInfo.InvariantCulture),
                    r.RevenueMonthMinor.ToString(CultureInfo.InvariantCulture), r.Churn30.ToString(CultureInfo.InvariantCulture), r.Churn90.ToString(CultureInfo.InvariantCulture),
                    r.Contraction30.ToString(CultureInfo.InvariantCulture), r.WeeksToChurn.ToString("R", CultureInfo.InvariantCulture), r.ChurnObserved ? "1" : "0", r.ExitReason ?? "",
                    r.RulesScore?.ToString(CultureInfo.InvariantCulture) ?? ""));
                foreach (var f in r.Features ?? new float[MlCustomerFeatures.Names.Count]) sb.Append(',').Append(f.ToString("R", CultureInfo.InvariantCulture));
                w.WriteLine(sb.ToString());
            }
        }
        static JsonObject Counts(IEnumerable<int> labels)
        {
            var l = labels.ToList();
            return new JsonObject { ["yes"] = l.Count(x => x == 1), ["no"] = l.Count(x => x == 0), ["censored"] = l.Count(x => x == -1) };
        }
        return new JsonObject
        {
            ["rows"] = rows.Count,
            ["customers"] = rows.Select(r => r.AccountId).Distinct().Count(),
            ["featureSchema"] = MlCustomerFeatures.Schema,
            ["features"] = new JsonArray(MlCustomerFeatures.Names.Select(n => (JsonNode)n).ToArray()),
            ["baseline"] = "rules_score — балл правил клиента на момент среза (веса по умолчанию)",
            ["definition"] = $"срез — понедельник 00:00 UTC, клиенты с действующими услугами; уход — нет действующих услуг и нет возврата {CustomerTimeline.ReturnDays} дн.; " +
                             $"сокращение — через 30 дн. выручка ниже на {CustomerTimeline.ContractionShare:P0} и больше при оставшихся услугах; −1 — исход неизвестен",
            ["churn30"] = Counts(rows.Select(r => r.Churn30)),
            ["churn90"] = Counts(rows.Select(r => r.Churn90)),
            ["contraction30"] = Counts(rows.Select(r => r.Contraction30)),
            ["exitReasons"] = new JsonObject(rows.Where(r => r.ExitReason is not null).DistinctBy(r => r.AccountId).GroupBy(r => r.ExitReason!)
                .OrderByDescending(g => g.Count()).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            ["sha256"] = Sha256(path),
        };
    }

    public static List<CustomerWeekRow> ReadCustomers(string dir)
    {
        var path = Path.Combine(dir, CustomersFile);
        if (!File.Exists(path)) return [];
        var rows = new List<CustomerWeekRow>();
        using var reader = new StreamReader(path, Encoding.UTF8);
        var header = reader.ReadLine()!.Split(',');
        if (!header.Skip(CustomerKeys.Length).SequenceEqual(MlCustomerFeatures.Names))
            throw new InvalidOperationException($"Колонки customers.csv не совпадают со схемой {MlCustomerFeatures.Schema}: пересоберите набор");
        while (reader.ReadLine() is { } line)
        {
            var p = line.Split(',');
            var f = new float[MlCustomerFeatures.Names.Count];
            for (var i = 0; i < f.Length; i++) f[i] = float.Parse(p[CustomerKeys.Length + i], CultureInfo.InvariantCulture);
            rows.Add(new CustomerWeekRow(long.Parse(p[0], CultureInfo.InvariantCulture), DateTimeOffset.Parse(p[1], CultureInfo.InvariantCulture),
                int.Parse(p[2], CultureInfo.InvariantCulture), long.Parse(p[3], CultureInfo.InvariantCulture), int.Parse(p[4], CultureInfo.InvariantCulture),
                int.Parse(p[5], CultureInfo.InvariantCulture), int.Parse(p[6], CultureInfo.InvariantCulture), double.Parse(p[7], CultureInfo.InvariantCulture),
                p[8] == "1", p[9].Length == 0 ? null : p[9], Dec(p[10]), f));
        }
        return rows;
    }

    public static (List<DatasetRow> Rows, JsonObject Manifest) Read(string dir)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ManifestFile)))!.AsObject();
        if (manifest["featureSchema"]?.GetValue<string>() != MlFeatures.Schema)
            throw new InvalidOperationException($"Схема признаков набора {manifest["featureSchema"]} ≠ {MlFeatures.Schema}: пересоберите набор");
        var rows = new List<DatasetRow>();
        using var r = new StreamReader(Path.Combine(dir, CsvFile), Encoding.UTF8);
        var header = r.ReadLine()!.Split(',');
        if (!header.Skip(Keys.Length).SequenceEqual(MlFeatures.Names)) throw new InvalidOperationException("Колонки набора не совпадают со схемой признаков");
        while (r.ReadLine() is { } line)
        {
            var p = line.Split(',');
            var f = new float[MlFeatures.Names.Count];
            for (var i = 0; i < f.Length; i++) f[i] = float.Parse(p[Keys.Length + i], CultureInfo.InvariantCulture);
            rows.Add(new DatasetRow(long.Parse(p[0], CultureInfo.InvariantCulture), long.Parse(p[1], CultureInfo.InvariantCulture), DateTimeOffset.Parse(p[2], CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(p[3], CultureInfo.InvariantCulture), int.Parse(p[4], CultureInfo.InvariantCulture), p[5], int.Parse(p[6], CultureInfo.InvariantCulture),
                Dec(p[7]), Dec(p[8]), f));
        }
        return (rows, manifest);
    }

    private static decimal? Dec(string s) => s.Length == 0 ? null : decimal.Parse(s, CultureInfo.InvariantCulture);

    public static string Iso(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }
}
