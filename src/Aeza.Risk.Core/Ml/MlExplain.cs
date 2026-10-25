namespace Aeza.Risk.Core;

/// <summary>Вклад фактора в прогноз модели для услуги</summary>
/// <param name="Key">Группа признаков</param>
/// <param name="Title">Название фактора</param>
/// <param name="Delta">На сколько вероятность выше, чем была бы при этом факторе как у типичного клиента; отрицательный — фактор снижает риск</param>
/// <param name="Text">Значения клиента</param>
public sealed record MlDriver(string Key, string Title, double Delta, string Text);

/// <summary>Объяснение прогноза: группа признаков по очереди заменяется значениями типичного клиента (медианы по услугам в окне модели),
/// модель пересчитывается, разница вероятностей — вклад фактора. Вклады не складываются в вероятность: факторы взаимодействуют</summary>
public static class MlExplain
{
    /// <summary>Вклады меньше этого (полпроцентного пункта) не показываются</summary>
    public const double MinDelta = 0.005;

    /// <summary>Точные вклады деревьев</summary>
    public const string TreeShapMethod = "treeshap";

    /// <summary>Сравнение с типичным клиентом</summary>
    public const string OcclusionMethod = "occlusion";

    private readonly record struct Row(float[] Batch, int Offset)
    {
        public double this[string name] => Batch[Offset + MlFeatures.Index[name]];
    }

    private sealed record Group(string Key, string Title, string[] Features, bool Behavioral, Func<Row, string> Text);

    private static string N(double v, int decimals = 1) => Fmt.Num((decimal)Math.Round(v, 6), decimals);

    // Обратное к log1p, которым признаки сжаты при обучении
    private static double Un(double v) => Math.Max(0, Math.Exp(v) - 1);

    private static string Eur(double eur) => "€" + N(eur, eur >= 100 ? 0 : 2);

    private static string Term(Row r) =>
        r["term_week"] > 0.5 ? "оплата понедельно"
        : r["term_month"] > 0.5 ? "оплата помесячно"
        : r["term_quarter"] > 0.5 ? "оплата поквартально"
        : r["term_half_year"] > 0.5 ? "оплата раз в полгода"
        : r["term_year"] > 0.5 ? "оплата раз в год"
        : "период оплаты неизвестен";

