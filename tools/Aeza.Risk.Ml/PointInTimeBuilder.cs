using Aeza.Risk.Core;
using Aeza.Risk.Core.Source;

namespace Aeza.Risk.Ml;

/// <summary>Строка набора: решение по сроку услуги на момент t, признаки, метка и балл правил на тот же момент</summary>
public sealed record DatasetRow(
    long AccountId, long ServiceId, DateTimeOffset DecisionAt, DateTimeOffset ExpiresAt, int OffsetDays, string Term, int Label, decimal? RulesScore,
    decimal? RulesScoreWide, float[] Features);

/// <summary>Итог построения: строки решений по услугам, счётчики и еженедельные срезы клиентов с метками</summary>
public sealed record DatasetBuild(
    IReadOnlyList<DatasetRow> Rows, int Decisions, int Skipped, int Positives, int Negatives, int Excluded, int Censored,
    DateTimeOffset DataStart, DateTimeOffset DataEnd, DateTimeOffset LabelsCompleteUpTo, long Events, IReadOnlyList<CustomerWeekRow>? Customers = null);

/// <summary>Набор point-in-time: журнал переигрывается по времени события тем же кодом, что и приём анализатора (SourceChangeRules</summary>
public sealed class PointInTimeBuilder(int graceDays = 5)
{
    private sealed record Decision(long ServiceId, DateTimeOffset ExpiresAt, DateTimeOffset At, int OffsetDays);

    private readonly RiskEngine _engine = new();
    private readonly ModelSettings _rules = ModelSettings.Default(FeatureCatalog.All);
    private readonly ModelSettings _rulesWide = ModelSettings.Default(FeatureCatalog.All) with { UrgencyWindowDays = ModelSettingsValidator.MaxUrgencyWindowDays };

    public DatasetBuild Build(IJournal journal)
    {
        var state = SourceState.From(journal.Base);
        var baseAsOf = journal.BaseAsOf;
        var ledger = new ExpiryLedger();
        var queue = new PriorityQueue<Decision, (long, long)>();
        long order = 0;
        var tx = new Dictionary<long, Dictionary<long, TransactionInput>>();
        foreach (var t in journal.Base.Transactions) Index(tx, t);
        var activity = new ActivityTracker();
        // Срез клиента: признаки и балл правил на момент среза, по состоянию журнала на этот момент
        (float[] Features, decimal? Rules)? CustomerSnapshot(long account, DateTimeOffset at)
        {
            if (!state.Accounts.TryGetValue(account, out var acc)) return null;
            var services = state.ServicesOf(account).ToList();
            var features = MlCustomerFeatures.Compute(acc, services, activity.Behavior(account, at), at);
            var transactions = services.SelectMany(x => tx.TryGetValue(x.Id, out var m) ? (IEnumerable<TransactionInput>)m.Values : []).ToList();
            return (features, _engine.Evaluate(new SourceData([acc], services, transactions), _rules, at).Customers[0].Score);
        }
        var customers = new CustomerTimeline(baseAsOf, CustomerSnapshot);
        foreach (var a in journal.Base.Accounts) customers.Observe(a.Id, baseAsOf, state.ServicesOf(a.Id));
        var evaluated = new List<(Decision D, DatasetRow Row)>();
        int decisions = 0, skipped = 0;
        long events = 0;

        void Schedule(IEnumerable<ExpiryTerm> terms)
        {
            foreach (var term in terms)
            {
                if (!state.Services.TryGetValue(term.ServiceId, out var s)) continue;
                foreach (var k in MlDecision.OffsetsDays(SourceEnums.ParseTerm(s.PaymentTerm)))
                {
                    var at = term.ExpiresAt.AddDays(-k);
                    if (at > term.RegisteredAt && at > baseAsOf) queue.Enqueue(new Decision(term.ServiceId, term.ExpiresAt, at, k), (at.UtcTicks, order++));
                }
            }
        }

        Schedule(ledger.Apply(journal.Base.Services.SelectMany(s => ExpiryOutcomeRules.OnServiceObserved(null, s, baseAsOf, graceDays)), graceDays));
        var dataStart = baseAsOf;
        var dataEnd = baseAsOf;
        var lastResolve = DateTimeOffset.MinValue;

        void EvaluateUntil(DateTimeOffset time)
        {
            while (queue.TryPeek(out var d, out _) && d.At < time)
            {
                queue.Dequeue();
                decisions++;
                if (Evaluate(d, state, tx, activity) is { } row) evaluated.Add((d, row));
                else skipped++;
            }
        }

        foreach (var e in journal.Events())
        {
            events++;
            EvaluateUntil(e.OccurredAt);
            customers.SnapshotUntil(e.OccurredAt);
            if (e.OccurredAt > dataEnd) dataEnd = e.OccurredAt;
            if (e.Domain is { } d)
            {
                activity.Add(d);
                customers.Domain(d);
            }
            // Событие не позже даты базы: результат уже в базе (правило before_base анализатора)
            if (e.OccurredAt <= baseAsOf) continue;
            foreach (var change in e.Changes)
            {
                var before = change.Kind == SourceRecordKind.Service ? state.Services.GetValueOrDefault(change.Service!.Id) : null;
                var r = state.Apply(change);
                if (r.Outcome != ChangeOutcome.Applied) continue;
                if (change.Kind == SourceRecordKind.Service)
                {
                    Schedule(ledger.Apply(ExpiryOutcomeRules.OnServiceObserved(before, change.Service!, e.OccurredAt, graceDays), graceDays));
                    customers.Observe(change.Service!.OwnerId, e.OccurredAt, state.ServicesOf(change.Service!.OwnerId));
                }
                else if (change.Kind == SourceRecordKind.Transaction) Index(tx, change.Transaction!);
            }
            // Источник полон до текущего события (поток по порядку): созревшие сроки решаются раз в час модельного времени
            if (e.OccurredAt - lastResolve >= TimeSpan.FromHours(1))
            {
                ledger.ResolveDue(e.OccurredAt, e.OccurredAt, graceDays, id => state.Services.GetValueOrDefault(id));
                lastResolve = e.OccurredAt;
            }
        }
        EvaluateUntil(dataEnd.AddTicks(1));
        customers.SnapshotUntil(dataEnd);
        ledger.ResolveDue(dataEnd, dataEnd, graceDays, id => state.Services.GetValueOrDefault(id));

        var rows = new List<DatasetRow>();
        int positives = 0, negatives = 0, excluded = 0, censored = 0;
        foreach (var (d, row) in evaluated)
        {
            switch (ledger.Find(d.ServiceId, d.ExpiresAt)?.Outcome)
            {
                case ExpiryOutcomes.Renewed:
                    rows.Add(row with { Label = 0 });
                    negatives++;
                    break;
                case ExpiryOutcomes.NotRenewed:
                    rows.Add(row with { Label = 1 });
                    positives++;
                    break;
                case ExpiryOutcomes.Excluded:
                    excluded++;
                    break;
                default:
                    censored++;
                    break;
            }
        }
        return new DatasetBuild(rows, decisions, skipped, positives, negatives, excluded, censored, dataStart, dataEnd, dataEnd, events, customers.Label(dataEnd));
    }

