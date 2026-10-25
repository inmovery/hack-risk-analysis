namespace Aeza.Risk.Core;

public enum ClockMode { Live, Source }

public sealed record Thresholds(decimal Medium, decimal High);

public sealed record ClockSettings(ClockMode Mode, int TickMinutes);

/// <summary>Определения трёх типов оттока: не меняют score, задают индикаторы и факты</summary>
/// <param name="NonRenewalDays">N: «не продлено», если срок не продлён в течение N дней после expiresAt</param>
/// <param name="PaymentWindowDays">W: окно сравнения выполненных платежей (последние W дней против предыдущих W)</param>
/// <param name="PaymentDeclinePct">Порог снижения платежей, % (снижение не меньше порога — отток по платежам)</param>
public sealed record ChurnSettings(int NonRenewalDays, int PaymentWindowDays, decimal PaymentDeclinePct)
{
    /// <summary>Значения по умолчанию: N = 5 (льготный период prolong_wait), W = 30, порог 30 %</summary>
    public static readonly ChurnSettings Default = new(5, 30, 30m);
}

/// <summary>Версионируемые настройки модели</summary>
public sealed record ModelSettings(
    int SchemaVersion,
    int HorizonDays,
    int UrgencyWindowDays,
    Thresholds Thresholds,
    ClockSettings Clock,
    IReadOnlyDictionary<string, decimal> Weights,
    ChurnSettings Churn)
{
    /// <summary>2 — добавлены определения оттока; ревизии схемы 1 читаются с <see cref="ChurnSettings.Default"/></summary>
    public const int CurrentSchemaVersion = 2;

    public static ModelSettings Default(IEnumerable<IRiskFeature> catalog) => new(
        CurrentSchemaVersion,
        HorizonDays: 30,
        UrgencyWindowDays: 7,
        new Thresholds(30m, 60m),
        new ClockSettings(ClockMode.Live, 5),
        catalog.ToDictionary(f => f.Key, f => f.DefaultWeight),
        ChurnSettings.Default);

    /// <summary>Вес признака; отсутствующий ключ (новый признак до явной ревизии) = 0</summary>
    public decimal WeightOf(string key) => Weights.TryGetValue(key, out var w) ? w : 0m;

    public RiskLevel LevelOf(decimal? score)
    {
        if (score is null) return RiskLevel.Unknown;
        if (score.Value >= Thresholds.High) return RiskLevel.High;
        if (score.Value >= Thresholds.Medium) return RiskLevel.Medium;
        return RiskLevel.Low;
    }
}

public static class ModelSettingsValidator
{
    public const int MaxHorizonDays = 90;
    public const int MaxUrgencyWindowDays = 30;
    public const int MaxNonRenewalDays = 90;
    public const int MinPaymentWindowDays = 7;
    public const int MaxPaymentWindowDays = 180;

    /// <summary>Возвращает ошибки по путям полей; пустой словарь — настройки допустимы</summary>
    public static IReadOnlyDictionary<string, string[]> Validate(ModelSettings s, IReadOnlyCollection<IRiskFeature> catalog)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string path, string msg)
        {
            if (!errors.TryGetValue(path, out var list)) errors[path] = list = new List<string>();
            list.Add(msg);
        }

        if (s.SchemaVersion != ModelSettings.CurrentSchemaVersion) Add("schemaVersion", $"Поддерживается только версия {ModelSettings.CurrentSchemaVersion}.");
        if (s.HorizonDays < 1 || s.HorizonDays > MaxHorizonDays) Add("horizonDays", $"Горизонт должен быть от 1 до {MaxHorizonDays} дней.");
        if (s.UrgencyWindowDays < 1 || s.UrgencyWindowDays > MaxUrgencyWindowDays) Add("urgencyWindowDays", $"Окно срочности должно быть от 1 до {MaxUrgencyWindowDays} дней.");
        else if (s.UrgencyWindowDays > s.HorizonDays) Add("urgencyWindowDays", "Окно срочности не может быть больше горизонта.");
        if (s.Thresholds.Medium <= 0) Add("thresholds.medium", "Порог «Средний» должен быть больше 0.");
        if (s.Thresholds.High > 100) Add("thresholds.high", "Порог «Высокий» не может быть больше 100.");
        if (s.Thresholds.Medium >= s.Thresholds.High) Add("thresholds", "Порог «Средний» должен быть меньше порога «Высокий».");
        if (s.Clock.TickMinutes < 1 || s.Clock.TickMinutes > 60) Add("clock.tickMinutes", "Период пересчёта по времени — от 1 до 60 минут.");
        if (s.Churn.NonRenewalDays < 1 || s.Churn.NonRenewalDays > MaxNonRenewalDays)
            Add("churn.nonRenewalDays", $"Срок «не продлено» — от 1 до {MaxNonRenewalDays} дней после expiresAt.");
        if (s.Churn.PaymentWindowDays < MinPaymentWindowDays || s.Churn.PaymentWindowDays > MaxPaymentWindowDays)
            Add("churn.paymentWindowDays", $"Окно платежей — от {MinPaymentWindowDays} до {MaxPaymentWindowDays} дней.");
        if (s.Churn.PaymentDeclinePct < 1 || s.Churn.PaymentDeclinePct > 100)
            Add("churn.paymentDeclinePct", "Порог снижения платежей — от 1 до 100 %.");
        else if (decimal.Round(s.Churn.PaymentDeclinePct, 1) != s.Churn.PaymentDeclinePct)
            Add("churn.paymentDeclinePct", "Не более одного знака после запятой.");

        var known = catalog.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, weight) in s.Weights)
        {
            if (!known.Contains(key)) { Add($"weights.{key}", "Неизвестный признак."); continue; }
            if (weight < 0 || weight > 100) Add($"weights.{key}", "Вес должен быть от 0 до 100.");
            else if (decimal.Round(weight, 2) != weight) Add($"weights.{key}", "Не более двух знаков после запятой.");
        }
        if (!s.Weights.Any(w => known.Contains(w.Key) && w.Value > 0)) Add("weights", "Хотя бы один признак должен иметь вес больше 0.");

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }
}
