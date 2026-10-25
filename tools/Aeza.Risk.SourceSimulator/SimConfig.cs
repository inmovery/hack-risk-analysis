using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aeza.Risk.SourceSimulator;

/// <summary>Параметры модели клиентов</summary>
public sealed class SimConfig
{
    /// <summary>Клиентов в истории (уже существующих на T₀ и пришедших за период)</summary>
    public int Customers { get; set; } = 2000;

    /// <summary>Дней истории</summary>
    public int Days { get; set; } = 365;

    /// <summary>Доля клиентов, которые уже есть на начало истории (остальные регистрируются за период)</summary>
    public double ExistingShare { get; set; } = 0.6;

    /// <summary>Новых клиентов в день в режиме live</summary>
    public double LiveRegistrationsPerDay { get; set; } = 1.0;

    /// <summary>Дней ожидания оплаты до удаления</summary>
    public int GraceDays { get; set; } = 5;

    public List<SegmentConfig> Segments { get; set; } = DefaultSegments();

    /// <summary>Тарифы: базовая цена месяца в еврокопейках</summary>
    public List<PlanConfig> Plans { get; set; } =
    [
        new() { Name = "vps-xs", Cpu = 1, RamGb = 1, DiskGb = 15, MonthPrice = 349 },
        new() { Name = "vps-s", Cpu = 1, RamGb = 2, DiskGb = 30, MonthPrice = 549 },
        new() { Name = "vps-m", Cpu = 2, RamGb = 4, DiskGb = 60, MonthPrice = 960 },
        new() { Name = "vps-l", Cpu = 4, RamGb = 8, DiskGb = 120, MonthPrice = 1790 },
        new() { Name = "vps-xl", Cpu = 8, RamGb = 16, DiskGb = 240, MonthPrice = 3490 },
    ];

    public List<string> Locations { get; set; } = ["de-fra", "nl-ams", "fi-hel", "us-nyc"];

    /// <summary>Аварий в ДЦ в день на локацию (пуассоновский поток)</summary>
    public double IncidentsPerLocationPerDay { get; set; } = 0.04;

    /// <summary>Изменения цен: день от начала истории, тариф, процент</summary>
    public List<PriceChangeConfig> PriceChanges { get; set; } =
    [
        new() { Day = 150, Plan = "vps-s", Percent = 20 },
        new() { Day = 270, Plan = "vps-m", Percent = 15 },
    ];

    public HazardConfig Hazard { get; set; } = new();

    public NoiseConfig Noise { get; set; } = new();

