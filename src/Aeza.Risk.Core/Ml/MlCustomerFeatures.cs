namespace Aeza.Risk.Core;

/// <summary>Вектор признаков клиента на момент t — для моделей ухода клиента целиком (M2) и сокращения выручки (M3)</summary>
public static class MlCustomerFeatures
{
    /// <summary>Версия схемы признаков клиента</summary>
    public const string Schema = "aeza-ml-customer-v1";

    /// <summary>Имена признаков в порядке вектора</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        // Услуги клиента: действующие — active, suspended, prolong_wait
        "live_services", "suspended_services", "prolong_wait_services", "autoprolong_off_share", "revenue_month_eur_log", "max_price_month_eur",
        "short_term_share", "min_days_to_expiry", "expiring_14", "tenure_days_log", "newest_service_age_log", "deleted_services_log",
        // Деньги клиента
        "funds_cover_month", "balance_eur_log", "bonus_unlocked", "withdraw_eur_log",
        // Поведение — как у модели услуги
        .. MlFeatures.BehaviorNames,
        // Сигналы переезда и удержания
        "backup_downloads_14", "dns_external_30", "retention_offers_90", "retention_holdouts_90",
    ];

    /// <summary>Позиция признака по имени</summary>
    public static readonly IReadOnlyDictionary<string, int> Index = Names.Select((n, k) => (n, k)).ToDictionary(x => x.n, x => x.k);

    /// <summary>Признаки клиента <paramref name="a"/> на момент <paramref name="t"/></summary>
    /// <param name="a">Аккаунт (состояние на t)</param>
    /// <param name="services">Все услуги клиента на t (включая удалённые)</param>
    /// <param name="b">Поведение на t; null — событий v2 нет</param>
    /// <param name="t">Момент среза</param>
    public static float[] Compute(AccountInput a, IReadOnlyCollection<ServiceInput> services, AccountBehavior? b, DateTimeOffset t)
    {
        var v = new float[Names.Count];
        var i = 0;
        var statuses = services.Select(s => (Service: s, Status: SourceEnums.ParseStatus(s.Status))).ToList();
        var live = statuses.Where(x => x.Status is ServiceStatus.Active or ServiceStatus.Suspended or ServiceStatus.ProlongWait).Select(x => x.Service).ToList();
        double Monthly(ServiceInput s) => MlFeatures.MonthlyEquivalent(Math.Max(0, s.Price ?? 0), SourceEnums.ParseTerm(s.PaymentTerm));
        var revenue = live.Sum(Monthly);

        v[i++] = live.Count;
        v[i++] = statuses.Count(x => x.Status == ServiceStatus.Suspended);
        v[i++] = statuses.Count(x => x.Status == ServiceStatus.ProlongWait);
        v[i++] = live.Count == 0 ? 0 : (float)live.Count(s => s.AutoProlong != true) / live.Count;
        v[i++] = Log1P(revenue / 100.0);
        v[i++] = live.Count == 0 ? 0 : (float)(live.Max(Monthly) / 100.0);
        v[i++] = live.Count == 0 ? 0 : (float)live.Count(s => SourceEnums.ParseTerm(s.PaymentTerm) is PaymentTerm.Hour or PaymentTerm.Week) / live.Count;
        var expiries = live.Select(s => s.ExpiresAt).OfType<DateTimeOffset>().Select(e => (e - t).TotalDays).ToList();
        v[i++] = expiries.Count == 0 ? 400 : (float)Math.Clamp(expiries.Min(), -10, 400);
        v[i++] = expiries.Count(d => d <= 14);
        var created = services.Select(s => s.CreatedAt).OfType<DateTimeOffset>().ToList();
        v[i++] = created.Count == 0 ? 0 : Log1P((t - created.Min()).TotalDays);
        var liveCreated = live.Select(s => s.CreatedAt).OfType<DateTimeOffset>().ToList();
        v[i++] = liveCreated.Count == 0 ? 0 : Log1P((t - liveCreated.Max()).TotalDays);
        v[i++] = Log1P(statuses.Count(x => x.Status == ServiceStatus.Deleted));

        var unlocked = SourceEnums.ParseBonusState(a.BonusState) == BonusState.Unlocked;
        var funds = Math.Max(a.Balance ?? 0, 0) + (unlocked ? Math.Max(a.BonusBalance ?? 0, 0) : 0);
        v[i++] = revenue > 0 ? (float)Math.Clamp(funds / revenue, 0, 10) : 10;
        v[i++] = Log1P(Math.Max(a.Balance ?? 0, 0) / 100.0);
        v[i++] = unlocked ? 1 : 0;
        v[i++] = Log1P(Math.Max(a.WithdrawBalance ?? 0, 0) / 100.0);

        i = MlFeatures.Behavior(b, t, v, i);

        if (b is not null)
        {
            var today = DateOnly.FromDateTime(t.UtcDateTime);
            ActivityDelta W(int days) => b.Sum(today.AddDays(-(days - 1)), today);
            v[i] = W(14).BackupDownloads;
            v[i + 1] = W(30).DnsExternal;
            var w90 = W(90);
            v[i + 2] = w90.RetentionOffers;
            v[i + 3] = w90.RetentionHoldouts;
        }
        i += 4;
        if (i != Names.Count) throw new InvalidOperationException($"Вектор признаков клиента: {i} значений при {Names.Count} именах");
        return v;
    }

    private static float Log1P(double x) => (float)Math.Log(1 + Math.Max(0, x));
}
