namespace Aeza.Risk.Core;

/// <summary>Качество ранжирования на отложенной выборке (офлайн-отчёт и проверка теневой модели на реальных исходах)</summary>
/// <param name="Rows">Решений</param>
/// <param name="Positives">Из них «не продлено»</param>
/// <param name="PrAuc">Площадь под кривой точность–полнота (average precision); случайный ранжир ≈ доля положительных</param>
/// <param name="RocAuc">ROC-AUC</param>
/// <param name="K">Ёмкость: сколько решений команда возврата обработает (top-K)</param>
/// <param name="PrecisionAtK">Доля «не продлено» среди top-K</param>
/// <param name="RecallAtK">Доля всех «не продлено», попавших в top-K</param>
/// <param name="Brier">Brier score (только для вероятностей модели)</param>
public sealed record RankMetrics(int Rows, int Positives, double PrAuc, double RocAuc, int K, double PrecisionAtK, double RecallAtK, double? Brier);

/// <summary>Метрики ранжирования «не продлено»: одни и те же в офлайн-отчёте (tools/Aeza.Risk.Ml) и в анализаторе</summary>
public static class RankingMetrics
{
    /// <summary>Доля решений, которую команда возврата обработает (top-K)</summary>
    public const double CapacityShare = 0.05;

    public static RankMetrics Evaluate(IReadOnlyList<int> labels, IReadOnlyList<double> scores, bool probabilities)
    {
        var n = labels.Count;
        var pos = labels.Count(l => l == 1);
        var k = Math.Max(1, (int)Math.Ceiling(n * CapacityShare));
        // Равные значения — по порядку строк (стабильно): ранжир не зависит от случайности
        var order = Enumerable.Range(0, n).OrderByDescending(i => scores[i]).ThenBy(i => i).ToList();
        var top = order.Take(k).Count(i => labels[i] == 1);
        return new RankMetrics(n, pos, AveragePrecision(labels, order), RocAuc(labels, scores), k, (double)top / k, pos == 0 ? 0 : (double)top / pos,
            probabilities ? labels.Select((l, i) => Math.Pow(scores[i] - l, 2)).Average() : null);
    }

    public static double AveragePrecision(IReadOnlyList<int> labels, IReadOnlyList<int> order)
    {
        var pos = labels.Count(l => l == 1);
        if (pos == 0) return 0;
        double sum = 0;
        var hits = 0;
        for (var r = 0; r < order.Count; r++)
        {
            if (labels[order[r]] != 1) continue;
            hits++;
            sum += (double)hits / (r + 1);
        }
        return sum / pos;
    }

    /// <summary>ROC-AUC через ранги (Манн — Уитни), равные значения — средний ранг</summary>
    public static double RocAuc(IReadOnlyList<int> labels, IReadOnlyList<double> scores)
    {
        var pos = labels.Count(l => l == 1);
        var neg = labels.Count - pos;
        if (pos == 0 || neg == 0) return 0.5;
        var idx = Enumerable.Range(0, labels.Count).OrderBy(i => scores[i]).ToList();
        var ranks = new double[labels.Count];
        for (var i = 0; i < idx.Count;)
        {
            var j = i;
            while (j + 1 < idx.Count && scores[idx[j + 1]] == scores[idx[i]]) j++;
            var rank = (i + j) / 2.0 + 1;
            for (var m = i; m <= j; m++) ranks[idx[m]] = rank;
            i = j + 1;
        }
        var sumPos = labels.Select((l, i) => l == 1 ? ranks[i] : 0).Sum();
        return (sumPos - pos * (pos + 1) / 2.0) / ((double)pos * neg);
    }

    /// <summary>Калибровка: 10 корзин по вероятности — средняя вероятность против наблюдённой доли</summary>
    public static IReadOnlyList<(double From, double To, int Rows, double Predicted, double Observed)> Calibration(IReadOnlyList<int> labels, IReadOnlyList<double> p)
    {
        var bins = new List<(double, double, int, double, double)>();
        for (var b = 0; b < 10; b++)
        {
            double lo = b / 10.0, hi = (b + 1) / 10.0;
            var idx = Enumerable.Range(0, p.Count).Where(i => p[i] >= lo && (p[i] < hi || b == 9 && p[i] <= 1)).ToList();
            if (idx.Count == 0) continue;
            bins.Add((lo, hi, idx.Count, idx.Average(i => p[i]), idx.Average(i => (double)labels[i])));
        }
        return bins;
    }

    /// <summary>Bootstrap по клиентам (кластерный): 95% интервал метрики и разницы с базовой линией</summary>
    public static (double Lo, double Hi, double DiffLo, double DiffHi) Bootstrap(
        IReadOnlyList<long> accounts, IReadOnlyList<int> labels, IReadOnlyList<double> model, IReadOnlyList<double> baseline,
        Func<RankMetrics, double> metric, int resamples, int seed)
    {
        var groups = Enumerable.Range(0, accounts.Count).GroupBy(i => accounts[i]).Select(g => g.ToArray()).ToArray();
        var rnd = new Random(seed);
        var values = new List<double>(resamples);
        var diffs = new List<double>(resamples);
        for (var s = 0; s < resamples; s++)
        {
            var pick = new List<int>(labels.Count);
            for (var g = 0; g < groups.Length; g++) pick.AddRange(groups[rnd.Next(groups.Length)]);
            var l = pick.Select(i => labels[i]).ToList();
            if (l.All(x => x == 0)) continue;
            var m = metric(Evaluate(l, pick.Select(i => model[i]).ToList(), false));
            var b = metric(Evaluate(l, pick.Select(i => baseline[i]).ToList(), false));
            values.Add(m);
            diffs.Add(m - b);
        }
        values.Sort();
        diffs.Sort();
        static double Q(List<double> xs, double q) => xs.Count == 0 ? double.NaN : xs[Math.Clamp((int)Math.Floor(q * (xs.Count - 1)), 0, xs.Count - 1)];
        return (Q(values, 0.025), Q(values, 0.975), Q(diffs, 0.025), Q(diffs, 0.975));
    }
}
