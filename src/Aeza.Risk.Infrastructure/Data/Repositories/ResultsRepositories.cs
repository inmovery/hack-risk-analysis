using System.Globalization;

using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Generations;
using Aeza.Risk.SharedKernel.Json;

using Microsoft.EntityFrameworkCore;

namespace Aeza.Risk.Infrastructure.Data.Repositories;

/// <inheritdoc />
public sealed class GenerationReader(AppDbContext db, TimeProvider time) : IGenerationReader
{
    /// <inheritdoc />
    public async Task<GenerationLookup> ResolveAsync(long? generationId, CancellationToken cancellationToken)
    {
        var state = await db.AppState.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var id = generationId ?? state?.ActiveGenerationId;
        if (id is null) return new GenerationLookup(null, GenerationLookupError.NoGeneration);
        var gen = await db.Generations.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (gen is null)
            return new GenerationLookup(null, generationId is not null ? GenerationLookupError.Gone : GenerationLookupError.ActiveMissing);
        var snap = await db.SourceSnapshots.AsNoTracking().Where(s => s.Id == gen.SourceSnapshotId)
            .Select(s => new { s.SourceAsOf, s.SourceAsOfAssumed, s.Synthetic }).SingleAsync(cancellationToken);
        var summary = AppJson.Deserialize<GenerationSummary>(gen.SummaryJson);
        var now = time.GetUtcNow();
        // Свежесть — по последнему изменению источника (событие или снимок), а не только по моменту выгрузки снимка
        var updated = gen.SourceUpdatedAt is { } u && u > snap.SourceAsOf ? u : snap.SourceAsOf;
        var info = new GenerationInfo(gen.Id, gen.SourceRevision, gen.ModelRevisionId, gen.AsOf, snap.SourceAsOf, snap.SourceAsOfAssumed,
            gen.CreatedAt, gen.ClockMode, gen.EngineVersion, snap.Synthetic || gen.SourceSynthetic, (long)Math.Max(0, (now - updated).TotalSeconds),
            summary.Money.HorizonDays, gen.SourceSeq, updated, updated > snap.SourceAsOf ? "stream" : "snapshot", gen.ActivitySeq, gen.MlModelId);
        var pending = await db.RecalcJobs.AsNoTracking().Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running)
            .OrderByDescending(j => j.Id).Select(j => new PendingInfo(j.Id, j.Status, j.Reason, j.ModelRevisionId, null, j.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);
        var lastJob = await db.RecalcJobs.AsNoTracking().Where(j => j.Status == JobStatus.Failed || j.Status == JobStatus.Succeeded)
            .OrderByDescending(j => j.Id).Select(j => new { j.Id, j.Status, j.ErrorCode, j.FinishedAt, j.ModelRevisionId })
            .FirstOrDefaultAsync(cancellationToken);
        var failure = lastJob is { Status: JobStatus.Failed } ? new LastFailureInfo(lastJob.Id, lastJob.ErrorCode, lastJob.FinishedAt, lastJob.ModelRevisionId, null) : null;
        return new GenerationLookup(new GenerationContext(gen, info, pending, failure), GenerationLookupError.None);
    }
}

/// <inheritdoc />
public sealed class CustomerResultsRepository(AppDbContext db) : ICustomerResultsRepository
{
    /// <inheritdoc />
    public async Task<CustomerListPage> ListAsync(long generationId, CustomerListCriteria criteria, CancellationToken cancellationToken)
    {
        var q = db.CustomerResults.AsNoTracking().Where(r => r.GenerationId == generationId);
        if (criteria.RiskLevels is { Count: > 0 } levels) q = q.Where(r => levels.Contains(r.Level));
        if (criteria.ScoreState is { } state) q = q.Where(r => r.ScoreState == state);
        if (!string.IsNullOrWhiteSpace(criteria.Flag)) q = q.Where(r => r.Flags.Contains(criteria.Flag));
        if (criteria.FullChurnLevels is { Count: > 0 } fl) q = q.Where(r => fl.Contains(r.FullChurnLevel));
        if (criteria.FullChurnState is { } fs) q = q.Where(r => r.FullChurnState == fs);
        if (criteria.PaymentTrends is { Count: > 0 } pt) q = q.Where(r => pt.Contains(r.PaymentTrend));
        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var term = criteria.Search.Trim().ToLowerInvariant();
            q = long.TryParse(term, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idTerm)
                ? q.Where(r => r.CustomerId == idTerm || r.EmailSort.Contains(term))
                : q.Where(r => r.EmailSort.Contains(term));
        }

