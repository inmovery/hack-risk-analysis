using System.Data;
using System.Diagnostics;

using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Generations;
using Aeza.Risk.Core.Live;
using Aeza.Risk.Core.Options;
using Aeza.Risk.Core.Settings;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.Infrastructure.Data.Repositories;
using Aeza.Risk.Infrastructure.Metrics;
using Aeza.Risk.Infrastructure.Ml;
using Aeza.Risk.Infrastructure.Source;
using Aeza.Risk.SharedKernel.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using NpgsqlTypes;

namespace Aeza.Risk.Infrastructure.BackgroundJobs;

/// <summary>Единственный лидер (PostgreSQL advisory lock) исполняет durable jobs: коалесцирует ожидающие, считает поколение чистым ядром и атомарно публикует его</summary>
public sealed class RecalcWorker(
    IServiceScopeFactory scopes,
    NpgsqlDataSource dataSource,
    RecalcSignal signal,
    TimeProvider time,
    ILiveNotifier live,
    RiskEngine engine,
    IOptions<RecalcOptions> options,
    IOptions<StreamOptions> stream,
    BusinessMetricsState metrics,
    OnnxShadowRuntime shadow,
    ILogger<RecalcWorker> log) : BackgroundService
{
    private const long AdvisoryLockKey = 727_001;
    private readonly RecalcOptions _opt = options.Value;
    private NpgsqlConnection? _lockConnection;
    private DateTimeOffset _lastClockCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ct = stoppingToken;
        if (!_opt.Enabled)
        {
            log.LogWarning("Recalc worker disabled by configuration");
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await EnsureLeaderAsync(ct))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
                var worked = await RunOnceAsync(ct);
                if (!worked)
                {
                    await MaybeEnqueueClockJobAsync(ct);
                    await signal.WaitAsync(TimeSpan.FromSeconds(_opt.PollSeconds), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Recalc loop error; leadership will be re-checked");
                await DropLeadershipAsync();
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
        await DropLeadershipAsync();
    }

    private async Task<bool> EnsureLeaderAsync(CancellationToken ct)
    {
        if (_lockConnection is { State: ConnectionState.Open }) return true;
        await DropLeadershipAsync();
        var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn))
        {
            cmd.Parameters.AddWithValue("k", AdvisoryLockKey);
            if (await cmd.ExecuteScalarAsync(ct) is not true)
            {
                await conn.DisposeAsync();
                return false;
            }
        }
        _lockConnection = conn;
        log.LogInformation("Recalc worker became leader");
        await RecoverAsync(ct);
        return true;
    }

    private async Task DropLeadershipAsync()
    {
        if (_lockConnection is null) return;
        try
        {
            await _lockConnection.DisposeAsync();
        }
        catch (NpgsqlException)
        {
            // соединение уже разорвано
        }
        _lockConnection = null;
    }

    /// <summary>Лидер один: всё, что «running», осталось от упавшего процесса → снова pending</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var orphaned = await db.RecalcJobs.Where(j => j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Pending).SetProperty(j => j.AvailableAt, now).SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null), ct);
        if (orphaned > 0) log.LogWarning("Recovered {Count} orphaned running jobs", orphaned);

        var state = await db.AppState.SingleOrDefaultAsync(ct);
        if (state?.LatestSourceSnapshotId is null) return;
        var active = state.ActiveGenerationId is { } gid ? await db.Generations.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gid, ct) : null;
        var hasPending = await db.RecalcJobs.AnyAsync(j => j.Status == JobStatus.Pending, ct);
        if (!hasPending && (active is null || active.EngineVersion != RiskEngine.Version))
        {
            RecalcQueue.Enqueue(db, "startup", now, "system");
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task MaybeEnqueueClockJobAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (now - _lastClockCheck < TimeSpan.FromSeconds(_opt.ClockCheckSeconds)) return;
        _lastClockCheck = now;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await db.AppState.AsNoTracking().SingleOrDefaultAsync(ct);
        await MaintainFactsAsync(db, state, now, ct);
        if (state?.ActiveGenerationId is not { } gid || state.LatestModelRevisionId is not { } mid) return;
        var rev = await db.ModelRevisions.AsNoTracking().SingleAsync(m => m.Id == mid, ct);
        var settings = AppJson.Deserialize<ModelSettingsDto>(rev.SettingsJson).ToModel();
        if (settings.Clock.Mode != ClockMode.Live) return;
        var active = await db.Generations.AsNoTracking().SingleAsync(g => g.Id == gid, ct);
        if (now - active.AsOf < TimeSpan.FromMinutes(settings.Clock.TickMinutes)) return;
        if (await db.RecalcJobs.AnyAsync(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running, ct)) return;
        // Идёт импорт истории: промежуточный расчёт по части истории не нужен — пересчёт будет после импорта
        if (await db.SourceImports.AnyAsync(i => i.Status == ImportStatus.Uploading || i.Status == ImportStatus.Pending || i.Status == ImportStatus.Running, ct)) return;
        RecalcQueue.Enqueue(db, "clock", now, "system");
        await db.SaveChangesAsync(ct);
        log.LogInformation("Clock tick: enqueued time-only recalculation");
    }

    /// <summary>Исходы созревших сроков (только когда источник полон после E + N: водяной знак или свежесть) и retention журналов</summary>
    private async Task MaintainFactsAsync(AppDbContext db, AppStateEntity? state, DateTimeOffset now, CancellationToken ct)
    {
        var grace = await SourceIngestion.GraceDaysAsync(db, ct);
        var completeUpTo = await SourceIngestion.CompleteUpToAsync(db, state?.SourceUpdatedAt, ct);
        var resolved = await ExpiryStore.ResolveDueAsync(db, now, completeUpTo, grace, ct);
        if (resolved > 0) log.LogInformation("Resolved {Count} matured expiry outcomes", resolved);
        if (now - _lastRetention < TimeSpan.FromHours(1)) return;
        _lastRetention = now;
        var cutoff = now.AddDays(-stream.Value.RetentionDays);
        var removed = await db.SourceEvents.Where(e => e.ReceivedAt < cutoff).ExecuteDeleteAsync(ct)
                      + await db.SourceDomainEvents.Where(e => e.ReceivedAt < cutoff).ExecuteDeleteAsync(ct);
        var cutoffDay = DateOnly.FromDateTime(cutoff.UtcDateTime);
        removed += await db.CustomerActivity.Where(a => a.Day < cutoffDay).ExecuteDeleteAsync(ct);
        if (removed > 0) log.LogInformation("Journal retention removed {Count} source events older than {Days} days", removed, stream.Value.RetentionDays);
        // Служебные таблицы входа: истёкшие сессии (через сутки) и забытые счётчики неудачных входов
        var sessions = await db.Sessions.Where(s => s.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        var throttles = await db.LoginThrottle.Where(t => t.UpdatedAt < now.AddDays(-1) && (t.LockedUntil == null || t.LockedUntil < now)).ExecuteDeleteAsync(ct);
        if (sessions + throttles > 0) log.LogInformation("Removed {Sessions} expired sessions and {Throttles} stale login counters", sessions, throttles);
    }

    /// <summary>Забирает и исполняет один job. true — работа была</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        long jobId;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using var claim = new NpgsqlCommand(@"
WITH next AS (
  SELECT id FROM recalc_jobs
  WHERE (status = 'pending' AND available_at <= @now) OR (status = 'running' AND lease_until < @now)
  ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED)
UPDATE recalc_jobs j SET status = 'running', attempts = j.attempts + 1, started_at = @now, lease_until = @lease
FROM next WHERE j.id = next.id RETURNING j.id", conn, tx);
            claim.Parameters.AddWithValue("now", now);
            claim.Parameters.AddWithValue("lease", now.AddSeconds(_opt.LeaseSeconds));
            if (await claim.ExecuteScalarAsync(ct) is not long id) { await tx.RollbackAsync(ct); return false; }
            jobId = id;
            // Все ожидающие jobs удовлетворяются этим запуском: он читает последние принятые входы после commit.
            await using var coalesce = new NpgsqlCommand(
                "UPDATE recalc_jobs SET status = 'coalesced', coalesced_into = @id, finished_at = @now WHERE status = 'pending' AND id <> @id", conn, tx);
            coalesce.Parameters.AddWithValue("id", jobId);
            coalesce.Parameters.AddWithValue("now", now);
            await coalesce.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.RecalcJobs.SingleAsync(j => j.Id == jobId, ct);
        try
        {
            await ExecuteJobAsync(db, job, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            var failed = await db.RecalcJobs.SingleAsync(j => j.Id == jobId, ct);
            var code = ex is OverflowException ? "money_overflow" : ex is DbUpdateException or NpgsqlException ? "database_error" : "engine_error";
            failed.ErrorCode = code;
            failed.ErrorMessage = ex.GetType().Name; // без текста исключения: в нём могут оказаться данные
            failed.LeaseUntil = null;
            if (failed.Attempts >= _opt.MaxAttempts)
            {
                failed.Status = JobStatus.Failed;
                failed.FinishedAt = time.GetUtcNow();
                log.LogError(ex, "Recalc job {JobId} failed permanently ({Code}) after {Attempts} attempts; last-good generation kept", jobId, code, failed.Attempts);
            }
            else
            {
                failed.Status = JobStatus.Pending;
                failed.AvailableAt = time.GetUtcNow().AddSeconds(Math.Pow(2, failed.Attempts));
                log.LogWarning(ex, "Recalc job {JobId} attempt {Attempt} failed ({Code}); will retry", jobId, failed.Attempts, code);
            }
            await db.SaveChangesAsync(ct);
            if (failed.Status == JobStatus.Failed)
                await live.RecalcFailedAsync(new RecalcFailedEvent(jobId, code, failed.ModelRevisionId, null), ct);
        }
        return true;
    }

    private async Task ExecuteJobAsync(AppDbContext db, RecalcJob job, CancellationToken ct)
    {
        // Согласованный срез входов: указатели app_state, текущее состояние cur_*, ревизия модели и активность клиентов читаются в одной транзакции REPEATABLE READ
        AppStateEntity? state;
        SourceData? data = null;
        var syntheticEvents = false;
        BehaviorInput behavior = new(new Dictionary<long, AccountBehavior>(), 0);
        ModelRevisionEntity? model = null;
        ModelSettings? settings = null;
        var now = time.GetUtcNow();
        var asOf = now;
        (long Id, long Revision, DateTimeOffset SourceAsOf)? snapshot = null;
        await using (var read = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
        {
            state = await db.AppState.AsNoTracking().SingleOrDefaultAsync(ct);
            if (state?.LatestSourceSnapshotId is { } baseId && state.LatestModelRevisionId is { } mid)
            {
                data = await CurrentStateStore.LoadAllAsync(db, ct);
                var b = await db.SourceSnapshots.AsNoTracking().Where(s => s.Id == baseId).Select(s => new { s.Id, s.Revision, s.SourceAsOf }).SingleAsync(ct);
                snapshot = (b.Id, b.Revision, b.SourceAsOf);
                var seq = state.SourceSeq;
                syntheticEvents = await db.SourceEvents.AnyAsync(e => e.OccurredAt > b.SourceAsOf && e.Seq <= seq && e.Synthetic && e.Outcome == "applied", ct);
                model = await db.ModelRevisions.AsNoTracking().SingleAsync(m => m.Id == mid, ct);
                settings = AppJson.Deserialize<ModelSettingsDto>(model.SettingsJson).ToModel();
                asOf = settings.Clock.Mode == ClockMode.Live
                    ? new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero)
                    : b.SourceAsOf;
                behavior = await BehaviorRepository.LoadAsync(db, asOf, ct);
            }
            await read.CommitAsync(ct);
        }
        if (state is null || snapshot is not { } snap || model is null || settings is null || data is null)
        {
            Finish(job, JobStatus.Succeeded, null);
            await db.SaveChangesAsync(ct);
            return;
        }

        var active = state.ActiveGenerationId is { } agid ? await db.Generations.AsNoTracking().SingleOrDefaultAsync(g => g.Id == agid, ct) : null;
        if (active is not null && active.SourceSnapshotId == snap.Id && active.SourceSeq == state.SourceSeq && active.ModelRevisionId == model.Id
            && active.AsOf == asOf && active.EngineVersion == RiskEngine.Version && active.ActivitySeq == behavior.ActivitySeq
            && active.MlRevision == state.MlRevision && active.MlModelId == state.ActiveMlModelId)
        {
            Finish(job, JobStatus.Succeeded, active.Id);
            await db.SaveChangesAsync(ct);
            return;
        }

        var sw = Stopwatch.StartNew();
        var result = engine.Evaluate(data, settings, asOf, behavior.Accounts);
        var txByCustomer = data.Transactions.Join(data.Services, t => t.ServiceId, s => s.Id, (t, s) => (s.OwnerId, t))
            .ToLookup(x => x.OwnerId, x => x.t);
        var ml = state.ActiveMlModelId is { } mlId ? await RunShadowAsync(db, mlId, result, data, behavior, asOf, ct) : null;
        var summary = GenerationPublishing.BuildSummary(result) with { MlShadow = ml?.Summary };

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var generation = new GenerationEntity
        {
            SourceSnapshotId = snap.Id, SourceRevision = snap.Revision, ModelRevisionId = model.Id, AsOf = asOf, ActivitySeq = behavior.ActivitySeq,
            ClockMode = settings.Clock.Mode == ClockMode.Live ? "live" : "source", EngineVersion = RiskEngine.Version,
            CreatedAt = now, JobId = job.Id, SourceSeq = state.SourceSeq, SourceUpdatedAt = state.SourceUpdatedAt, SourceSynthetic = syntheticEvents,
            MlModelId = state.ActiveMlModelId, MlRevision = state.MlRevision,
            SummaryJson = AppJson.Serialize(summary),
            ForecastJson = AppJson.Serialize(result.Forecast.Select(d => new ForecastPointDto(d.Date, d.PlannedMinor, d.AtRiskMinor, d.Renewals)).ToList()),
        };
        db.Generations.Add(generation);
        await db.SaveChangesAsync(ct);
        await WriteResultsAsync(db, generation.Id, result, txByCustomer, ml, ct);

        var appState = await db.AppState.FromSqlRaw("SELECT * FROM app_state WHERE id = 1 FOR UPDATE").SingleAsync(ct);
        var current = appState.ActiveGenerationId is { } cg ? await db.Generations.AsNoTracking().SingleOrDefaultAsync(g => g.Id == cg, ct) : null;
        var publish = GenerationPublishing.ShouldPublish(current, generation);
        if (publish) appState.ActiveGenerationId = generation.Id;
        // Прогнозы в точках решения копятся только при живых часах: момент оценки = сейчас, признаки не видят будущего
        if (publish && ml is { Summary.State: "scored" } && settings.Clock.Mode == ClockMode.Live)
            await SavePredictionsAsync(db, generation.Id, ml.Summary.ModelId, asOf, ml.Predictions, ct);
        generation.DurationMs = (int)sw.ElapsedMilliseconds;
        Finish(job, publish ? JobStatus.Succeeded : JobStatus.Superseded, generation.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (publish)
        {
            metrics.RequestRefresh(); // бизнес-метрики — сразу из нового поколения
            log.LogInformation("Published generation {GenerationId} (source r{SourceRevision} seq {SourceSeq}, model r{ModelRevision}, asOf {AsOf:o}, {Customers} customers, {Ms} ms, job {JobId} {Reason})",
                generation.Id, snap.Revision, state.SourceSeq, model.Id, asOf, result.Customers.Count, generation.DurationMs, job.Id, job.Reason);
            await live.GenerationPublishedAsync(new GenerationPublishedEvent(generation.Id, snap.Revision, state.SourceSeq, model.Id, asOf, job.Reason, now), ct);
        }
        else
        {
            log.LogWarning("Generation {GenerationId} superseded: active generation has newer inputs", generation.Id);
        }
        await CleanupAsync(db, ct);
    }

    private void Finish(RecalcJob job, string status, long? generationId)
    {
        job.Status = status;
        job.GenerationId = generationId;
        job.FinishedAt = time.GetUtcNow();
        job.LeaseUntil = null;
    }

    /// <summary>Теневая модель: те же признаки, что при обучении</summary>
    private async Task<MlShadowResult> RunShadowAsync(AppDbContext db, long modelId, EngineResult result, SourceData data, BehaviorInput behavior,
        DateTimeOffset asOf, CancellationToken ct)
    {
        var version = await db.MlModels.AsNoTracking().Where(m => m.Id == modelId).Select(m => m.Version).SingleOrDefaultAsync(ct) ?? "";
        var model = new MlModelRef(modelId, version);
        try
        {
            if (shadow.LoadedModelId != modelId)
            {
                var onnx = await db.MlModels.AsNoTracking().Where(m => m.Id == modelId).Select(m => m.Onnx).SingleAsync(ct);
                shadow.Load(modelId, onnx);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Shadow model {ModelId} failed to load; rules unaffected", modelId);
            return MlShadow.Run(model, result, data, behavior.Accounts, asOf, (_, _) => throw new InvalidOperationException("load_error"));
        }
        var timeout = TimeSpan.FromSeconds(_opt.MlShadowTimeoutSeconds);
        var run = MlShadow.Run(model, result, data, behavior.Accounts, asOf, (batch, rows) => shadow.Predict(batch, rows, timeout));
        if (run.Summary.State == "failed")
            log.LogWarning("Shadow model {ModelId} inference failed ({Code}); rules unaffected", modelId, run.Summary.ErrorCode);
        else
            log.LogInformation("Shadow model {ModelId}: {Services} services in scope, top-K overlap with rules {Overlap}/{K}, {Ms} ms",
                modelId, run.Summary.ServicesInScope, run.Summary.TopKOverlap, run.Summary.TopK, run.Summary.DurationMs);
        return run;
    }

    /// <summary>Первый прогноз после точки решения E − k для каждого срока: проверка модели на реальных исходах (expiry_outcomes)</summary>
    private static async Task SavePredictionsAsync(AppDbContext db, long generationId, long modelId, DateTimeOffset asOf, IReadOnlyList<MlShadowPrediction> predictions,
        CancellationToken ct)
    {
        var due = predictions.Where(p => p.OffsetDays > 0).ToList();
        if (due.Count == 0) return;
        await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO ml_shadow_predictions (model_id, service_id, expires_at, offset_days, account_id, predicted_at, generation_id, probability, rules_score)
SELECT @model, x.service_id, x.expires_at, x.offset_days, x.account_id, @at, @generation, x.probability, x.rules_score
FROM unnest(@services, @expires, @offsets, @accounts, @probabilities, @rules) AS x(service_id, expires_at, offset_days, account_id, probability, rules_score)
ON CONFLICT DO NOTHING",
            [
                new NpgsqlParameter("model", modelId), new NpgsqlParameter("at", asOf), new NpgsqlParameter("generation", generationId),
                new NpgsqlParameter("services", due.Select(p => p.ServiceId).ToArray()),
                new NpgsqlParameter("expires", due.Select(p => p.ExpiresAt).ToArray()) { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz },
                new NpgsqlParameter("offsets", due.Select(p => p.OffsetDays).ToArray()),
                new NpgsqlParameter("accounts", due.Select(p => p.AccountId).ToArray()),
                new NpgsqlParameter("probabilities", due.Select(p => p.Probability).ToArray()),
                new NpgsqlParameter("rules", due.Select(p => p.RulesScore).ToArray()) { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Numeric },
            ], ct);
    }

    private static async Task WriteResultsAsync(AppDbContext db, long generationId, EngineResult result, ILookup<long, TransactionInput> txByCustomer,
        MlShadowResult? ml, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var importer = await conn.BeginBinaryImportAsync(@"COPY customer_results (
generation_id, customer_id, email, email_sort, balance, total_replenished, withdraw_balance, bonus_balance, bonus_state,
score, level, score_state, primary_reason_code, primary_reason_text, primary_reason_contribution, primary_reason_service_id,
reason_codes, flags, planned_minor, unfunded_minor, at_risk_minor, services_total, services_renewable, services_scored,
full_churn_score, full_churn_level, full_churn_state, payment_trend, payment_change_pct, paid_current_minor, paid_previous_minor, detail_json) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var c in result.Customers)
        {
            var a = c.Account;
            var detail = CustomerMapping.ToDetail(c, txByCustomer[a.Id].ToList(), ml?.Customers.GetValueOrDefault(a.Id));
            await importer.StartRowAsync(ct);
            await importer.WriteAsync(generationId, NpgsqlDbType.Bigint, ct);
            await importer.WriteAsync(a.Id, NpgsqlDbType.Bigint, ct);
            await importer.WriteAsync(a.Email, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(a.Email.ToLowerInvariant(), NpgsqlDbType.Text, ct);
            await WriteNullable(importer, a.Balance, ct);
            await WriteNullable(importer, a.TotalReplenished, ct);
            await WriteNullable(importer, a.WithdrawBalance, ct);
            await WriteNullable(importer, a.BonusBalance, ct);
            if (a.BonusState is null) await importer.WriteNullAsync(ct);
            else await importer.WriteAsync(a.BonusState, NpgsqlDbType.Text, ct);
            if (c.Score is null) await importer.WriteNullAsync(ct);
            else await importer.WriteAsync(c.Score.Value, NpgsqlDbType.Numeric, ct);
            await importer.WriteAsync(c.Level.ToApi(), NpgsqlDbType.Text, ct);
            await importer.WriteAsync(c.State.ToApi(), NpgsqlDbType.Text, ct);
            await importer.WriteAsync(c.PrimaryReason.Code, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(c.PrimaryReason.Text, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(c.PrimaryReason.Contribution, NpgsqlDbType.Numeric, ct);
            await WriteNullable(importer, c.PrimaryReason.ServiceId, ct);
            await importer.WriteAsync(c.Reasons.Select(r => r.Code).Distinct().ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
            await importer.WriteAsync(c.Flags.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
            await importer.WriteAsync(c.Money.PlannedRenewalsMinor, NpgsqlDbType.Bigint, ct);
            await importer.WriteAsync(c.Money.UnfundedMinor, NpgsqlDbType.Bigint, ct);
            await importer.WriteAsync(c.Money.AtRiskMinor, NpgsqlDbType.Bigint, ct);
            await importer.WriteAsync(c.ServicesTotal, NpgsqlDbType.Integer, ct);
            await importer.WriteAsync(c.ServicesRenewable, NpgsqlDbType.Integer, ct);
            await importer.WriteAsync(c.ServicesScored, NpgsqlDbType.Integer, ct);
            if (c.FullChurn.Score is null) await importer.WriteNullAsync(ct);
            else await importer.WriteAsync(c.FullChurn.Score.Value, NpgsqlDbType.Numeric, ct);
            await importer.WriteAsync(c.FullChurn.Level.ToApi(), NpgsqlDbType.Text, ct);
            await importer.WriteAsync(c.FullChurn.State, NpgsqlDbType.Text, ct);
            await importer.WriteAsync(c.PaymentTrend.State, NpgsqlDbType.Text, ct);
            if (c.PaymentTrend.ChangePct is null) await importer.WriteNullAsync(ct);
            else await importer.WriteAsync(c.PaymentTrend.ChangePct.Value, NpgsqlDbType.Numeric, ct);
            await importer.WriteAsync(c.PaymentTrend.CurrentMinor, NpgsqlDbType.Bigint, ct);
            await importer.WriteAsync(c.PaymentTrend.PreviousMinor, NpgsqlDbType.Bigint, ct);
            await importer.WriteAsync(AppJson.Serialize(detail), NpgsqlDbType.Jsonb, ct);
        }
        await importer.CompleteAsync(ct);
    }

    private static async Task WriteNullable(NpgsqlBinaryImporter importer, long? value, CancellationToken ct)
    {
        if (value is null) await importer.WriteNullAsync(ct);
        else await importer.WriteAsync(value.Value, NpgsqlDbType.Bigint, ct);
    }

    /// <summary>Retention: последние N поколений + активное</summary>
    private async Task CleanupAsync(AppDbContext db, CancellationToken ct)
    {
        var activeId = await db.AppState.AsNoTracking().Select(s => s.ActiveGenerationId).SingleOrDefaultAsync(ct);
        var keep = await db.Generations.AsNoTracking().OrderByDescending(g => g.Id).Select(g => g.Id).Take(_opt.KeepGenerations).ToListAsync(ct);
        if (keep.Count < _opt.KeepGenerations) return;
        var min = keep.Min();
        var removed = await db.Generations.Where(g => g.Id < min && g.Id != activeId).ExecuteDeleteAsync(ct);
        if (removed > 0) log.LogDebug("Retention removed {Count} generations", removed);
    }
}
