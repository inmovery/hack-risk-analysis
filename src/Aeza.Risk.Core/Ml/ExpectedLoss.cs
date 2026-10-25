namespace Aeza.Risk.Core;

/// <summary>Ожидаемая потеря и её интервал</summary>
/// <param name="ExpectedMinor">Σ сумма × вероятность, еврокопейки</param>
/// <param name="LowMinor">Нижняя граница интервала</param>
/// <param name="HighMinor">Верхняя граница интервала</param>
/// <param name="Level">Уровень интервала (0,9 — 90 %)</param>
/// <param name="Items">Сколько сумм вошло</param>
public sealed record LossInterval(long ExpectedMinor, long LowMinor, long HighMinor, double Level, int Items);

/// <summary>Ожидаемая потеря портфеля: каждая сумма теряется с вероятностью модели независимо от остальных; интервал —
/// нормальное приближение E ± z·√Σ a²p(1−p), ограниченное [0, Σ a]. Независимость — допущение: общие причины (рост цен, авария)
/// делают реальный разброс шире, поэтому покрытие интервала проверяется на истории</summary>
public static class ExpectedLoss
{
    /// <summary>Уровень интервала</summary>
    public const double Level = 0.9;

    private const double Z = 1.6448536269514722;

    public static LossInterval Of(IEnumerable<(long AmountMinor, double Probability)> items)
    {
        double mean = 0, variance = 0;
        long total = 0;
        var n = 0;
        foreach (var (amount, probability) in items)
        {
            if (amount <= 0) continue;
            var p = Math.Clamp(probability, 0, 1);
            mean += amount * p;
            variance += (double)amount * amount * p * (1 - p);
            total = checked(total + amount);
            n++;
        }
        var sd = Math.Sqrt(variance);
        return new LossInterval(RiskEngine.RoundMinor((decimal)mean), (long)Math.Floor(Math.Max(0, mean - Z * sd)), (long)Math.Ceiling(Math.Min(total, mean + Z * sd)),
            Level, n);
    }
}
