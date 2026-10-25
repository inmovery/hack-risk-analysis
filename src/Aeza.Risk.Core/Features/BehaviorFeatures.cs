namespace Aeza.Risk.Core;

/// <summary>Признаки поведения клиента по дневной активности</summary>
public abstract class BehaviorFeature : IRiskFeature
{
    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    public decimal DefaultWeight => 0m;
    public bool Behavioral => true;

    public FeatureValue Evaluate(ServiceContext ctx)
    {
        if (ctx.Behavior is not { } b) return FeatureValue.Missing("Нет поведенческих данных: источник присылает только состояние записей", "behavior");
        return Evaluate(b, DateOnly.FromDateTime(ctx.AsOf.UtcDateTime), ctx);
    }

    protected abstract FeatureValue Evaluate(AccountBehavior b, DateOnly today, ServiceContext ctx);

    protected static decimal Clamp01(decimal v) => Math.Clamp(v, 0m, 1m);
}

/// <summary>Нагрузка за 7 дней упала относительно 30 дней до этого (главный опережающий сигнал)</summary>
public sealed class UsageDropFeature : BehaviorFeature
{
    public const int MinRecentSamples = 3;
    public const int MinBaseSamples = 7;

    public override string Key => "usage_drop";
    public override string Title => "Нагрузка на серверы упала";
    public override string Description => "средняя загрузка CPU за 7 дней против 30 дней до этого: падение 20% → 0, 70% и больше → 1";

    protected override FeatureValue Evaluate(AccountBehavior b, DateOnly today, ServiceContext ctx)
    {
        var recent = b.Sum(today.AddDays(-6), today);
        var basis = b.Sum(today.AddDays(-36), today.AddDays(-7));
        if (recent.UsageSamples < MinRecentSamples || basis.UsageSamples < MinBaseSamples)
            return FeatureValue.Missing($"Мало телеметрии: {recent.UsageSamples} услуго-дн. за 7 дней, {basis.UsageSamples} за 30 дней до этого", "usage.daily");
        var r = (decimal)(recent.CpuSum / recent.UsageSamples);
        var p = (decimal)(basis.CpuSum / basis.UsageSamples);
        var ev = new Dictionary<string, object?> { ["cpuRecentPct"] = Math.Round(r, 1), ["cpuBasePct"] = Math.Round(p, 1) };
        if (p < 1m) return FeatureValue.Of(0m, $"Нагрузка и так почти нулевая ({p:0.#}% CPU) — падение не оценивается", ev);
        var drop = 1m - r / p;
        ev["dropPct"] = Math.Round(drop * 100, 0);
        return FeatureValue.Of(Clamp01((drop - 0.2m) / 0.5m),
            drop > 0 ? $"Нагрузка упала на {drop:P0} за 7 дней: {r:0.#}% CPU против {p:0.#}%" : $"Нагрузка не упала: {r:0.#}% CPU против {p:0.#}%", ev);
    }
}

/// <summary>Давно не входил в панель: 7 дней и меньше → 0, 30 и больше → 1</summary>
public sealed class InactiveLoginsFeature : BehaviorFeature
{
    public override string Key => "inactive_logins";
    public override string Title => "Давно не входил в панель";
    public override string Description => "дней с последнего входа: до 7 → 0, 30 и больше → 1 (без входов — с первого дня данных)";

    protected override FeatureValue Evaluate(AccountBehavior b, DateOnly today, ServiceContext ctx)
    {
        var since = b.LastLoginDay ?? b.KnownSince;
        var days = Math.Max(0, today.DayNumber - since.DayNumber);
        var ev = new Dictionary<string, object?> { ["daysSinceLogin"] = days, ["lastLoginDay"] = b.LastLoginDay?.ToString("yyyy-MM-dd"), ["knownSince"] = b.KnownSince.ToString("yyyy-MM-dd") };
        var text = b.LastLoginDay is null ? $"Входов не было за все {days} дн. данных" : days == 0 ? "Входил сегодня" : $"Последний вход {days} дн. назад";
        return FeatureValue.Of(Clamp01((days - 7) / 23m), text, ev);
    }
}

