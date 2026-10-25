namespace Aeza.Risk.Core;

/// <summary>Покрытие продлений одной услуги после общего распределения баланса клиента</summary>
public sealed class ServiceFunding
{
    public long ServiceId { get; init; }
    public int Renewals { get; set; }
    public long PlannedMinor { get; set; }
    public long CoveredMinor { get; set; }
    public long UnfundedMinor => PlannedMinor - CoveredMinor;
    public DateTimeOffset? FirstDue { get; set; }
    public Renewal? FirstUnfunded { get; set; }
    public long FirstUnfundedCoveredMinor { get; set; }
    /// <summary>Плановые суммы по дням (UTC) — для прогноза</summary>
    public SortedDictionary<DateOnly, (long Amount, int Count)> ByDay { get; } = new();
}

public sealed class FundingResult
{
    public long BalanceAvailableMinor { get; init; }
    public long BonusAvailableMinor { get; init; }
    public long FundsMinor => checked(BalanceAvailableMinor + BonusAvailableMinor);
    public long BalanceUsedMinor { get; set; }
    public long BonusUsedMinor { get; set; }
    public bool BonusEligible { get; init; }
    public DateTimeOffset? FirstUnfundedDue { get; set; }
    public Dictionary<long, ServiceFunding> PerService { get; } = new();
    public long PlannedMinor => PerService.Values.Aggregate(0L, (s, f) => checked(s + f.PlannedMinor));
    public long UnfundedMinor => PerService.Values.Aggregate(0L, (s, f) => checked(s + f.UnfundedMinor));
}

/// <summary>Общий waterfall клиента: доступные средства (основной баланс, затем разрешённые бонусы) распределяются один раз по всем продлениям всех услуг в порядке (срок, serviceId, номер периода). withdrawBalance не тратится</summary>
public static class FundingWaterfall
{
    public static FundingResult Allocate(AccountInput account, IReadOnlyList<ServicePlan> plans, DateTimeOffset horizonEnd)
    {
        var bonusState = SourceEnums.ParseBonusState(account.BonusState);
        var balance = Math.Max(account.Balance ?? 0, 0);
        var bonus = bonusState == BonusState.Unlocked ? Math.Max(account.BonusBalance ?? 0, 0) : 0;
        var result = new FundingResult
        {
            BalanceAvailableMinor = balance,
            BonusAvailableMinor = bonus,
            BonusEligible = bonusState == BonusState.Unlocked,
        };

        var queue = new PriorityQueue<IEnumerator<Renewal>, (DateTimeOffset Due, long ServiceId, int Ordinal)>();
        foreach (var plan in plans.Where(p => p.Schedulable))
        {
            result.PerService[plan.ServiceId] = new ServiceFunding { ServiceId = plan.ServiceId };
            var it = RenewalSchedule.Renewals(plan, horizonEnd).GetEnumerator();
            if (it.MoveNext()) queue.Enqueue(it, (it.Current.Due, it.Current.ServiceId, it.Current.Ordinal));
        }

        var balanceLeft = balance;
        var bonusLeft = bonus;
        while (queue.TryDequeue(out var it, out _))
        {
            var r = it.Current;
            var sf = result.PerService[r.ServiceId];
            sf.Renewals++;
            sf.PlannedMinor = checked(sf.PlannedMinor + r.Amount);
            sf.FirstDue ??= r.Due;
            var day = DateOnly.FromDateTime(r.Due.UtcDateTime);
            sf.ByDay[day] = sf.ByDay.TryGetValue(day, out var d) ? (checked(d.Amount + r.Amount), d.Count + 1) : (r.Amount, 1);

            var fromBalance = Math.Min(balanceLeft, r.Amount);
            balanceLeft -= fromBalance;
            var fromBonus = Math.Min(bonusLeft, r.Amount - fromBalance);
            bonusLeft -= fromBonus;
            var covered = fromBalance + fromBonus;
            sf.CoveredMinor = checked(sf.CoveredMinor + covered);
            result.BalanceUsedMinor += fromBalance;
            result.BonusUsedMinor += fromBonus;
            if (covered < r.Amount)
            {
                if (sf.FirstUnfunded is null) { sf.FirstUnfunded = r; sf.FirstUnfundedCoveredMinor = covered; }
                result.FirstUnfundedDue ??= r.Due;
            }

            if (it.MoveNext()) queue.Enqueue(it, (it.Current.Due, it.Current.ServiceId, it.Current.Ordinal));
        }
        return result;
    }
}
