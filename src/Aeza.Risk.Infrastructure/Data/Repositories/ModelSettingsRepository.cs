using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Settings;
using Aeza.Risk.SharedKernel.Json;

using Microsoft.EntityFrameworkCore;

namespace Aeza.Risk.Infrastructure.Data.Repositories;

/// <inheritdoc />
public sealed class ModelSettingsRepository(AppDbContext db, TimeProvider time) : IModelSettingsRepository
{
    /// <inheritdoc />
    public async Task<ModelStateView> GetStateAsync(CancellationToken cancellationToken)
    {
        var state = await db.AppState.AsNoTracking().SingleAsync(cancellationToken);
        var latest = await db.ModelRevisions.AsNoTracking().SingleAsync(r => r.Id == state.LatestModelRevisionId, cancellationToken);
        var active = state.ActiveGenerationId is { } gid
            ? await db.Generations.AsNoTracking().SingleAsync(x => x.Id == gid, cancellationToken)
            : null;
        var pending = await db.RecalcJobs.AnyAsync(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running, cancellationToken);
        return new ModelStateView(latest, active, pending);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelRevisionEntity>> ListRevisionsAsync(int take, CancellationToken cancellationToken) =>
        await db.ModelRevisions.AsNoTracking().OrderByDescending(r => r.Id).Take(take).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<ModelAppendResult> AppendAsync(long baseRevision, Func<Core.ModelSettings, ModelChangeDecision> decide, string actor,
        CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var state = await db.AppState.FromSqlRaw("SELECT * FROM app_state WHERE id = 1 FOR UPDATE").SingleAsync(cancellationToken);
        var latest = await db.ModelRevisions.AsNoTracking().SingleAsync(r => r.Id == state.LatestModelRevisionId, cancellationToken);
        if (baseRevision != latest.Id)
            return new ModelAppendResult(ModelAppendOutcome.Conflict, latest, null, null, null);

        var current = AppJson.Deserialize<ModelSettingsDto>(latest.SettingsJson).ToModel();
        switch (decide(current))
        {
            case ModelChangeDecision.Invalid invalid:
                return new ModelAppendResult(ModelAppendOutcome.Invalid, latest, null, null, invalid.Errors);
            case ModelChangeDecision.Change change:
                var now = time.GetUtcNow();
                var merged = ModelSettingsDto.From(change.Merged);
                var rev = new ModelRevisionEntity
                {
                    Id = latest.Id + 1,
                    SettingsJson = AppJson.Serialize(merged),
                    SchemaVersion = merged.SchemaVersion,
                    CreatedAt = now,
                    CreatedBy = actor,
                    Comment = change.Comment,
                    BasedOn = latest.Id,
                };
                db.ModelRevisions.Add(rev);
                state.LatestModelRevisionId = rev.Id;
                var job = RecalcQueue.Enqueue(db, "model", now, actor, modelRevisionId: rev.Id);
                db.Audit.Add(new AuditEntry
                {
                    At = now,
                    Actor = actor,
                    Action = "model_revision",
                    Details = AppJson.Serialize(new { revision = rev.Id, basedOn = latest.Id, before = ModelSettingsDto.From(current), after = merged }),
                });
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return new ModelAppendResult(ModelAppendOutcome.Accepted, latest, rev.Id, job.Id, null);
            default:
                return new ModelAppendResult(ModelAppendOutcome.Unchanged, latest, null, null, null);
        }
    }
}
