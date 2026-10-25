namespace Aeza.Risk.Core;

public sealed record FeatureResult(
    string Key, string Title, decimal? Value, decimal Weight, decimal Contribution,
    IReadOnlyList<string> MissingInputs, IReadOnlyDictionary<string, object?> Evidence, string Text);

public sealed record ServiceAssessment(
    long Id, string Name, string? Status, string? PaymentTerm, bool? AutoProlong,
    DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt, long? Price,
    bool Renewable, bool Scored, decimal? Score, decimal? RawScore, bool Capped,
    IReadOnlyList<FeatureResult> Features, DateTimeOffset? NextDue, int RenewalsInHorizon, DateTimeOffset? FirstDue,
    long PlannedMinor, long UnfundedMinor, decimal AtRiskExact,
    IReadOnlyList<string> Flags, IReadOnlyList<string> MissingInputs, TransactionInput? LastTransaction);

public sealed record Reason(
    string Code, string FeatureKey, string Title, string Text, long? ServiceId,
    decimal Value, decimal Weight, decimal Contribution, IReadOnlyDictionary<string, object?> Evidence);

public sealed record CustomerFunding(
    bool Known, long FundsMinor, long BalanceAvailableMinor, long BonusAvailableMinor, long BalanceUsedMinor, long BonusUsedMinor,
    bool BonusEligible, string? BonusState, long WithdrawExcludedMinor, DateTimeOffset? FirstUnfundedDue);

public sealed record CustomerMoney(long PlannedRenewalsMinor, long UnfundedMinor, long AtRiskMinor);

public sealed record CustomerAssessment(
    AccountInput Account, decimal? Score, RiskLevel Level, ScoreState State, Reason PrimaryReason, IReadOnlyList<Reason> Reasons,
    IReadOnlyList<ServiceAssessment> Services, CustomerFunding Funding, CustomerMoney Money, IReadOnlyList<string> Flags,
    int ServicesTotal, int ServicesRenewable, int ServicesScored, long UnscoredPlannedMinor,
    FullChurnIndicator FullChurn, PaymentTrendIndicator PaymentTrend);

public sealed record LevelMoney(int Customers, long PlannedRenewalsMinor, long UnfundedMinor, long AtRiskMinor);

public sealed record ForecastDay(DateOnly Date, long PlannedMinor, long AtRiskMinor, int Renewals);

public sealed record EngineSummary(
    int CustomersTotal, IReadOnlyDictionary<string, int> ByLevel, IReadOnlyDictionary<string, int> ByState,
    int ServicesTotal, int ServicesRenewable, int ServicesScored, int ServicesUnscored, int ServicesUnschedulable,
    IReadOnlyDictionary<string, int> ServicesByStatus, int HorizonDays,
    long PlannedRenewalsMinor, long UnfundedMinor, long AtRiskMinor, IReadOnlyDictionary<string, LevelMoney> MoneyByLevel,
    long UnscoredPlannedMinor, long UnfundedUnknownPlannedMinor, IReadOnlyDictionary<string, int> QualityFlags, decimal ScoreSum, int ScoredCustomers,
    IReadOnlyDictionary<string, int> FullChurnByLevel, IReadOnlyDictionary<string, int> FullChurnByState, IReadOnlyDictionary<string, int> PaymentTrendByState);

public sealed record EngineResult(
    DateTimeOffset AsOf, string EngineVersion, IReadOnlyList<CustomerAssessment> Customers, EngineSummary Summary, IReadOnlyList<ForecastDay> Forecast);

/// <summary>Чистый расчёт: снимок + настройки + момент оценки → оценки клиентов, причины и деньги</summary>
public sealed class RiskEngine
{
    /// <summary>1.1.0 — индикаторы «уход целиком» и «тренд платежей»; 1.2.0 — признаки поведения по событиям v2</summary>
    public const string Version = "1.2.0";
    /// <summary>Точность score: устраняет артефакты деления (2/7 и т.п.) у порогов; уровень определяется по этому значению</summary>
    public const int ScorePrecision = 6;

