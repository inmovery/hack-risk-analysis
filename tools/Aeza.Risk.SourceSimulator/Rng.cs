namespace Aeza.Risk.SourceSimulator;

/// <summary>Детерминированный генератор xoshiro256** (seed → одна и та же история)</summary>
public sealed class Rng
{
    private ulong _s0, _s1, _s2, _s3;

    /// <summary>Генератор из seed (SplitMix64 разворачивает seed в 256 бит)</summary>
    public Rng(ulong seed)
    {
        var x = seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    private Rng(ulong[] state) => (_s0, _s1, _s2, _s3) = (state[0], state[1], state[2], state[3]);

    /// <summary>Состояние для сохранения</summary>
    public ulong[] State => [_s0, _s1, _s2, _s3];

    /// <summary>Восстановить из сохранённого состояния</summary>
    public static Rng FromState(ulong[] state) => new(state);

    private static ulong SplitMix(ref ulong x)
    {
        var z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public ulong NextULong()
    {
        var result = ulong.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = ulong.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>[0, 1)</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>[0, max)</summary>
    public int Next(int maxExclusive) => maxExclusive <= 1 ? 0 : (int)(NextDouble() * maxExclusive);

    /// <summary>[min, max)</summary>
    public int Next(int min, int maxExclusive) => min + Next(maxExclusive - min);

    public bool Chance(double p) => p > 0 && NextDouble() < p;

    public double Uniform(double a, double b) => a + (b - a) * NextDouble();

    /// <summary>Нормальное (Бокс — Мюллер, без кеша второго значения: состояние — только 256 бит)</summary>
    public double Normal(double mean = 0, double sd = 1)
    {
        var u1 = 1.0 - NextDouble();
        var u2 = NextDouble();
        return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>Логнормальное с заданной медианой</summary>
    public double LogNormal(double median, double sigma) => median * Math.Exp(sigma * Normal());

    /// <summary>Пуассон: Кнут для малых λ, нормальное приближение для больших</summary>
    public int Poisson(double lambda)
    {
        if (lambda <= 0) return 0;
        if (lambda > 30) return Math.Max(0, (int)Math.Round(Normal(lambda, Math.Sqrt(lambda))));
        var l = Math.Exp(-lambda);
        var k = 0;
        var p = 1.0;
        do
        {
            k++;
            p *= NextDouble();
        }
        while (p > l);
        return k - 1;
    }

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    /// <summary>Индекс по весам</summary>
    public int Weighted(IReadOnlyList<double> weights)
    {
        var total = 0.0;
        foreach (var w in weights) total += Math.Max(0, w);
        if (total <= 0) return 0;
        var x = NextDouble() * total;
        for (var i = 0; i < weights.Count; i++)
        {
            x -= Math.Max(0, weights[i]);
            if (x < 0) return i;
        }
        return weights.Count - 1;
    }

    /// <summary>Ключ по весам словаря (ключи упорядочены: результат не зависит от порядка в файле конфигурации)</summary>
    public string Weighted(IReadOnlyDictionary<string, double> weights)
    {
        var keys = weights.Keys.Order(StringComparer.Ordinal).ToList();
        return keys[Weighted(keys.Select(k => weights[k]).ToList())];
    }
}