    private static readonly Group[] Groups =
    [
        new("funds", "Средства на продление", ["funds_cover", "balance_eur_log", "bonus_unlocked", "withdraw_eur_log"], false,
            r => (r["funds_cover"] >= 1
                    ? $"Средств хватает на продление ({N(r["funds_cover"])}× цены)"
                    : $"Средства покрывают {N(r["funds_cover"] * 100, 0)} % цены продления")
                + $", баланс {Eur(Un(r["balance_eur_log"]))}" + (r["bonus_unlocked"] > 0.5 ? "" : ", бонусы заблокированы")),
        new("auto_prolong", "Автопродление", ["auto_prolong"], false,
            r => r["auto_prolong"] > 0.5 ? "Автопродление включено" : "Автопродление выключено"),
        new("status", "Состояние услуги", ["status_suspended", "status_prolong_wait"], false,
            r => r["status_suspended"] > 0.5 ? "Сервер выключен (suspended), но тарифицируется"
                : r["status_prolong_wait"] > 0.5 ? "Срок истёк, ждём оплату" : "Услуга активна"),
        new("expiry", "Срок оплаты", ["days_to_expiry"], false, r => $"Срок через {N(r["days_to_expiry"])} дн."),
        new("tariff", "Тариф и цена", ["term_week", "term_month", "term_quarter", "term_half_year", "term_year", "price_month_eur"], false,
            r => $"{Term(r)}, {Eur(r["price_month_eur"])} в месяц"),
        new("tenure", "Стаж и услуги", ["service_age_days_log", "live_services", "tenure_days_log"], false,
            r => $"Клиент {N(Un(r["tenure_days_log"]), 0)} дн., услуге {N(Un(r["service_age_days_log"]), 0)} дн., действующих услуг {N(r["live_services"], 0)}"),
        new("logins", "Входы в панель", ["logins_7", "logins_30", "days_since_login"], true,
            r => $"Не входил {N(r["days_since_login"], 0)} дн.; входов за 7 дн.: {N(r["logins_7"], 0)}, за 30 дн.: {N(r["logins_30"], 0)}"),
        new("usage", "Нагрузка на сервер", ["cpu_7", "cpu_base", "cpu_ratio", "usage_days_7"], true,
            r => r["usage_days_7"] < 0.5
                ? "Нет данных о нагрузке за 7 дн."
                : $"Нагрузка за 7 дн. — {N(r["cpu_ratio"] * 100, 0)} % от прежней (CPU {N(r["cpu_7"])} % против {N(r["cpu_base"])} %)"),
        new("payments", "Платежи и пополнения", ["failed_payments_30", "payments_30_eur_log", "topups_30", "topups_90_eur_log"], true,
            r => $"Неудачных списаний за 30 дн.: {N(r["failed_payments_30"], 0)}; пополнений за 30 дн.: {N(r["topups_30"], 0)}, "
                + $"за 90 дн. на {Eur(Un(r["topups_90_eur_log"]))}"),
        new("support", "Поддержка и аварии", ["tickets_30", "unsolved_30", "csat_min_30", "nps_min_90", "incident_min_30_log"], true,
            r => $"Обращений за 30 дн.: {N(r["tickets_30"], 0)}, нерешённых {N(r["unsolved_30"], 0)}; худшая оценка {N(r["csat_min_30"], 0)}/5, "
                + $"NPS {N(r["nps_min_90"], 0)}; аварии {N(Un(r["incident_min_30_log"]), 0)} мин."),
        new("changes", "Изменения тарифа и услуг",
            ["downgrades_60", "upgrades_60", "autoprolong_off_30", "suspends_30", "backups_14", "deletions_90", "renewals_90"], true,
            r => $"За 60 дн. понижений тарифа {N(r["downgrades_60"], 0)}, повышений {N(r["upgrades_60"], 0)}; за 30 дн. выключал автопродление "
                + $"{N(r["autoprolong_off_30"], 0)} раз, сервер {N(r["suspends_30"], 0)}; за 90 дн. удалил услуг {N(r["deletions_90"], 0)}, "
                + $"продлил {N(r["renewals_90"], 0)}; бэкапов за 14 дн. {N(r["backups_14"], 0)}"),
        new("money_out", "Вывод средств и бонусы", ["withdrawals_30_eur_log", "bonus_expired_45"], true,
            r => $"Вывел за 30 дн. {Eur(Un(r["withdrawals_30_eur_log"]))}; сгоревших бонусов за 45 дн.: {N(r["bonus_expired_45"], 0)}"),
    ];

    // Без событий поведения отдельные поведенческие факторы не сравниваются: «нет данных» — один фактор
    private static readonly Group NoBehavior = new("no_behavior", "Нет данных о поведении",
        ["has_behavior", .. Groups.Where(g => g.Behavioral).SelectMany(g => g.Features)], true,
        _ => "Источник не присылал событий о поведении клиента: входы, нагрузка, платежи, обращения");

