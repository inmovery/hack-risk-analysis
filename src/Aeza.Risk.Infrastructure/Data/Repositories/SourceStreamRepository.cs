using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Source;
using Aeza.Risk.SharedKernel.Json;

using Microsoft.EntityFrameworkCore;

namespace Aeza.Risk.Infrastructure.Data.Repositories;

/// <inheritdoc />
public sealed class SourceStreamRepository(AppDbContext db) : ISourceStreamRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<JournalItem>> ListAsync(long? beforeSeq, int limit, string? kind, string? outcome, long? ownerId, CancellationToken cancellationToken)
    {
        var q = db.SourceEvents.AsNoTracking();
        if (beforeSeq is { } b) q = q.Where(e => e.Seq < b);
        if (!string.IsNullOrEmpty(kind)) q = q.Where(e => e.Kind == kind);
        if (!string.IsNullOrEmpty(outcome)) q = q.Where(e => e.Outcome == outcome);
        if (ownerId is { } o) q = q.Where(e => e.OwnerId == o);
        var rows = await q.OrderByDescending(e => e.Seq).Take(limit)
            .Select(e => new { e.Seq, e.ReceivedAt, e.OccurredAt, e.EventType, e.Kind, e.EntityId, e.OwnerId, e.Outcome, e.StaleReason, e.Changes, e.Effect, e.Origin, e.Synthetic })
            .ToListAsync(cancellationToken);
        return rows.Select(e => new JournalItem(e.Seq, e.ReceivedAt, e.OccurredAt, e.EventType, e.Kind, e.EntityId, e.OwnerId, e.Outcome, e.StaleReason,
            e.Changes, AppJson.Deserialize<Dictionary<string, object?>>(e.Effect), e.Origin, e.Synthetic)).ToList();
    }

    /// <inheritdoc />
    public async Task<StreamCoverage> GetCoverageAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        // Журнал изменений записей и поведенческие события v2 (без state — только в журнале доменных событий)
        var behavior = db.SourceDomainEvents.AsNoTracking().Where(e => e.Outcome == SourceCodes.Recorded);
        var first = Min(await db.SourceEvents.AsNoTracking().OrderBy(e => e.Seq).Select(e => (DateTimeOffset?)e.ReceivedAt).FirstOrDefaultAsync(ct),
            await behavior.OrderBy(e => e.Seq).Select(e => (DateTimeOffset?)e.ReceivedAt).FirstOrDefaultAsync(ct));
        var last = Max(await db.SourceEvents.AsNoTracking().OrderByDescending(e => e.Seq).Select(e => (DateTimeOffset?)e.ReceivedAt).FirstOrDefaultAsync(ct),
            await behavior.OrderByDescending(e => e.Seq).Select(e => (DateTimeOffset?)e.ReceivedAt).FirstOrDefaultAsync(ct));
        var count = first is null ? 0 : await db.SourceEvents.LongCountAsync(ct) + await behavior.LongCountAsync(ct);
        var synthetic = first is not null && (await db.SourceEvents.AnyAsync(e => e.Synthetic, ct) || await behavior.AnyAsync(e => e.Synthetic, ct));
        var updated = await db.AppState.AsNoTracking().Select(s => s.SourceUpdatedAt).SingleOrDefaultAsync(ct);
        return new StreamCoverage(first, last, updated, count, synthetic);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HistoryDay>> GetDailyAsync(DateTimeOffset from, CancellationToken cancellationToken)
    {
        // Только применённые события: факт изменения состояния, по дню события (UTC): v2 — occurredAt, v1 — время получения
        var rows = await db.SourceEvents.AsNoTracking().Where(e => e.OccurredAt >= from && e.Outcome == SourceCodes.Applied)
            .Select(e => new { e.OccurredAt, e.Changes, e.Effect }).ToListAsync(cancellationToken);
        return rows.GroupBy(r => DateOnly.FromDateTime(r.OccurredAt.UtcDateTime)).OrderBy(g => g.Key).Select(g =>
        {
            int renewed = 0, toWait = 0, deleted = 0, blocked = 0, lost = 0, payments = 0, topUps = 0;
            long paymentsMinor = 0, topUpsMinor = 0;
            foreach (var r in g)
            {
                if (r.Changes.Contains(ChangeCodes.Renewed)) renewed++;
                if (r.Changes.Contains(ChangeCodes.ToProlongWait)) toWait++;
                if (r.Changes.Contains(ChangeCodes.Deleted)) deleted++;
                if (r.Changes.Contains(ChangeCodes.Blocked)) blocked++;
                if (r.Changes.Contains(ChangeCodes.CustomerLostAllServices)) lost++;
                if (r.Changes.Contains(ChangeCodes.PaymentPerformed))
                {
                    payments++;
                    paymentsMinor += ReadLong(r.Effect, "amount");
                }
                if (r.Changes.Contains(ChangeCodes.ToppedUp))
                {
                    topUps++;
                    topUpsMinor += ReadLong(r.Effect, "toppedUpMinor");
                }
            }
            return new HistoryDay(g.Key, renewed, toWait, deleted, blocked, lost, paymentsMinor, payments, topUpsMinor, topUps);
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<ExpiryOutcomeStats> GetOutcomeStatsAsync(int graceDays, CancellationToken cancellationToken)
    {
        var rows = await db.ExpiryOutcomes.AsNoTracking().GroupBy(o => new { o.Outcome, o.PaymentTerm })
            .Select(g => new { g.Key.Outcome, g.Key.PaymentTerm, Count = g.Count() }).ToListAsync(cancellationToken);
        int Sum(string? outcome) => rows.Where(r => r.Outcome == outcome).Sum(r => r.Count);
        var renewed = Sum(ExpiryOutcomes.Renewed);
        var notRenewed = Sum(ExpiryOutcomes.NotRenewed);
        var byTerm = rows.Where(r => r.Outcome is ExpiryOutcomes.Renewed or ExpiryOutcomes.NotRenewed)
            .GroupBy(r => r.PaymentTerm ?? "unknown")
            .ToDictionary(g => g.Key, g => new TermOutcomes(g.Where(x => x.Outcome == ExpiryOutcomes.Renewed).Sum(x => x.Count),
                g.Where(x => x.Outcome == ExpiryOutcomes.NotRenewed).Sum(x => x.Count)));
        decimal? rate = renewed + notRenewed > 0 ? Math.Round(notRenewed * 100m / (renewed + notRenewed), 1, MidpointRounding.ToEven) : null;
        return new ExpiryOutcomeStats(graceDays, renewed, notRenewed, Sum(ExpiryOutcomes.Excluded), Sum(null), byTerm, rate);
    }

    /// <inheritdoc />
    public async Task<StreamCounters> GetCountersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var since = now.AddHours(-24);
        var last24 = await db.SourceEvents.AsNoTracking().Where(e => e.ReceivedAt >= since).GroupBy(e => e.Outcome)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var baseAsOf = await db.AppState.AsNoTracking().Where(s => s.LatestSourceSnapshotId != null)
            .Join(db.SourceSnapshots, s => s.LatestSourceSnapshotId, x => x.Id, (s, x) => (DateTimeOffset?)x.SourceAsOf).SingleOrDefaultAsync(ct);
        var sinceBase = baseAsOf is { } b ? await db.SourceEvents.LongCountAsync(e => e.OccurredAt > b, ct) : 0;
        // «Парковка»: услуга пришла раньше аккаунта — хранится и ждёт события аккаунта, в расчёт не входит
        var orphans = db.CurServices.Where(s => !db.CurAccounts.Any(a => a.Id == s.OwnerId));
        var orphanServices = await orphans.CountAsync(ct);
        var oldestOrphan = orphanServices == 0 ? null : await orphans.MinAsync(s => (DateTimeOffset?)s.UpdatedAt, ct);
        var orphanTx = await db.CurTransactions.CountAsync(t => !db.CurServices.Any(s => s.Id == t.ServiceId), ct);
        var domain24 = db.SourceDomainEvents.AsNoTracking().Where(e => e.ReceivedAt >= since);
        var domainCount = await domain24.CountAsync(ct);
        var unknownTypes = domainCount == 0 ? 0 : await domain24.CountAsync(e => !e.KnownType, ct);
        var gaps = await db.SourceAccountStreams.CountAsync(s => s.Missing.Length > 0, ct);
        return new StreamCounters(last24, sinceBase, orphanServices, orphanTx, domainCount, unknownTypes, gaps, oldestOrphan);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectorState>> GetConnectorsAsync(CancellationToken cancellationToken) =>
        await db.SourceConnectors.AsNoTracking().OrderBy(c => c.Name)
            .Select(c => new ConnectorState(c.Name, c.Watermark, c.LastReceivedAt, c.LastOccurredAt, c.Events, c.Rejected, c.Duplicates, c.LastError, c.LastErrorAt))
            .ToListAsync(cancellationToken);

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static long ReadLong(string effectJson, string key)
    {
        var d = AppJson.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(effectJson);
        return d.TryGetValue(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    }
}