/// <summary>Неудачные списания за 30 дней: 1 → 0,5, 2 и больше → 1</summary>
public sealed class PaymentFailuresFeature : BehaviorFeature
{
    public override string Key => "payment_failures";
    public override string Title => "Неудачные списания";
    public override string Description => "неудачных списаний за 30 дней: 1 → 0,5, 2 и больше → 1";

    protected override FeatureValue Evaluate(AccountBehavior b, DateOnly today, ServiceContext ctx)
    {
        var n = b.Sum(today.AddDays(-29), today).PaymentsFailed;
        return FeatureValue.Of(Clamp01(n / 2m), n == 0 ? "Неудачных списаний за 30 дней нет" : $"Неудачных списаний за 30 дней: {n}",
            new Dictionary<string, object?> { ["failed30d"] = n });
    }
}

/// <summary>Проблемы с поддержкой за 30 дней: нерешённое обращение → 1, оценка 1–2 → 0,7, NPS 0–6 → 0,5</summary>
public sealed class SupportIssuesFeature : BehaviorFeature
{
    public override string Key => "support_issues";
    public override string Title => "Проблемы с поддержкой";
    public override string Description => "за 30 дней: нерешённое обращение → 1, оценка обращения 1–2 → 0,7, NPS 0–6 → 0,5";

    protected override FeatureValue Evaluate(AccountBehavior b, DateOnly today, ServiceContext ctx)
    {
        var w = b.Sum(today.AddDays(-29), today);
        var ev = new Dictionary<string, object?> { ["ticketsOpened"] = w.TicketsOpened, ["ticketsUnsolved"] = w.TicketsUnsolved, ["csatMin"] = w.CsatMin, ["npsMin"] = w.NpsMin };
        if (w.TicketsUnsolved > 0) return FeatureValue.Of(1m, $"Обращений без решения за 30 дней: {w.TicketsUnsolved}", ev);
        if (w.CsatMin is <= 2) return FeatureValue.Of(0.7m, $"Низкая оценка обращения: {w.CsatMin} из 5", ev);
        if (w.NpsMin is <= 6) return FeatureValue.Of(0.5m, $"NPS {w.NpsMin} из 10 (критик)", ev);
        return FeatureValue.Of(0m, w.TicketsOpened == 0 ? "Обращений за 30 дней нет" : $"Обращений за 30 дней: {w.TicketsOpened}, все решены", ev);
    }
}

/// <summary>Аварии на серверах клиента за 30 дней: 4 часа простоя и больше → 1</summary>
public sealed class IncidentsFeature : BehaviorFeature
{
    public override string Key => "incidents";
    public override string Title => "Аварии на серверах клиента";
    public override string Description => "минуты аварий на услугах клиента за 30 дней: 0 → 0, 240 и больше → 1";

    protected override FeatureValue Evaluate(AccountBehavior b, DateOnly today, ServiceContext ctx)
    {
        var minutes = b.Sum(today.AddDays(-29), today).IncidentMinutes;
        return FeatureValue.Of(Clamp01(minutes / 240m), minutes == 0 ? "Аварий за 30 дней не было" : $"Аварии за 30 дней: {minutes} мин простоя",
            new Dictionary<string, object?> { ["incidentMinutes30d"] = minutes });
    }
}

/// <summary>Понизил тариф за 60 дней</summary>
public sealed class DowngradeFeature : BehaviorFeature
{
    public override string Key => "downgrade";
    public override string Title => "Понизил тариф";
    public override string Description => "1, если за 60 дней клиент перешёл на тариф дешевле";

    protected override FeatureValue Evaluate(AccountBehavior b, DateOnly today, ServiceContext ctx)
    {
        var n = b.Sum(today.AddDays(-59), today).Downgrades;
        return FeatureValue.Of(n > 0 ? 1m : 0m, n > 0 ? $"Понижений тарифа за 60 дней: {n}" : "Тариф за 60 дней не понижал",
            new Dictionary<string, object?> { ["downgrades60d"] = n });
    }
}
