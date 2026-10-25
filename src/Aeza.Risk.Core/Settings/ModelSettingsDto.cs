namespace Aeza.Risk.Core.Settings;

/// <summary>Сериализуемая форма настроек модели (API и jsonb ревизий)</summary>
/// <param name="SchemaVersion">Версия схемы настроек</param>
/// <param name="HorizonDays">Горизонт денег и прогноза, дни</param>
/// <param name="UrgencyWindowDays">Окно срочности, дни</param>
/// <param name="Thresholds">Пороги уровней риска</param>
/// <param name="Clock">Часы оценки</param>
/// <param name="Weights">Веса признаков (0..100)</param>
/// <param name="Churn">Определения типов оттока (схема 2; в ревизиях схемы 1 отсутствует — значения по умолчанию)</param>
public sealed record ModelSettingsDto(
    int SchemaVersion,
    int HorizonDays,
    int UrgencyWindowDays,
    ThresholdsDto Thresholds,
    ClockDto Clock,
    Dictionary<string, decimal> Weights,
    ChurnDto? Churn = null)
{
    /// <summary>Из доменной модели</summary>
    public static ModelSettingsDto From(ModelSettings s) => new(
        s.SchemaVersion, s.HorizonDays, s.UrgencyWindowDays,
        new ThresholdsDto(s.Thresholds.Medium, s.Thresholds.High),
        new ClockDto(s.Clock.Mode == ClockMode.Live ? "live" : "source", s.Clock.TickMinutes),
        s.Weights.ToDictionary(k => k.Key, k => k.Value),
        new ChurnDto(s.Churn.NonRenewalDays, s.Churn.PaymentWindowDays, s.Churn.PaymentDeclinePct));

    /// <summary>В доменную модель; ревизия схемы 1 поднимается до текущей схемы с определениями оттока по умолчанию</summary>
    public ModelSettings ToModel() => new(
        SchemaVersion == 1 && Churn is null ? ModelSettings.CurrentSchemaVersion : SchemaVersion, HorizonDays, UrgencyWindowDays,
        new Thresholds(Thresholds.Medium, Thresholds.High),
        new ClockSettings(Clock.Mode == "source" ? ClockMode.Source : ClockMode.Live, Clock.TickMinutes),
        Weights,
        Churn is null ? ChurnSettings.Default : new ChurnSettings(Churn.NonRenewalDays, Churn.PaymentWindowDays, Churn.PaymentDeclinePct));
}

/// <summary>Определения типов оттока</summary>
/// <param name="NonRenewalDays">N: «не продлено» через N дней после срока</param>
/// <param name="PaymentWindowDays">Окно сравнения платежей, дни</param>
/// <param name="PaymentDeclinePct">Порог снижения платежей, %</param>
public sealed record ChurnDto(int NonRenewalDays, int PaymentWindowDays, decimal PaymentDeclinePct);

/// <summary>Пороги уровней риска</summary>
/// <param name="Medium">Порог «Средний»</param>
/// <param name="High">Порог «Высокий»</param>
public sealed record ThresholdsDto(decimal Medium, decimal High);

/// <summary>Часы оценки</summary>
/// <param name="Mode">live — текущее время; source — момент выгрузки тестовых данных</param>
/// <param name="TickMinutes">Период пересчёта по времени, минуты</param>
public sealed record ClockDto(string Mode, int TickMinutes);
