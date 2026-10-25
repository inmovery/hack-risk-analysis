namespace Aeza.Risk.Core;

/// <summary>Всё, что известно об одной продлеваемой услуге в момент оценки</summary>
public sealed record ServiceContext(
    ServiceInput Service,
    ServiceStatus Status,
    AccountInput Account,
    ServicePlan Plan,
    ServiceFunding? Funding,
    FundingResult AccountFunding,
    bool AccountFundingKnown,
    TransactionInput? LastTransaction,
    DateTimeOffset AsOf,
    TimeSpan UrgencyWindow,
    AccountBehavior? Behavior = null)
{
    /// <summary>1 — срок наступил или прошёл, 0 — дальше окна срочности; линейно между ними</summary>
    public decimal Urgency(DateTimeOffset due)
    {
        if (due <= AsOf) return 1m;
        var left = (decimal)(due - AsOf).Ticks / UrgencyWindow.Ticks;
        return Math.Clamp(1m - left, 0m, 1m);
    }
}

/// <summary>Результат признака: значение 0..1 или пропуск (с перечнем недостающих входов), evidence и текст причины</summary>
public sealed record FeatureValue(decimal? Value, IReadOnlyList<string> MissingInputs, IReadOnlyDictionary<string, object?> Evidence, string Text)
{
    public static FeatureValue Of(decimal value, string text, IReadOnlyDictionary<string, object?>? evidence = null) =>
        new(Math.Clamp(value, 0m, 1m), Array.Empty<string>(), evidence ?? new Dictionary<string, object?>(), text);

    public static FeatureValue Missing(string text, params string[] inputs) =>
        new(null, inputs, new Dictionary<string, object?>(), text);

    public bool IsMissing => Value is null;
}

/// <summary>Признак риска. Чтобы добавить новый: реализовать интерфейс, добавить экземпляр в <see cref="FeatureCatalog.All"/>, написать тест-вектор</summary>
public interface IRiskFeature
{
    /// <summary>Стабильный ключ (используется в настройках, API, причинах)</summary>
    string Key { get; }
    string Title { get; }
    /// <summary>Формула простыми словами — показывается в редакторе модели</summary>
    string Description { get; }
    decimal DefaultWeight { get; }
    /// <summary>Признак поведения: «нет данных» не снимает оценку услуги, а помечает клиента флагом качества</summary>
    bool Behavioral => false;
    FeatureValue Evaluate(ServiceContext ctx);
}