    private readonly IReadOnlyList<IRiskFeature> _features;

    public RiskEngine(IReadOnlyList<IRiskFeature>? features = null) => _features = features ?? FeatureCatalog.All;

    public IReadOnlyList<IRiskFeature> Features => _features;

    /// <summary>Оценить всех клиентов на момент <paramref name="asOf"/></summary>
    /// <param name="source">Текущее состояние источника</param>
    /// <param name="settings">Ревизия модели</param>
    /// <param name="asOf">Момент оценки</param>
    /// <param name="behavior">Поведение клиентов на момент оценки (проекция событий v2); нет клиента — нет данных</param>
    public EngineResult Evaluate(SourceData source, ModelSettings settings, DateTimeOffset asOf, IReadOnlyDictionary<long, AccountBehavior>? behavior = null)
    {
        var horizonEnd = asOf.AddDays(settings.HorizonDays);
        var window = TimeSpan.FromDays(settings.UrgencyWindowDays);
        var servicesByOwner = source.Services.OrderBy(s => s.Id).ToLookup(s => s.OwnerId);
        var lastTx = source.Transactions
            .GroupBy(t => t.ServiceId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.CreatedAt ?? DateTimeOffset.MinValue).ThenByDescending(t => t.Id).First());
        var txByService = source.Transactions.ToLookup(t => t.ServiceId);

        var customers = new List<CustomerAssessment>(source.Accounts.Count);
        var forecast = new SortedDictionary<DateOnly, (long Planned, decimal AtRisk, int Count)>();
        foreach (var account in source.Accounts.OrderBy(a => a.Id))
        {
            var services = servicesByOwner[account.Id].ToList();
            var transactions = services.SelectMany(s => txByService[s.Id]).ToList();
            customers.Add(EvaluateCustomer(account, services, transactions, lastTx, settings, asOf, horizonEnd, window, forecast, behavior?.GetValueOrDefault(account.Id)));
        }