    private static List<SegmentConfig> DefaultSegments() =>
    [
        new()
        {
            Name = SegmentNames.Stable, DecayPerDay = 1.0 / 3000, Share = 0.20, Satisfaction = 0.85, Need = 0.85, Finance = 0.9, PriceSensitivity = 0.2,
            Terms = new() { ["month"] = 0.3, ["quarter_year"] = 0.2, ["half_year"] = 0.2, ["year"] = 0.3 },
            Plans = new() { ["vps-m"] = 0.3, ["vps-l"] = 0.4, ["vps-xl"] = 0.3 },
            AutoProlong = 0.95, LoginsPerDay = 1.2, CpuBase = 45, ServicesMean = 2.0, PromoShare = 0.05, TwoFactor = 0.8, Business = 0.85,
            Habits = new() { ["auto"] = 0.6, ["manual"] = 0.35, ["last_moment"] = 0.05 }, NewServicePerDay = 1.0 / 150,
        },
        new()
        {
            Name = SegmentNames.Growing, DecayPerDay = 1.0 / 1500, Share = 0.15, Satisfaction = 0.8, Need = 0.6, Finance = 0.75, PriceSensitivity = 0.4, NeedTrend = 0.002,
            Terms = new() { ["month"] = 0.8, ["quarter_year"] = 0.2 },
            Plans = new() { ["vps-s"] = 0.4, ["vps-m"] = 0.4, ["vps-l"] = 0.2 },
            AutoProlong = 0.8, LoginsPerDay = 1.5, CpuBase = 35, ServicesMean = 1.3, PromoShare = 0.15, TwoFactor = 0.4, Business = 0.5,
            Habits = new() { ["auto"] = 0.3, ["manual"] = 0.6, ["last_moment"] = 0.1 }, NewServicePerDay = 1.0 / 45, UpgradePerDay = 1.0 / 90,
        },
        new()
        {
            Name = SegmentNames.PriceSensitive, DecayPerDay = 1.0 / 900, Share = 0.25, Satisfaction = 0.7, Need = 0.7, Finance = 0.5, PriceSensitivity = 0.9,
            Terms = new() { ["month"] = 0.9, ["week"] = 0.1 },
            Plans = new() { ["vps-xs"] = 0.5, ["vps-s"] = 0.5 },
            AutoProlong = 0.6, LoginsPerDay = 0.6, CpuBase = 25, ServicesMean = 1.1, PromoShare = 0.7, TwoFactor = 0.1, Business = 0.1,
            Habits = new() { ["auto"] = 0.1, ["manual"] = 0.4, ["last_moment"] = 0.5 }, NewServicePerDay = 1.0 / 365,
        },
        new()
        {
            Name = SegmentNames.Developer, DecayPerDay = 1.0 / 700, Share = 0.20, Satisfaction = 0.75, Need = 0.6, Finance = 0.65, PriceSensitivity = 0.5,
            Terms = new() { ["hour"] = 0.45, ["week"] = 0.35, ["month"] = 0.2 },
            Plans = new() { ["vps-xs"] = 0.4, ["vps-s"] = 0.4, ["vps-m"] = 0.2 },
            AutoProlong = 0.5, LoginsPerDay = 1.0, CpuBase = 20, ServicesMean = 1.5, PromoShare = 0.3, TwoFactor = 0.3, Business = 0.1,
            Habits = new() { ["auto"] = 0.2, ["manual"] = 0.6, ["last_moment"] = 0.2 }, NewServicePerDay = 1.0 / 10, ShortLivedDeletePerDay = 1.0 / 5,
        },
        new()
        {
            Name = SegmentNames.Forgotten, Share = 0.15, Satisfaction = 0.7, Need = 0.03, Finance = 0.6, PriceSensitivity = 0.3, Attentive = false,
            Terms = new() { ["month"] = 1.0 },
            Plans = new() { ["vps-xs"] = 0.6, ["vps-s"] = 0.4 },
            AutoProlong = 0.95, LoginsPerDay = 0.01, CpuBase = 1.5, ServicesMean = 1.0, PromoShare = 0.2, TwoFactor = 0.05, Business = 0.1,
            Habits = new() { ["auto"] = 0.05, ["manual"] = 0.15, ["last_moment"] = 0.8 }, TopUpAttention = 0.03,
        },
        new()
        {
            Name = SegmentNames.Abuser, Share = 0.05, Satisfaction = 0.6, Need = 0.7, Finance = 0.5, PriceSensitivity = 0.5, Attentive = false, Abuser = true,
            Terms = new() { ["hour"] = 0.4, ["week"] = 0.3, ["month"] = 0.3 },
            Plans = new() { ["vps-s"] = 0.5, ["vps-m"] = 0.5 },
            AutoProlong = 0.5, LoginsPerDay = 0.8, CpuBase = 85, ServicesMean = 1.2, PromoShare = 0.5, TwoFactor = 0.0, Business = 0.0,
            Habits = new() { ["manual"] = 1.0 }, NewServicePerDay = 1.0 / 30,
        },
    ];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Параметры по умолчанию, поверх — файл (если задан)</summary>
    public static SimConfig Load(string? path)
    {
        if (string.IsNullOrEmpty(path)) return new SimConfig();
        var config = JsonSerializer.Deserialize<SimConfig>(File.ReadAllText(path), Json) ?? new SimConfig();
        config.Validate();
        return config;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public void Validate()
    {
        if (Customers < 1) throw new InvalidOperationException("customers ≥ 1");
        if (Days < 1) throw new InvalidOperationException("days ≥ 1");
        if (Segments.Count == 0 || Segments.Sum(s => s.Share) <= 0) throw new InvalidOperationException("Нужен хотя бы один сегмент с долей > 0");
        foreach (var s in Segments)
            foreach (var plan in s.Plans.Keys)
                if (Plans.All(p => p.Name != plan)) throw new InvalidOperationException($"Сегмент {s.Name}: тариф {plan} не описан в plans");
    }
}

/// <summary>Сегмент клиентов: профиль, привычки и базовый уровень скрытого состояния</summary>
public sealed class SegmentConfig
{
    public string Name { get; set; } = "";
    public double Share { get; set; }
    /// <summary>Базовая удовлетворённость s₀</summary>
    public double Satisfaction { get; set; }
    /// <summary>Базовая потребность в серверах e₀</summary>
    public double Need { get; set; }
    /// <summary>Базовое финансовое здоровье f₀</summary>
    public double Finance { get; set; }
    public double PriceSensitivity { get; set; }
    /// <summary>Рост потребности в день (растущий проект)</summary>
    public double NeedTrend { get; set; }
    public Dictionary<string, double> Terms { get; set; } = new();
    public Dictionary<string, double> Plans { get; set; } = new();
    public double AutoProlong { get; set; }
    public double LoginsPerDay { get; set; }
    /// <summary>Типичная загрузка CPU, %</summary>
    public double CpuBase { get; set; }
    public double ServicesMean { get; set; } = 1;
    /// <summary>Доля пришедших по промо</summary>
    public double PromoShare { get; set; }
    public double TwoFactor { get; set; }
    /// <summary>Доля юрлиц</summary>
    public double Business { get; set; }
    /// <summary>Привычка пополнять: auto / manual / last_moment</summary>
    public Dictionary<string, double> Habits { get; set; } = new();
    public double NewServicePerDay { get; set; }
    public double UpgradePerDay { get; set; }
    /// <summary>Вероятность в день, что проект начнёт затухать (потребность в серверах плавно падает)</summary>
    public double DecayPerDay { get; set; } = 1.0 / 1200;
    /// <summary>Удаление недолгих серверов (экспериментатор) в день</summary>
    public double ShortLivedDeletePerDay { get; set; }
    /// <summary>Следит ли клиент за своими услугами (у «забытого сервера» и нарушителя — нет: намерения уйти не возникает)</summary>
    public bool Attentive { get; set; } = true;
    public bool Abuser { get; set; }
    /// <summary>Вероятность в день вспомнить о пополнении, если денег не хватает (для невнимательных)</summary>
    public double TopUpAttention { get; set; } = 1;
}

public sealed class PlanConfig
{
    public string Name { get; set; } = "";
    public int Cpu { get; set; }
    public int RamGb { get; set; }
    public int DiskGb { get; set; }
    public long MonthPrice { get; set; }
}

public sealed class PriceChangeConfig
{
    public int Day { get; set; }
    public string Plan { get; set; } = "";
    public double Percent { get; set; }
}

/// <summary>Дневной риск возникновения намерения уйти: h = σ(β₀ + β₁(1−s) + β₂(1−e) + β₃(1−f) + β₄·промо_закончилось + β₅·рост_цены)</summary>
public sealed class HazardConfig
{
    public double B0 { get; set; } = -10.4;
    public double B1 { get; set; } = 4.5;
    public double B2 { get; set; } = 4.0;
    public double B3 { get; set; } = 3.5;
    public double B4 { get; set; } = 2.2;
    public double B5 { get; set; } = 2.0;
    /// <summary>Сколько дней после конца промо / роста цены действует фактор</summary>
    public int ShockDays { get; set; } = 45;
    public int MatureDaysMin { get; set; } = 7;
    public int MatureDaysMax { get; set; } = 30;
    /// <summary>Намерение гаснет, если удовлетворённость поднялась выше</summary>
    public double CancelSatisfaction { get; set; } = 0.7;
}

/// <summary>Шум и ложные сигналы</summary>
public sealed class NoiseConfig
{
    /// <summary>Доля уходов без предупреждения</summary>
    public double SilentChurnShare { get; set; } = 0.25;
    public double VacationsPerYear { get; set; } = 1.0;
    /// <summary>Доля дней без телеметрии</summary>
    public double TelemetryGap { get; set; } = 0.02;
    /// <summary>live: доля пакетов с перестановкой событий разных аккаунтов</summary>
    public double DeliveryShuffle { get; set; } = 0.1;
    /// <summary>live: доля пакетов, отправленных повторно (потерян ответ)</summary>
    public double DeliveryDuplicate { get; set; } = 0.02;
}

/// <summary>Имена сегментов</summary>
public static class SegmentNames
{
    public const string Stable = "stable_business";
    public const string Growing = "growing_project";
    public const string PriceSensitive = "price_sensitive";
    public const string Developer = "developer";
    public const string Forgotten = "forgotten_server";
    public const string Abuser = "abuser";
}

/// <summary>Скрытые причины ухода (truth.jsonl)</summary>
public static class Causes
{
    /// <summary>Недовольство после аварии или нерешённого обращения</summary>
    public const string Dissatisfaction = "dissatisfaction";
    /// <summary>Затухание проекта</summary>
    public const string Decay = "decay";
    public const string Finance = "finance";
    /// <summary>Конец промо или рост цены</summary>
    public const string Price = "price";
    /// <summary>Забытый сервер: деньги кончились, удалён по неоплате (намерения не было)</summary>
    public const string Neglect = "neglect";
    /// <summary>Блокировка за нарушение — не решение клиента</summary>
    public const string Abuse = "abuse";
    /// <summary>Экспериментатор удалил все серверы, но не ушёл</summary>
    public const string Experiment = "experiment";
    /// <summary>Не оплатил без намерения уйти (не хватило денег)</summary>
    public const string NonPayment = "non_payment";
}
