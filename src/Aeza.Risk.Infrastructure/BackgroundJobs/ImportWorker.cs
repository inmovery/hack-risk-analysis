using System.Text;
using System.Text.Json;

using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Live;
using Aeza.Risk.Core.Options;
using Aeza.Risk.Core.Source;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.Infrastructure.Data.Repositories;
using Aeza.Risk.Infrastructure.Source;
using Aeza.Risk.SharedKernel.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace Aeza.Risk.Infrastructure.BackgroundJobs;

/// <summary>Сигнал «появился импорт» (in-process; durable часть — строка source_imports)</summary>
public sealed class ImportSignal : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

    public void Notify() => _signal.Release();

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _signal.WaitAsync(timeout, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // таймаут
        }
        while (_signal.CurrentCount > 0) _signal.Wait(0);
    }

    public void Dispose() => _signal.Dispose();
}

/// <summary>Исполнитель пакетного импорта: берёт импорт (SKIP LOCKED + аренда</summary>
public sealed class ImportWorker(
    IServiceScopeFactory scopes,
    NpgsqlDataSource dataSource,
    ImportSignal signal,
    IRecalcSignal recalc,
    ILiveNotifier live,
    TimeProvider time,
    IOptions<RecalcOptions> options,
    ILogger<ImportWorker> log) : BackgroundService
{
    private const int ParseBatch = 1000;
    private const int MaxErrors = 20;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ct = stoppingToken;
        if (!options.Value.Enabled) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await RunOnceAsync(ct)) await signal.WaitAsync(TimeSpan.FromSeconds(options.Value.PollSeconds), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Import loop error");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    /// <summary>Выполнить один кусок любого импорта; true — работа была</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        long importId;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using var claim = new NpgsqlCommand(@"
WITH next AS (
  SELECT id FROM source_imports
  WHERE status = 'pending' OR (status = 'running' AND lease_until < @now)
  ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED)
UPDATE source_imports i SET status = 'running', lease_until = @lease, started_at = COALESCE(i.started_at, @now)
FROM next WHERE i.id = next.id RETURNING i.id", conn, tx);
            claim.Parameters.AddWithValue("now", now);
            claim.Parameters.AddWithValue("lease", now + Lease);
            if (await claim.ExecuteScalarAsync(ct) is not long id)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            importId = id;
            await tx.CommitAsync(ct);
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ingestion = scope.ServiceProvider.GetRequiredService<ISourceIngestion>();
        var import = await db.SourceImports.AsNoTracking().SingleAsync(i => i.Id == importId, ct);
        try
        {
            // Импорт взят: куски по порядку, аренда продлевается после каждого (другая реплика его не возьмёт, пока эта жива)
            while (!ct.IsCancellationRequested)
            {
                var chunk = await db.SourceImportChunks.AsNoTracking().Where(c => c.ImportId == importId && !c.Done).OrderBy(c => c.No).FirstOrDefaultAsync(ct);
                if (chunk is null)
                {
                    await FinishAsync(db, import.Id, import.Actor, ct);
                    return true;
                }
                var (items, rejected, errors) = Parse(ImportStore.Unpack(chunk.Data), chunk.FirstLine, time.GetUtcNow());
                await ingestion.IngestAsync(items,
                    new IngestMeta(SourceCodes.OriginPush, import.Actor, import.Synthetic, import.Connector, rejected, errors.FirstOrDefault(), DeferRecalc: true,
                        new ImportChunkRef(import.Id, chunk.No)), ct);
                if (errors.Count > 0) await AppendErrorsAsync(db, import.Id, errors, ct);
                await db.SourceImports.Where(i => i.Id == import.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.LeaseUntil, time.GetUtcNow() + Lease), ct);
                if (chunk.No % 20 == 0 || chunk.No == import.ChunksTotal - 1)
                    log.LogInformation("Import {ImportId}: chunk {No}/{Total} ({Lines} lines, rejected {Rejected})", import.Id, chunk.No + 1, import.ChunksTotal, chunk.Lines, rejected);
                db.ChangeTracker.Clear();
            }
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var attempts = import.Attempts + 1;
            var failed = attempts >= options.Value.MaxAttempts;
            await db.SourceImports.Where(i => i.Id == import.Id).ExecuteUpdateAsync(u => u
                .SetProperty(i => i.Attempts, attempts)
                .SetProperty(i => i.Status, failed ? ImportStatus.Failed : ImportStatus.Pending)
                .SetProperty(i => i.ErrorCode, ex is DbUpdateException or NpgsqlException ? "database_error" : "import_error")
                .SetProperty(i => i.FinishedAt, failed ? time.GetUtcNow() : null)
                .SetProperty(i => i.LeaseUntil, (DateTimeOffset?)null), CancellationToken.None);
            log.LogError(ex, "Import {ImportId} chunk failed (attempt {Attempt}); {Outcome}", import.Id, attempts, failed ? "import failed" : "will retry");
            return true;
        }
    }

    /// <summary>Строки куска → события; неразборчивые строки — отклонены с номером строки (путь ошибки без значений)</summary>
    internal static (List<IngestItem> Items, int Rejected, List<string> Errors) Parse(IReadOnlyList<string> lines, long firstLine, DateTimeOffset now)
    {
        var items = new List<IngestItem>(lines.Count);
        var errors = new List<string>();
        var rejected = 0;
        var json = new List<(int Index, string Line)>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (ImportStore.IsJsonObject(lines[i])) json.Add((i, lines[i]));
            else
            {
                rejected++;
                if (errors.Count < MaxErrors) errors.Add($"line {firstLine + i}: invalid_json");
            }
        }
        foreach (var slice in json.Chunk(ParseBatch))
        {
            var body = Encoding.UTF8.GetBytes("{\"events\":[" + string.Join(',', slice.Select(s => s.Line)) + "]}");
            var batch = EventBatchParser.Parse(body, now);
            foreach (var e in batch.Events)
            {
                var index = slice[e.Index].Index;
                if (e.IsValid) items.Add(new IngestItem(index, e.Domain, e.Changes));
                else
                {
                    rejected++;
                    if (errors.Count < MaxErrors) errors.Add($"line {firstLine + index}: {string.Join(", ", e.Errors.Keys.Select(k => k.Replace($"events[{e.Index}]", "", StringComparison.Ordinal).TrimStart('.')))}");
                }
            }
        }
        return (items, rejected, errors);
    }

    private static async Task AppendErrorsAsync(AppDbContext db, long importId, List<string> errors, CancellationToken ct)
    {
        var current = await db.SourceImports.Where(i => i.Id == importId).Select(i => i.Errors).SingleAsync(ct);
        var list = current is null ? [] : JsonSerializer.Deserialize<List<string>>(current) ?? [];
        if (list.Count >= MaxErrors) return;
        list.AddRange(errors.Take(MaxErrors - list.Count));
        var json = JsonSerializer.Serialize(list);
        await db.SourceImports.Where(i => i.Id == importId).ExecuteUpdateAsync(u => u.SetProperty(i => i.Errors, json), ct);
    }

    /// <summary>Все куски применены: импорт выполнен, один пересчёт на весь импорт</summary>
    private async Task FinishAsync(AppDbContext db, long importId, string actor, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var import = await db.SourceImports.SingleAsync(i => i.Id == importId, ct);
        var job = RecalcQueue.Enqueue(db, "import", now, actor);
        await db.SaveChangesAsync(ct);
        import.Status = ImportStatus.Succeeded;
        import.FinishedAt = now;
        import.LeaseUntil = null;
        import.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        await db.SourceImportChunks.Where(c => c.ImportId == importId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        recalc.Notify();
        log.LogInformation("Import {ImportId} finished: {Events} events, applied {Applied}, recorded {Recorded}, duplicates {Duplicates}, stale {Stale}, rejected {Rejected}; job {JobId}",
            import.Id, import.EventsTotal, import.Applied, import.Recorded, import.Duplicates, import.Stale, import.Rejected, job.Id);
        await live.JobQueuedAsync(new JobQueuedEvent(job.Id, "import", null, null), ct);
        await live.SourceChangedAsync(new SourceChangedEvent(await db.AppState.Select(s => s.SourceSeq).SingleAsync(ct), (int)Math.Min(int.MaxValue, import.Applied),
            (int)Math.Min(int.MaxValue, import.EventsTotal), new Dictionary<string, int>(), import.Synthetic, "import"), ct);
    }
}