    private DatasetRow? Evaluate(Decision d, SourceState state, Dictionary<long, Dictionary<long, TransactionInput>> tx, ActivityTracker activity)
    {
        if (!state.Services.TryGetValue(d.ServiceId, out var s) || s.ExpiresAt != d.ExpiresAt) return null;
        if (SourceEnums.ParseStatus(s.Status) is not (ServiceStatus.Active or ServiceStatus.Suspended)) return null;
        if (!state.Accounts.TryGetValue(s.OwnerId, out var account)) return null;
        var services = state.ServicesOf(s.OwnerId).ToList();
        var behavior = activity.Behavior(s.OwnerId, d.At);
        var features = MlFeatures.Compute(s, account, services, behavior, d.At);
        // Балл правил на тот же момент — базовая линия (веса по умолчанию: поведение не учитывается)
        var transactions = services.SelectMany(x => tx.TryGetValue(x.Id, out var m) ? (IEnumerable<TransactionInput>)m.Values : []).ToList();
        var data = new SourceData([account], services, transactions);
        decimal? Score(ModelSettings settings) => _engine.Evaluate(data, settings, d.At).Customers[0].Services.FirstOrDefault(x => x.Id == s.Id)?.Score;
        return new DatasetRow(s.OwnerId, s.Id, d.At, d.ExpiresAt, d.OffsetDays, s.PaymentTerm ?? "", -1, Score(_rules), Score(_rulesWide), features);
    }

    private static void Index(Dictionary<long, Dictionary<long, TransactionInput>> tx, TransactionInput t)
    {
        if (!tx.TryGetValue(t.ServiceId, out var m)) tx[t.ServiceId] = m = new Dictionary<long, TransactionInput>();
        m[t.Id] = t;
    }

    /// <summary>Дневная активность по мере переигрывания (та же свёртка, что у проекции анализатора)</summary>
    private sealed class ActivityTracker
    {
        private readonly Dictionary<long, (DateOnly KnownSince, DateOnly? LastLogin, SortedDictionary<DateOnly, ActivityDelta> Days)> _accounts = new();

        public void Add(DomainEventInput e)
        {
            if (ActivityProjection.Fold(e) is not var (account, day, delta)) return;
            if (!_accounts.TryGetValue(account, out var a)) a = (day, null, new SortedDictionary<DateOnly, ActivityDelta>());
            if (a.Days.TryGetValue(day, out var cur)) cur.Add(delta);
            else a.Days[day] = delta;
            if (day < a.KnownSince) a.KnownSince = day;
            if (delta.Logins > 0 && (a.LastLogin is null || day > a.LastLogin)) a.LastLogin = day;
            // Старше окна признаков дни не нужны
            while (a.Days.Count > 0 && a.Days.Keys.First() < day.AddDays(-(AccountBehavior.WindowDays + 10))) a.Days.Remove(a.Days.Keys.First());
            _accounts[account] = a;
        }

        public AccountBehavior? Behavior(long account, DateTimeOffset t)
        {
            if (!_accounts.TryGetValue(account, out var a)) return null;
            var today = DateOnly.FromDateTime(t.UtcDateTime);
            var from = today.AddDays(-(AccountBehavior.WindowDays - 1));
            var days = a.Days.Where(kv => kv.Key >= from && kv.Key <= today).Select(kv => new ActivityDay(kv.Key, Copy(kv.Value))).ToList();
            return a.KnownSince > today ? null : new AccountBehavior(a.KnownSince, a.LastLogin is { } l && l <= today ? l : null, days);
        }

        private static ActivityDelta Copy(ActivityDelta d)
        {
            var c = new ActivityDelta();
            c.Add(d);
            return c;
        }
    }
}
