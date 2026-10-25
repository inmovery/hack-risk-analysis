using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;

using Microsoft.EntityFrameworkCore;

namespace Aeza.Risk.Infrastructure.Data.Repositories;

/// <inheritdoc />
public sealed class BehaviorRepository(AppDbContext db) : IBehaviorRepository
{
    /// <inheritdoc />
    public Task<BehaviorInput> LoadAsync(DateTimeOffset asOf, CancellationToken cancellationToken) => LoadAsync(db, asOf, cancellationToken);

    /// <summary>Поведение на момент оценки: дни активности за <see cref="AccountBehavior.WindowDays"/> до даты оценки, первый день данных и последний вход</summary>
    public static async Task<BehaviorInput> LoadAsync(AppDbContext db, DateTimeOffset asOf, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(asOf.UtcDateTime);
        var from = today.AddDays(-(AccountBehavior.WindowDays - 1));
        var activitySeq = await db.SourceDomainEvents.MaxAsync(e => (long?)e.Seq, ct) ?? 0;
        var known = await db.CustomerActivity.AsNoTracking().Where(a => a.Day <= today)
            .GroupBy(a => a.AccountId).Select(g => new { AccountId = g.Key, First = g.Min(a => a.Day) }).ToListAsync(ct);
        if (known.Count == 0) return new BehaviorInput(new Dictionary<long, AccountBehavior>(), activitySeq);
        var lastLogin = await db.CustomerActivity.AsNoTracking().Where(a => a.Day <= today && a.Logins > 0)
            .GroupBy(a => a.AccountId).Select(g => new { AccountId = g.Key, Last = g.Max(a => a.Day) }).ToDictionaryAsync(x => x.AccountId, x => x.Last, ct);
        var rows = await db.CustomerActivity.AsNoTracking().Where(a => a.Day >= from && a.Day <= today).ToListAsync(ct);
        var days = rows.ToLookup(r => r.AccountId);
        var result = known.ToDictionary(k => k.AccountId, k => new AccountBehavior(
            k.First, lastLogin.TryGetValue(k.AccountId, out var l) ? l : null,
            days[k.AccountId].OrderBy(r => r.Day).Select(ToDay).ToList()));
        return new BehaviorInput(result, activitySeq);
    }

    /// <inheritdoc />
    public async Task<CustomerActivityView> GetCustomerAsync(long accountId, DateOnly from, DateOnly to, string? domain, long? beforeSeq, int limit,
        CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var first = await db.CustomerActivity.AsNoTracking().Where(a => a.AccountId == accountId).MinAsync(a => (DateOnly?)a.Day, ct);
        var days = await db.CustomerActivity.AsNoTracking().Where(a => a.AccountId == accountId && a.Day >= from && a.Day <= to)
            .OrderBy(a => a.Day).ToListAsync(ct);
        var q = db.SourceDomainEvents.AsNoTracking().Where(e => e.AccountId == accountId);
        if (beforeSeq is { } b) q = q.Where(e => e.Seq < b);
        q = Domain(q, domain);
        var page = await q.OrderByDescending(e => e.Seq).Take(limit + 1)
            .Select(e => new { e.Seq, e.OccurredAt, e.ReceivedAt, e.Type, e.ServiceId, e.Data, e.KnownType, e.Connector, e.Synthetic }).ToListAsync(ct);
        var items = page.Take(limit).Select(e => new DomainEventItem(e.Seq, e.OccurredAt, e.ReceivedAt, e.Type, DomainEventText.DomainOf(e.Type), e.ServiceId,
            DomainEventText.Describe(e.Type, e.Data), e.KnownType, e.Connector, e.Synthetic)).ToList();
        return new CustomerActivityView(first, days.Select(ToDay).ToList(), items, page.Count > limit ? items[^1].Seq : null);
    }

    private static IQueryable<SourceDomainEventEntity> Domain(IQueryable<SourceDomainEventEntity> q, string? domain) => domain switch
    {
        DomainEventText.Account => q.Where(e => e.Type.StartsWith("account.")),
        DomainEventText.Usage => q.Where(e => e.Type == "usage.daily" || e.Type == "service.incident"),
        DomainEventText.Service => q.Where(e => e.Type.StartsWith("service.") && e.Type != "service.incident"),
        DomainEventText.Money => q.Where(e => e.Type.StartsWith("balance.") || e.Type.StartsWith("payment.") || e.Type.StartsWith("bonus.")
                                              || e.Type.StartsWith("withdrawal.") || e.Type.StartsWith("renewal.") || e.Type.StartsWith("notification.")),
        DomainEventText.Support => q.Where(e => e.Type.StartsWith("ticket.") || e.Type.StartsWith("nps.")),
        DomainEventText.Retention => q.Where(e => e.Type.StartsWith("retention.")),
        _ => q,
    };

    private static ActivityDay ToDay(CustomerActivityEntity r) => new(r.Day, new ActivityDelta
    {
        Logins = r.Logins, LoginsFailed = r.LoginsFailed, UsageSamples = r.UsageSamples, CpuSum = r.CpuSum, TrafficOutGb = r.TrafficOutGb,
        IncidentMinutes = r.IncidentMinutes, TicketsOpened = r.TicketsOpened, TicketsUnsolved = r.TicketsUnsolved, CsatMin = r.CsatMin, NpsMin = r.NpsMin,
        PaymentsCount = r.PaymentsCount, PaymentsMinor = r.PaymentsMinor, PaymentsFailed = r.PaymentsFailed, TopUpsCount = r.TopUpsCount,
        TopUpsMinor = r.TopUpsMinor, Downgrades = r.Downgrades, Upgrades = r.Upgrades, AutoProlongOff = r.AutoProlongOff, Suspends = r.Suspends,
        WithdrawalsMinor = r.WithdrawalsMinor, Backups = r.Backups, Renewals = r.Renewals, Deletions = r.Deletions, BonusExpired = r.BonusExpired,
        Events = r.Events,
    });
}