        var summary = Summarize(customers, settings);
        var days = forecast.Select(kv => new ForecastDay(kv.Key, kv.Value.Planned, RoundMinor(kv.Value.AtRisk), kv.Value.Count)).ToList();
        return new EngineResult(asOf, Version, customers, summary, days);
    }

    private CustomerAssessment EvaluateCustomer(
        AccountInput account, List<ServiceInput> services, List<TransactionInput> transactions, Dictionary<long, TransactionInput> lastTx, ModelSettings settings,
        DateTimeOffset asOf, DateTimeOffset horizonEnd, TimeSpan window, SortedDictionary<DateOnly, (long Planned, decimal AtRisk, int Count)> forecast,
        AccountBehavior? behavior)
    {
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        var bonusState = SourceEnums.ParseBonusState(account.BonusState);
        if (bonusState == BonusState.Unknown) flags.Add(QualityFlags.BonusStateUnknown);
        if (account.Balance is null) flags.Add(QualityFlags.MissingBalance);
        else if (account.Balance < 0) flags.Add(QualityFlags.NegativeBalance);

        var statuses = services.ToDictionary(s => s.Id, s => SourceEnums.ParseStatus(s.Status));
        var renewable = services.Where(s => SourceEnums.IsRenewable(statuses[s.Id])).ToList();
        var unknownStatus = services.Where(s => statuses[s.Id] == ServiceStatus.Unknown).ToList();
        if (unknownStatus.Count > 0)
        {
            // Услугу с неизвестным статусом нельзя ни исключить, ни учесть в покрытии баланса.
            flags.Add(QualityFlags.UnknownStatus);
            flags.Add(QualityFlags.FundingIncomplete);
        }

        var plans = renewable.ToDictionary(s => s.Id, s => RenewalSchedule.Plan(s, statuses[s.Id], asOf));
        if (plans.Values.Any(p => !p.Schedulable)) flags.Add(QualityFlags.FundingIncomplete);
        var funding = FundingWaterfall.Allocate(account, plans.Values.ToList(), horizonEnd);
        var fundingKnown = account.Balance is not null;

        var assessments = new List<ServiceAssessment>(services.Count);
        foreach (var s in services)
        {
            var status = statuses[s.Id];
            lastTx.TryGetValue(s.Id, out var last);
            if (!SourceEnums.IsRenewable(status))
            {
                assessments.Add(new ServiceAssessment(s.Id, s.Name, s.Status, s.PaymentTerm, s.AutoProlong, s.CreatedAt, s.ExpiresAt, s.Price,
                    false, false, null, null, false, Array.Empty<FeatureResult>(), null, 0, null, 0, 0, 0m,
                    status == ServiceStatus.Unknown ? new[] { QualityFlags.UnknownStatus } : Array.Empty<string>(),
                    status == ServiceStatus.Unknown ? new[] { "status" } : Array.Empty<string>(), last));
                continue;
            }

            var plan = plans[s.Id];
            foreach (var f in plan.Flags) flags.Add(f);
            funding.PerService.TryGetValue(s.Id, out var sf);
            var ctx = new ServiceContext(s, status, account, plan, sf, funding, fundingKnown, last, asOf, window, behavior);
            var results = new List<FeatureResult>(_features.Count);
            var missing = new List<string>();
            decimal raw = 0m;
            foreach (var feature in _features)
            {
                var weight = settings.WeightOf(feature.Key);
                var v = feature.Evaluate(ctx);
                var contribution = v.Value is { } val ? Math.Round(weight * val, ScorePrecision, MidpointRounding.ToEven) : 0m;
                // Пропуск признака поведения не снимает оценку: балл — по остальным признакам, клиенту — флаг
                if (v.IsMissing && weight > 0 && feature.Behavioral) flags.Add(QualityFlags.BehaviorDataMissing);
                else if (v.IsMissing && weight > 0) missing.AddRange(v.MissingInputs);
                if (weight != 0m && v.Value is { } x) raw += weight * x; // признак с весом 0 не меняет даже масштаб числа
                results.Add(new FeatureResult(feature.Key, feature.Title, v.Value, weight, contribution, v.MissingInputs, v.Evidence, v.Text));
            }

            var scored = !results.Where((r, i) => r.Value is null && r.Weight > 0 && !_features[i].Behavioral).Any();
            decimal? score = scored ? Math.Round(Math.Min(100m, raw), ScorePrecision, MidpointRounding.ToEven) : null;
            var planned = sf?.PlannedMinor ?? 0;
            var atRisk = score is { } sc ? planned * sc / 100m : 0m;
            if (sf is not null)
            {
                foreach (var (day, (amount, count)) in sf.ByDay)
                {
                    var add = score is { } scd ? amount * scd / 100m : 0m;
                    forecast[day] = forecast.TryGetValue(day, out var cur)
                        ? (checked(cur.Planned + amount), cur.AtRisk + add, cur.Count + count)
                        : (amount, add, count);
                }
            }
            assessments.Add(new ServiceAssessment(s.Id, s.Name, s.Status, s.PaymentTerm, s.AutoProlong, s.CreatedAt, s.ExpiresAt, s.Price,
                true, scored, score, scored ? Math.Round(raw, ScorePrecision, MidpointRounding.ToEven) : null, scored && raw > 100m,
                results, plan.NextDue, sf?.Renewals ?? 0, sf?.FirstDue, planned, sf?.UnfundedMinor ?? 0, atRisk,
                plan.Flags, missing.Distinct().ToList(), last));
        }

        var scoredServices = assessments.Where(a => a.Scored).ToList();
        var assessable = renewable.Count + unknownStatus.Count;
        decimal? customerScore = scoredServices.Count > 0 ? scoredServices.Max(a => a.Score!.Value) : null;
        var state = assessable == 0 ? ScoreState.NoRenewableServices
            : scoredServices.Count == 0 ? ScoreState.InsufficientData
            : scoredServices.Count < assessable ? ScoreState.Partial
            : ScoreState.Complete;

        var reasons = BuildReasons(scoredServices);
        var primary = PrimaryReason(state, customerScore, scoredServices, assessments);
        var atRiskExact = scoredServices.Aggregate(0m, (acc, a) => acc + a.AtRiskExact);
        var unscoredPlanned = assessments.Where(a => a.Renewable && !a.Scored).Aggregate(0L, (acc, a) => checked(acc + a.PlannedMinor));
        // Баланс неизвестен → непокрытая часть неизвестна (unknown ≠ 0): в U не входит, учитывается отдельно.
        var money = new CustomerMoney(funding.PlannedMinor, fundingKnown ? funding.UnfundedMinor : 0, RoundMinor(atRiskExact));
        var fundingDto = new CustomerFunding(fundingKnown, funding.FundsMinor, funding.BalanceAvailableMinor, funding.BonusAvailableMinor,
            funding.BalanceUsedMinor, funding.BonusUsedMinor, funding.BonusEligible, account.BonusState,
            Math.Max(account.WithdrawBalance ?? 0, 0), funding.FirstUnfundedDue);

        return new CustomerAssessment(account, customerScore, settings.LevelOf(customerScore), state, primary, reasons, assessments,
            fundingDto, money, flags.ToList(), services.Count, renewable.Count, scoredServices.Count, unscoredPlanned,
            ChurnIndicators.FullChurn(assessments, settings), ChurnIndicators.PaymentTrend(services, transactions, asOf, settings.Churn));
    }

    private List<Reason> BuildReasons(List<ServiceAssessment> scored)
    {
        var order = _features.Select((f, i) => (f.Key, i)).ToDictionary(x => x.Key, x => x.i);
        var behavioral = _features.Where(f => f.Behavioral).Select(f => f.Key).ToHashSet();
        return scored
            .SelectMany(s => s.Features.Where(f => f.Contribution > 0).Select(f => (s, f)))
            .OrderByDescending(x => x.f.Contribution)
            .ThenByDescending(x => x.s.Score)
            .ThenBy(x => order[x.f.Key])
            .ThenBy(x => x.s.Id)
            // Признак поведения — про клиента, а не про услугу: одна причина, а не по разу на каждую услугу
            .DistinctBy(x => behavioral.Contains(x.f.Key) ? x.f.Key : $"{x.f.Key}#{x.s.Id}")
            .Select(x => new Reason(x.f.Key, x.f.Key, x.f.Title, x.f.Text, x.s.Id, x.f.Value ?? 0m, x.f.Weight, x.f.Contribution, x.f.Evidence))
            .ToList();
    }

    private Reason PrimaryReason(ScoreState state, decimal? score, List<ServiceAssessment> scored, List<ServiceAssessment> all)
    {
        var empty = new Dictionary<string, object?>();
        if (state == ScoreState.NoRenewableServices)
        {
            var statuses = string.Join(", ", all.Select(a => a.Status ?? "нет статуса").Distinct());
            var text = all.Count == 0 ? "Нет услуг — оценивать нечего" : $"Нет продлеваемых услуг ({statuses}): продление невозможно или услуга уже удалена";
            return new Reason("no_renewable_services", "", "Нет оценки", text, null, 0, 0, 0, empty);
        }
        if (score is null)
        {
            var missing = string.Join(", ", all.SelectMany(a => a.MissingInputs).Distinct());
            return new Reason("insufficient_data", "", "Нет оценки", $"Недостаточно данных для оценки: {missing}", null, 0, 0, 0, empty);
        }
        var order = _features.Select((f, i) => (f.Key, i)).ToDictionary(x => x.Key, x => x.i);
        var worst = scored.Where(s => s.Score == score).OrderBy(s => s.Id).First();
        var top = worst.Features.Where(f => f.Contribution > 0).OrderByDescending(f => f.Contribution).ThenBy(f => order[f.Key]).FirstOrDefault();
        if (top is null) return new Reason("no_risk_signals", "", "Нет вкладов в индекс",
            "Индекс 0: ни один признак не дал вклада — ожидающих оплат и выключенных услуг нет, ближайшие сроки и непокрытые продления дальше окна срочности", worst.Id, 0, 0, 0, empty);
        return new Reason(top.Key, top.Key, top.Title, top.Text, worst.Id, top.Value ?? 0m, top.Weight, top.Contribution, top.Evidence);
    }

    private static EngineSummary Summarize(List<CustomerAssessment> customers, ModelSettings settings)
    {
        var levels = new[] { RiskLevel.High, RiskLevel.Medium, RiskLevel.Low, RiskLevel.Unknown };
        var byLevel = levels.ToDictionary(l => l.ToApi(), l => customers.Count(c => c.Level == l));
        var states = new[] { ScoreState.Complete, ScoreState.Partial, ScoreState.NoRenewableServices, ScoreState.InsufficientData };
        var byState = states.ToDictionary(s => s.ToApi(), s => customers.Count(c => c.State == s));
        var services = customers.SelectMany(c => c.Services).ToList();
        var byStatus = services.GroupBy(s => s.Status ?? "null").OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var moneyByLevel = levels.ToDictionary(l => l.ToApi(), l =>
        {
            var cs = customers.Where(c => c.Level == l).ToList();
            return new LevelMoney(cs.Count, cs.Aggregate(0L, (a, c) => checked(a + c.Money.PlannedRenewalsMinor)),
                cs.Aggregate(0L, (a, c) => checked(a + c.Money.UnfundedMinor)), cs.Aggregate(0L, (a, c) => checked(a + c.Money.AtRiskMinor)));
        });
        var flags = customers.SelectMany(c => c.Flags).GroupBy(f => f).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var scoredCustomers = customers.Where(c => c.Score is not null).ToList();
        return new EngineSummary(
            customers.Count, byLevel, byState,
            services.Count, services.Count(s => s.Renewable), services.Count(s => s.Scored), services.Count(s => s.Renewable && !s.Scored),
            customers.Sum(c => c.Services.Count(s => s.Renewable && s.Flags.Any(f => f is QualityFlags.MissingPrice or QualityFlags.UnknownPaymentTerm or QualityFlags.MissingExpiresAt))),
            byStatus, settings.HorizonDays,
            customers.Aggregate(0L, (a, c) => checked(a + c.Money.PlannedRenewalsMinor)),
            customers.Aggregate(0L, (a, c) => checked(a + c.Money.UnfundedMinor)),
            customers.Aggregate(0L, (a, c) => checked(a + c.Money.AtRiskMinor)),
            moneyByLevel,
            customers.Aggregate(0L, (a, c) => checked(a + c.UnscoredPlannedMinor)),
            customers.Where(c => !c.Funding.Known).Aggregate(0L, (a, c) => checked(a + c.Money.PlannedRenewalsMinor)),
            flags,
            scoredCustomers.Aggregate(0m, (a, c) => a + c.Score!.Value),
            scoredCustomers.Count,
            levels.ToDictionary(l => l.ToApi(), l => customers.Count(c => c.FullChurn.Level == l)),
            FullChurnStates.All.ToDictionary(st => st, st => customers.Count(c => c.FullChurn.State == st)),
            PaymentTrendStates.All.ToDictionary(st => st, st => customers.Count(c => c.PaymentTrend.State == st)));
    }

    public static long RoundMinor(decimal value) => (long)Math.Round(value, 0, MidpointRounding.ToEven);
}