    /// <summary>Вклады факторов для каждой строки</summary>
    /// <param name="batch">Строки признаков подряд (как для инференса)</param>
    /// <param name="rows">Число строк</param>
    /// <param name="p">Вероятности модели для строк</param>
    /// <param name="predict">Инференс той же модели</param>
    /// <returns>Для каждой строки — факторы с |вкладом| ≥ <see cref="MinDelta"/>, по убыванию вклада</returns>
    public static IReadOnlyList<MlDriver>[] Explain(float[] batch, int rows, float[] p, Func<float[], int, float[]> predict)
    {
        var n = MlFeatures.Names.Count;
        if (rows == 0) return [];
        var reference = Medians(batch, rows, n);
        var hasBehavior = MlFeatures.Index["has_behavior"];
        var jobs = new List<(int Row, Group Group)>();
        for (var r = 0; r < rows; r++)
        {
            var behavior = batch[r * n + hasBehavior] > 0.5f;
            foreach (var g in Groups.Where(g => !g.Behavioral || behavior).Append(behavior ? null : NoBehavior).OfType<Group>())
                if (g.Features.Any(f => batch[r * n + MlFeatures.Index[f]] != reference[MlFeatures.Index[f]])) jobs.Add((r, g));
        }
        if (jobs.Count == 0) return Enumerable.Range(0, rows).Select(_ => (IReadOnlyList<MlDriver>)[]).ToArray();

        var probe = new float[jobs.Count * n];
        for (var j = 0; j < jobs.Count; j++)
        {
            Array.Copy(batch, jobs[j].Row * n, probe, j * n, n);
            foreach (var f in jobs[j].Group.Features) probe[j * n + MlFeatures.Index[f]] = reference[MlFeatures.Index[f]];
        }
        var q = predict(probe, jobs.Count);
        if (q.Length != jobs.Count) throw new InvalidOperationException("Модель вернула не то число вероятностей");

        var drivers = Enumerable.Range(0, rows).Select(_ => new List<MlDriver>()).ToArray();
        for (var j = 0; j < jobs.Count; j++)
        {
            var (r, g) = jobs[j];
            var delta = Math.Round(p[r] - (double)q[j], 4);
            if (Math.Abs(delta) >= MinDelta) drivers[r].Add(new MlDriver(g.Key, g.Title, delta, g.Text(new Row(batch, r * n))));
        }
        return drivers.Select(d => (IReadOnlyList<MlDriver>)d.OrderByDescending(x => x.Delta).ThenBy(x => x.Key, StringComparer.Ordinal).ToList()).ToArray();
    }

    /// <summary>Вклады факторов по TreeSHAP: вклады признаков в логит пропорционально переведены в процентные пункты вероятности
    /// (их сумма — разница прогноза и среднего по обучающим данным) и сложены по факторам</summary>
    public static IReadOnlyList<MlDriver>[] FromShap(TreeEnsemble model, float[] batch, int rows, float[] p)
    {
        var n = MlFeatures.Names.Count;
        if (!model.Features.SequenceEqual(MlFeatures.Names)) throw new InvalidOperationException("Признаки деревьев не совпадают со схемой");
        var hasBehavior = MlFeatures.Index["has_behavior"];
        var withBehavior = MlFeatures.Names.Select(f => Groups.FirstOrDefault(g => g.Features.Contains(f))).ToArray();
        var withoutBehavior = MlFeatures.Names.Select((f, j) => j == hasBehavior || withBehavior[j] is { Behavioral: true } ? NoBehavior : withBehavior[j]).ToArray();
        var result = new IReadOnlyList<MlDriver>[rows];
        for (var r = 0; r < rows; r++)
        {
            var x = batch.AsSpan(r * n, n);
            var shap = TreeShap.Explain(model, x);
            var sum = shap.Phi.Sum();
            var total = p[r] - TreeEnsemble.Sigmoid(shap.Base);
            var groups = x[hasBehavior] > 0.5f ? withBehavior : withoutBehavior;
            var byGroup = new Dictionary<Group, double>();
            for (var j = 0; j < n; j++)
                if (groups[j] is { } g && sum != 0) byGroup[g] = byGroup.GetValueOrDefault(g) + shap.Phi[j] / sum * total;
            var row = new Row(batch, r * n);
            result[r] = byGroup.Select(kv => (kv.Key, Delta: Math.Round(kv.Value, 4))).Where(x => Math.Abs(x.Delta) >= MinDelta)
                .Select(x => new MlDriver(x.Key.Key, x.Key.Title, x.Delta, x.Key.Text(row)))
                .OrderByDescending(d => d.Delta).ThenBy(d => d.Key, StringComparer.Ordinal).ToList();
        }
        return result;
    }

    private static float[] Medians(float[] batch, int rows, int n)
    {
        var reference = new float[n];
        var column = new float[rows];
        for (var f = 0; f < n; f++)
        {
            for (var r = 0; r < rows; r++) column[r] = batch[r * n + f];
            Array.Sort(column);
            reference[f] = column[rows / 2];
        }
        return reference;
    }
}
