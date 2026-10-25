namespace Aeza.Risk.Core;

/// <summary>Вектор признаков ML на момент решения t</summary>
public static class MlFeatures
{
    /// <summary>Версия схемы признаков</summary>
    public const string Schema = "aeza-ml-features-v1";

    /// <summary>Имена признаков в порядке вектора</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        // Услуга и её состояние
        "term_week", "term_month", "term_quarter", "term_half_year", "term_year",
        "price_month_eur", "auto_prolong", "status_suspended", "status_prolong_wait", "days_to_expiry", "service_age_days_log",
        // Деньги клиента
        "funds_cover", "balance_eur_log", "bonus_unlocked", "withdraw_eur_log",
        // Клиент
        "live_services", "tenure_days_log",
        // Поведение по событиям v2 (дни до момента решения включительно)
        .. BehaviorNames,
    ];

    /// <summary>Признаки поведения — одинаковые в схемах услуги и клиента</summary>
    public static IReadOnlyList<string> BehaviorNames =>
    [
        "has_behavior", "logins_7", "logins_30", "days_since_login",
        "cpu_7", "cpu_base", "cpu_ratio", "usage_days_7",
        "failed_payments_30", "payments_30_eur_log", "topups_30", "topups_90_eur_log",
        "tickets_30", "unsolved_30", "csat_min_30", "nps_min_90", "incident_min_30_log",
        "downgrades_60", "upgrades_60", "autoprolong_off_30", "suspends_30", "backups_14", "withdrawals_30_eur_log",
        "bonus_expired_45", "deletions_90", "renewals_90",
    ];

    /// <summary>Признаки услуги <paramref name="s"/> клиента <paramref name="a"/> на момент <paramref name="t"/></summary>
    /// <param name="s">Услуга (состояние на t)</param>
    /// <param name="a">Аккаунт (состояние на t)</param>
    /// <param name="customerServices">Все услуги клиента на t (включая удалённые)</param>
    /// <param name="b">Поведение на t; null — событий v2 нет</param>
    /// <param name="t">Момент решения</param>
    public static float[] Compute(ServiceInput s, AccountInput a, IReadOnlyCollection<ServiceInput> customerServices, AccountBehavior? b, DateTimeOffset t)
    {
        var v = new float[Names.Count];
        var i = 0;
        var term = SourceEnums.ParseTerm(s.PaymentTerm);
        v[i++] = term == PaymentTerm.Week ? 1 : 0;
        v[i++] = term == PaymentTerm.Month ? 1 : 0;
        v[i++] = term == PaymentTerm.QuarterYear ? 1 : 0;
        v[i++] = term == PaymentTerm.HalfYear ? 1 : 0;
        v[i++] = term == PaymentTerm.Year ? 1 : 0;
        var price = Math.Max(0, s.Price ?? 0);
        v[i++] = (float)(MonthlyEquivalent(price, term) / 100.0);
        v[i++] = s.AutoProlong == true ? 1 : 0;
        var status = SourceEnums.ParseStatus(s.Status);
        v[i++] = status == ServiceStatus.Suspended ? 1 : 0;
        v[i++] = status == ServiceStatus.ProlongWait ? 1 : 0;
        v[i++] = s.ExpiresAt is { } e ? (float)Math.Clamp((e - t).TotalDays, -10, 400) : 0;
        v[i++] = s.CreatedAt is { } c ? Log1P((t - c).TotalDays) : 0;

        var funds = Math.Max(a.Balance ?? 0, 0) + (SourceEnums.ParseBonusState(a.BonusState) == BonusState.Unlocked ? Math.Max(a.BonusBalance ?? 0, 0) : 0);
        v[i++] = price > 0 ? (float)Math.Clamp((double)funds / price, 0, 10) : 10;
        v[i++] = Log1P(Math.Max(a.Balance ?? 0, 0) / 100.0);
        v[i++] = SourceEnums.ParseBonusState(a.BonusState) == BonusState.Unlocked ? 1 : 0;
        v[i++] = Log1P(Math.Max(a.WithdrawBalance ?? 0, 0) / 100.0);

        v[i++] = customerServices.Count(x => SourceEnums.ParseStatus(x.Status) != ServiceStatus.Deleted);
        var first = customerServices.Select(x => x.CreatedAt).OfType<DateTimeOffset>().DefaultIfEmpty(t).Min();
        v[i++] = Log1P((t - first).TotalDays);

        i = Behavior(b, t, v, i);
        if (i != Names.Count) throw new InvalidOperationException($"Вектор признаков: {i} значений при {Names.Count} именах");
        return v;
    }

    /// <summary>Признаки поведения в порядке <see cref="BehaviorNames"/> с позиции <paramref name="i"/>; возвращает позицию после них</summary>
    internal static int Behavior(AccountBehavior? b, DateTimeOffset t, float[] v, int i)
    {
        var start = i;
        var today = DateOnly.FromDateTime(t.UtcDateTime);
        if (b is null)
        {
            // Нет поведенческих данных: признаки поведения — нейтральные значения, а has_behavior = 0 говорит модели, что это «нет данных»
            v[start + 6] = 1; // cpu_ratio
            v[start + 14] = 5; // csat_min_30
            v[start + 15] = 10; // nps_min_90
            return start + 26;
        }
        ActivityDelta W(int days, int endOffset = 0) => b.Sum(today.AddDays(-(days - 1 + endOffset)), today.AddDays(-endOffset));
        var w7 = W(7);
        var w30 = W(30);
        var w14 = W(14);
        var w45 = W(45);
        var w60 = W(60);
        var w90 = W(90);
        var basis = W(30, 7);
        v[i++] = 1;
        v[i++] = w7.Logins;
        v[i++] = w30.Logins;
        var since = b.LastLoginDay ?? b.KnownSince;
        v[i++] = Math.Clamp(today.DayNumber - since.DayNumber, 0, 90);
        double? Cpu(ActivityDelta w) => w.UsageSamples > 0 ? w.CpuSum / w.UsageSamples : null;
        var cpu7 = Cpu(w7);
        var cpuBase = Cpu(basis);
        v[i++] = (float)(cpu7 ?? 0);
        v[i++] = (float)(cpuBase ?? 0);
        v[i++] = cpu7 is { } r && cpuBase is >= 1 ? (float)Math.Clamp(r / cpuBase.Value, 0, 3) : 1;
        v[i++] = w7.UsageSamples;
        v[i++] = w30.PaymentsFailed;
        v[i++] = Log1P(w30.PaymentsMinor / 100.0);
        v[i++] = w30.TopUpsCount;
        v[i++] = Log1P(w90.TopUpsMinor / 100.0);
        v[i++] = w30.TicketsOpened;
        v[i++] = w30.TicketsUnsolved;
        v[i++] = w30.CsatMin ?? 5;
        v[i++] = w90.NpsMin ?? 10;
        v[i++] = Log1P(w30.IncidentMinutes);
        v[i++] = w60.Downgrades;
        v[i++] = w60.Upgrades;
        v[i++] = w30.AutoProlongOff;
        v[i++] = w30.Suspends;
        v[i++] = w14.Backups;
        v[i++] = Log1P(w30.WithdrawalsMinor / 100.0);
        v[i++] = w45.BonusExpired;
        v[i++] = w90.Deletions;
        v[i++] = w90.Renewals;
        return i;
    }

    /// <summary>Позиция признака по имени</summary>
    public static readonly IReadOnlyDictionary<string, int> Index = Names.Select((n, k) => (n, k)).ToDictionary(x => x.n, x => x.k);

    /// <summary>Цена периода, приведённая к месяцу (еврокопейки)</summary>
    public static double MonthlyEquivalent(long price, PaymentTerm term) => term switch
    {
        PaymentTerm.Hour => price * 720.0,
        PaymentTerm.Week => price * 30.0 / 7,
        PaymentTerm.QuarterYear => price / 3.0,
        PaymentTerm.HalfYear => price / 6.0,
        PaymentTerm.Year => price / 12.0,
        _ => price,
    };

    private static float Log1P(double x) => (float)Math.Log(1 + Math.Max(0, x));
}

/// <summary>Точки решения модели непродления: за k дней до срока E</summary>
public static class MlDecision
{
    /// <summary>Максимум дней до срока, при котором модель применяется</summary>
    public const int MaxDaysAhead = 14;

    public static IReadOnlyList<int> OffsetsDays(PaymentTerm term) => term switch
    {
        PaymentTerm.Month or PaymentTerm.QuarterYear or PaymentTerm.HalfYear or PaymentTerm.Year => [14, 7],
        PaymentTerm.Week => [3],
        _ => [],
    };

    /// <summary>Услуга в области применения модели на момент <paramref name="t"/></summary>
    public static bool InScope(ServiceInput s, DateTimeOffset t)
    {
        var status = SourceEnums.ParseStatus(s.Status);
        if (status is not (ServiceStatus.Active or ServiceStatus.Suspended) || s.ExpiresAt is not { } e) return false;
        var offsets = OffsetsDays(SourceEnums.ParseTerm(s.PaymentTerm));
        if (offsets.Count == 0) return false;
        var days = (e - t).TotalDays;
        return days > 0 && days <= offsets.Max();
    }
}
