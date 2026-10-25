namespace Aeza.Risk.Core;

/// <summary>Чем считается основной балл</summary>
public static class ScoringModes
{
    /// <summary>Правила: Σ вес × сила признака</summary>
    public const string Rules = "rules";

    /// <summary>Только ML: балл услуги = вероятность «не продлено» × 100</summary>
    public const string Ml = "ml";

    public static bool IsValid(string? mode) => mode is Rules or Ml;
}

/// <summary>Прогноз модели для услуги</summary>
/// <param name="Probability">Вероятность «не продлено»</param>
/// <param name="Drivers">Вклады факторов</param>
public sealed record MlServiceScore(double Probability, IReadOnlyList<MlDriver> Drivers);

/// <summary>Основной балл по ML: прогнозы для услуг в окне модели</summary>
/// <param name="Model">Модель</param>
/// <param name="Services">Услуга → прогноз; услуги вне окна модели отсутствуют и получают «нет оценки»</param>
public sealed record MlPrimary(MlModelRef Model, IReadOnlyDictionary<long, MlServiceScore> Services)
{
    /// <summary>Причина, когда ни один фактор не повышает риск сильнее, чем у типичного клиента</summary>
    public const string FeatureKey = "ml_probability";

    /// <summary>Префикс ключей факторов модели в причинах</summary>
    public const string DriverPrefix = "ml.";

    /// <summary>Чего не хватает услуге вне окна модели</summary>
    public const string OutOfScopeInput = "ml_window";
}

/// <summary>Чем посчитано поколение</summary>
/// <param name="Requested">Выбранный режим</param>
/// <param name="Used">Фактический режим</param>
/// <param name="ModelId">Модель, если балл посчитан ML</param>
/// <param name="ModelVersion">Версия модели</param>
/// <param name="Fallback">Почему выбран ML, а посчитано правилами: no_model — модель не включена, ml_failed — инференс не удался</param>
public sealed record ScoringInfo(string Requested, string Used, long? ModelId, string? ModelVersion, string? Fallback)
{
    public static readonly ScoringInfo Rules = new(ScoringModes.Rules, ScoringModes.Rules, null, null, null);
}