        var total = await q.CountAsync(cancellationToken);
        var asc = criteria.Ascending;
        // «Нет оценки» и пустые балансы — всегда в конце; равенства — по id ↑
        IOrderedQueryable<CustomerResultEntity> ordered = criteria.SortBy switch
        {
            "id" => asc ? q.OrderBy(r => r.CustomerId) : q.OrderByDescending(r => r.CustomerId),
            "email" => asc ? q.OrderBy(r => r.EmailSort).ThenBy(r => r.Email) : q.OrderByDescending(r => r.EmailSort).ThenByDescending(r => r.Email),
            "balance" => asc ? q.OrderBy(r => r.Balance == null).ThenBy(r => r.Balance) : q.OrderBy(r => r.Balance == null).ThenByDescending(r => r.Balance),
            "totalReplenished" => asc
                ? q.OrderBy(r => r.TotalReplenished == null).ThenBy(r => r.TotalReplenished)
                : q.OrderBy(r => r.TotalReplenished == null).ThenByDescending(r => r.TotalReplenished),
            "atRisk" => asc ? q.OrderBy(r => r.Score == null).ThenBy(r => r.AtRiskMinor) : q.OrderBy(r => r.Score == null).ThenByDescending(r => r.AtRiskMinor),
            // Индикаторы оттока: неизвестные (null) — всегда в конце, как «нет оценки»
            "fullChurnRisk" => asc
                ? q.OrderBy(r => r.FullChurnScore == null).ThenBy(r => r.FullChurnScore)
                : q.OrderBy(r => r.FullChurnScore == null).ThenByDescending(r => r.FullChurnScore),
            "paymentChange" => asc
                ? q.OrderBy(r => r.PaymentChangePct == null).ThenBy(r => r.PaymentChangePct)
                : q.OrderBy(r => r.PaymentChangePct == null).ThenByDescending(r => r.PaymentChangePct),
            _ => asc ? q.OrderBy(r => r.Score == null).ThenBy(r => r.Score) : q.OrderBy(r => r.Score == null).ThenByDescending(r => r.Score),
        };
        if (criteria.SortBy != "id") ordered = ordered.ThenBy(r => r.CustomerId);

        var rows = await ordered.Skip((criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize).ToListAsync(cancellationToken);
        return new CustomerListPage(total, rows);
    }

    /// <inheritdoc />
    public Task<string?> GetDetailJsonAsync(long generationId, long customerId, CancellationToken cancellationToken) =>
        db.CustomerResults.AsNoTracking().Where(r => r.GenerationId == generationId && r.CustomerId == customerId)
            .Select(r => r.DetailJson).SingleOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, CustomerScoreRow>> GetScoresAsync(long generationId, CancellationToken cancellationToken) =>
        await db.CustomerResults.AsNoTracking().Where(r => r.GenerationId == generationId)
            .Select(r => new CustomerScoreRow(r.CustomerId, r.Email, r.Score, r.Level, r.AtRiskMinor))
            .ToDictionaryAsync(r => r.CustomerId, cancellationToken);
}

/// <inheritdoc />
public sealed class AnalyticsRepository(AppDbContext db) : IAnalyticsRepository
{
    /// <inheritdoc />
    public async Task<ObservedHistory> GetHistoryAsync(long upToSourceRevision, CancellationToken cancellationToken)
    {
        var snapshots = await db.SourceSnapshots.CountAsync(s => s.Revision <= upToSourceRevision, cancellationToken);
        var events = db.ObservedServiceEvents.AsNoTracking().Where(e => e.SourceRevision <= upToSourceRevision);
        var total = await events.CountAsync(cancellationToken);
        var deleted = await events.Where(e => e.Kind == "status_changed" && e.ToStatus == "deleted")
            .GroupBy(e => e.ObservedTo.Date).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var recent = await events.OrderByDescending(e => e.Id).Take(20)
            .Select(e => new ObservedEventDto(e.ServiceId, e.OwnerId, e.Kind, e.FromStatus, e.ToStatus, e.ObservedFrom, e.ObservedTo, e.SourceRevision))
            .ToListAsync(cancellationToken);
        return new ObservedHistory(snapshots, total,
            deleted.OrderBy(d => d.Key).ToDictionary(d => d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d => d.Count),
            recent);
    }

    /// <inheritdoc />
    public async Task<ObservationCounts> GetObservationCountsAsync(CancellationToken cancellationToken)
    {
        var snapshots = await db.SourceSnapshots.CountAsync(cancellationToken);
        var changes = await db.ObservedServiceEvents.CountAsync(e => e.Kind == "status_changed", cancellationToken);
        var deletions = await db.ObservedServiceEvents.CountAsync(e => e.Kind == "status_changed" && e.ToStatus == "deleted", cancellationToken);
        return new ObservationCounts(snapshots, changes, deletions);
    }
}
