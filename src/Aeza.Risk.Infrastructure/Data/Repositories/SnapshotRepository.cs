using System.IO.Compression;
using System.Security.Cryptography;

using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Source;
using Aeza.Risk.Infrastructure.Source;
using Aeza.Risk.SharedKernel.Json;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using NpgsqlTypes;

namespace Aeza.Risk.Infrastructure.Data.Repositories;

/// <inheritdoc />
public sealed class SnapshotRepository(AppDbContext db, TimeProvider time) : ISnapshotRepository
{
    private const long WriteLockKey = SourceIngestion.WriteLockKey;

    /// <inheritdoc />
    public async Task<StoreResult> StoreAsync(ParsedSnapshot parsed, byte[] raw, SnapshotRequestMeta meta, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var hash = SnapshotParser.ContentHash(parsed.Data, meta.SourceAsOf);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({WriteLockKey})", ct);

        var latest = await db.SourceSnapshots.AsNoTracking().OrderByDescending(s => s.Revision)
            .Select(s => new { s.Id, s.Revision, s.SourceAsOf }).FirstOrDefaultAsync(ct);
        if (meta.IfMatchRevision is { } expected && latest?.Revision != expected)
            return new StoreResult(StoreOutcome.PreconditionFailed, null, null, latest?.Revision);
        var same = await db.SourceSnapshots.AsNoTracking().Where(s => s.ContentHash == hash).Select(s => (long?)s.Revision).FirstOrDefaultAsync(ct);
        if (same is not null) return new StoreResult(StoreOutcome.Idempotent, same, null, latest?.Revision);
        if (latest is not null && meta.SourceAsOf < latest.SourceAsOf)
            return new StoreResult(StoreOutcome.Stale, null, null, latest.Revision);

        var now = time.GetUtcNow();
        var snapshot = new SourceSnapshotEntity
        {
            Revision = (latest?.Revision ?? 0) + 1,
            SourceAsOf = meta.SourceAsOf,
            SourceAsOfAssumed = meta.SourceAsOfAssumed,
            ReceivedAt = now,
            ContentHash = hash,
            RawSha256 = Convert.ToHexStringLower(SHA256.HashData(raw)),
            Adapter = SnapshotParser.AdapterName,
            SchemaVersion = parsed.SchemaVersion,
            SourceRevisionLabel = parsed.SourceRevisionLabel,
            Synthetic = parsed.Synthetic,
            Provenance = meta.Provenance,
            Warnings = AppJson.Serialize(parsed.Warnings),
            AccountsCount = parsed.Data.Accounts.Count,
            ServicesCount = parsed.Data.Services.Count,
            TransactionsCount = parsed.Data.Transactions.Count,
            RawBytes = raw.LongLength,
            RawGzip = Gzip(raw),
            Actor = meta.Actor,
        };
        db.SourceSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        await CopyRowsAsync((NpgsqlConnection)db.Database.GetDbConnection(), snapshot.Id, parsed.Data, ct);

        var state = await db.AppState.FromSqlRaw("SELECT * FROM app_state WHERE id = 1 FOR UPDATE").SingleAsync(ct);
        // Сверка с текущим состоянием (снимок ⊕ события), а не только с прошлым снимком: расхождения — наблюдения с интервалом
        var previous = await db.CurServices.AsNoTracking()
            .Select(s => new ServiceInput(s.Id, s.OwnerId, s.Name, s.PaymentTerm, s.AutoProlong, s.CreatedAt, s.ExpiresAt, s.Status, s.Price))
            .ToDictionaryAsync(s => s.Id, ct);
        if (latest is not null)
        {
            var observedFrom = state.SourceUpdatedAt is { } u && u > latest.SourceAsOf ? u : latest.SourceAsOf;
            if (observedFrom > meta.SourceAsOf) observedFrom = latest.SourceAsOf;
            foreach (var s in parsed.Data.Services)
            {
                if (!previous.TryGetValue(s.Id, out var p))
                    db.ObservedServiceEvents.Add(Event(snapshot, s.Id, s.OwnerId, "appeared", null, s.Status, observedFrom));
                else if (p.Status != s.Status)
                    db.ObservedServiceEvents.Add(Event(snapshot, s.Id, s.OwnerId, "status_changed", p.Status, s.Status, observedFrom));
            }
            var current = parsed.Data.Services.Select(s => s.Id).ToHashSet();
            foreach (var p in previous.Values.Where(p => !current.Contains(p.Id)))
                db.ObservedServiceEvents.Add(Event(snapshot, p.Id, p.OwnerId, "absent_from_snapshot", p.Status, null, observedFrom));
        }

        // Снимок — новая база текущего состояния; события, полученные после sourceAsOf, применяются поверх
        await CurrentStateStore.ReplaceFromSnapshotAsync(db, snapshot.Id, meta.SourceAsOf, ct);
        // Сверка тестовыми данными закрывает пропуски accountSeq: состояние записей снова полное
        await db.SourceAccountStreams.Where(s => s.Missing.Length > 0)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Missing, Array.Empty<long>()).SetProperty(s => s.GapSince, (DateTimeOffset?)null), ct);
        var replayed = await ReplayEventsAsync(parsed.Data, meta.SourceAsOf, now, ct);
        var grace = await SourceIngestion.GraceDaysAsync(db, ct);
        var actions = parsed.Data.Services.SelectMany(s => ExpiryOutcomeRules.OnServiceObserved(previous.GetValueOrDefault(s.Id), s, meta.SourceAsOf, grace)).ToList();
        await ExpiryStore.ApplyAsync(db, actions, parsed.Data.Services.ToDictionary(s => s.Id, s => s.PaymentTerm), grace, parsed.Synthetic, ct);

        state.LatestSourceSnapshotId = snapshot.Id;
        if (state.SourceUpdatedAt is null || state.SourceUpdatedAt < meta.SourceAsOf) state.SourceUpdatedAt = meta.SourceAsOf;
        var job = RecalcQueue.Enqueue(db, "source", now, meta.Actor, sourceSnapshotId: snapshot.Id);
        db.Audit.Add(new AuditEntry
        {
            At = now,
            Actor = meta.Actor,
            Action = "snapshot_accepted",
            Details = AppJson.Serialize(new
            {
                snapshot.Revision,
                snapshot.SourceAsOf,
                snapshot.ContentHash,
                snapshot.AccountsCount,
                snapshot.ServicesCount,
                snapshot.TransactionsCount,
                replayedEvents = replayed,
            }),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new StoreResult(StoreOutcome.Accepted, snapshot.Revision, job.Id, snapshot.Revision);
    }

    /// <inheritdoc />
    public Task<bool> AnyAsync(CancellationToken cancellationToken) => db.SourceSnapshots.AnyAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<SourceSnapshotEntity?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var state = await db.AppState.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (state?.LatestSourceSnapshotId is not { } id) return null;
        return await db.SourceSnapshots.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SourceSnapshotEntity> GetAsync(long snapshotId, CancellationToken cancellationToken) =>
        db.SourceSnapshots.AsNoTracking().SingleAsync(s => s.Id == snapshotId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<(SourceSnapshotEntity Snapshot, bool Active)>> ListAsync(int take, CancellationToken cancellationToken)
    {
        var latest = await db.AppState.AsNoTracking().Select(s => s.LatestSourceSnapshotId).SingleOrDefaultAsync(cancellationToken);
        var list = await db.SourceSnapshots.AsNoTracking().OrderByDescending(s => s.Revision).Take(take).ToListAsync(cancellationToken);
        return list.Select(s => (s, s.Id == latest)).ToList();
    }

    /// <inheritdoc />
    public Task<SourceData> LoadCurrentAsync(CancellationToken cancellationToken) => CurrentStateStore.LoadAllAsync(db, cancellationToken);

    /// <inheritdoc />
    public async Task<SourceData> LoadDataAsync(long snapshotId, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var accounts = await db.SrcAccounts.AsNoTracking().Where(a => a.SnapshotId == snapshotId).OrderBy(a => a.Id)
            .Select(a => new AccountInput(a.Id, a.Email, a.Balance, a.WithdrawBalance, a.TotalReplenished, a.BonusBalance, a.BonusState)).ToListAsync(ct);
        var services = await db.SrcServices.AsNoTracking().Where(s => s.SnapshotId == snapshotId).OrderBy(s => s.Id)
            .Select(s => new ServiceInput(s.Id, s.OwnerId, s.Name, s.PaymentTerm, s.AutoProlong, s.CreatedAt, s.ExpiresAt, s.Status, s.Price)).ToListAsync(ct);
        var transactions = await db.SrcTransactions.AsNoTracking().Where(t => t.SnapshotId == snapshotId).OrderBy(t => t.Id)
            .Select(t => new TransactionInput(t.Id, t.ServiceId, t.Amount, t.Status, t.PerformedAt, t.CreatedAt, t.Type)).ToListAsync(ct);
        return new SourceData(accounts, services, transactions);
    }

    /// <summary>Переиграть события, полученные после sourceAsOf, поверх нового снимка; возвращает число событий</summary>
    private async Task<int> ReplayEventsAsync(SourceData snapshotData, DateTimeOffset sourceAsOf, DateTimeOffset now, CancellationToken ct)
    {
        // По времени события: v2 из истории (occurredAt до выгрузки) уже учтены в файле, даже если получены позже
        var events = await db.SourceEvents.AsNoTracking().Where(e => e.OccurredAt > sourceAsOf).OrderBy(e => e.Seq)
            .Select(e => new { e.Seq, e.Kind, e.Payload, e.EventId }).ToListAsync(ct);
        if (events.Count == 0) return 0;
        var stateMem = SourceState.From(snapshotData);
        var accounts = new Dictionary<long, (AccountInput, long)>();
        var services = new Dictionary<long, (ServiceInput, long)>();
        var transactions = new Dictionary<long, (TransactionInput, long)>();
        foreach (var e in events)
        {
            var change = SourceIngestion.FromPayload(e.Kind, e.Payload, e.EventId);
            if (stateMem.Apply(change).Outcome != ChangeOutcome.Applied) continue;
            switch (change.Kind)
            {
                case SourceRecordKind.Account: accounts[change.Account!.Id] = (change.Account, e.Seq); break;
                case SourceRecordKind.Service: services[change.Service!.Id] = (change.Service, e.Seq); break;
                default: transactions[change.Transaction!.Id] = (change.Transaction, e.Seq); break;
            }
        }
        await CurrentStateStore.UpsertAsync(db, accounts, services, transactions, now, ct);
        return events.Count;
    }

    private static ObservedServiceEvent Event(SourceSnapshotEntity snap, long serviceId, long ownerId, string kind, string? from, string? to, DateTimeOffset prevAsOf) => new()
    {
        SourceSnapshotId = snap.Id, SourceRevision = snap.Revision, ServiceId = serviceId, OwnerId = ownerId, Kind = kind,
        FromStatus = from, ToStatus = to, ObservedFrom = prevAsOf, ObservedTo = snap.SourceAsOf,
    };

    private static byte[] Gzip(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize)) gz.Write(raw);
        return ms.ToArray();
    }

    private static async Task CopyRowsAsync(NpgsqlConnection conn, long snapshotId, SourceData data, CancellationToken ct)
    {
        await using (var w = await conn.BeginBinaryImportAsync("COPY src_accounts (snapshot_id, id, email, balance, withdraw_balance, total_replenished, bonus_balance, bonus_state) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var a in data.Accounts)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(snapshotId, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(a.Id, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(a.Email, NpgsqlDbType.Text, ct);
                await Long(w, a.Balance, ct); await Long(w, a.WithdrawBalance, ct); await Long(w, a.TotalReplenished, ct); await Long(w, a.BonusBalance, ct);
                await Text(w, a.BonusState, ct);
            }
            await w.CompleteAsync(ct);
        }
        await using (var w = await conn.BeginBinaryImportAsync("COPY src_services (snapshot_id, id, owner_id, name, payment_term, auto_prolong, created_at, expires_at, status, price) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var s in data.Services)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(snapshotId, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(s.Id, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(s.OwnerId, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(s.Name, NpgsqlDbType.Text, ct);
                await Text(w, s.PaymentTerm, ct);
                if (s.AutoProlong is null) await w.WriteNullAsync(ct); else await w.WriteAsync(s.AutoProlong.Value, NpgsqlDbType.Boolean, ct);
                await Date(w, s.CreatedAt, ct); await Date(w, s.ExpiresAt, ct);
                await Text(w, s.Status, ct);
                await Long(w, s.Price, ct);
            }
            await w.CompleteAsync(ct);
        }
        await using (var w = await conn.BeginBinaryImportAsync("COPY src_transactions (snapshot_id, id, service_id, amount, status, performed_at, created_at, type) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var t in data.Transactions)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(snapshotId, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(t.Id, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(t.ServiceId, NpgsqlDbType.Bigint, ct);
                await Long(w, t.Amount, ct);
                await Text(w, t.Status, ct);
                await Date(w, t.PerformedAt, ct); await Date(w, t.CreatedAt, ct);
                await Text(w, t.Type, ct);
            }
            await w.CompleteAsync(ct);
        }
    }

    private static async Task Long(NpgsqlBinaryImporter w, long? v, CancellationToken ct)
    {
        if (v is null) await w.WriteNullAsync(ct);
        else await w.WriteAsync(v.Value, NpgsqlDbType.Bigint, ct);
    }
    private static async Task Text(NpgsqlBinaryImporter w, string? v, CancellationToken ct)
    {
        if (v is null) await w.WriteNullAsync(ct);
        else await w.WriteAsync(v, NpgsqlDbType.Text, ct);
    }
    private static async Task Date(NpgsqlBinaryImporter w, DateTimeOffset? v, CancellationToken ct)
    {
        if (v is null) await w.WriteNullAsync(ct);
        else await w.WriteAsync(v.Value.UtcDateTime, NpgsqlDbType.TimestampTz, ct);
    }
}
