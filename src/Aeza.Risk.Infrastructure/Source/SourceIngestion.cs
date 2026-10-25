using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Options;
using Aeza.Risk.Core.Settings;
using Aeza.Risk.Core.Source;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.Infrastructure.Data.Repositories;
using Aeza.Risk.SharedKernel.Json;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using NpgsqlTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aeza.Risk.Infrastructure.Source;

/// <inheritdoc />
public sealed class SourceIngestion(
    AppDbContext db,
    TimeProvider time,
    IOptions<StreamOptions> stream,
    ILogger<SourceIngestion> log) : ISourceIngestion
{
    /// <summary>Общий ключ записи источника (снимки и события)</summary>
    internal const long WriteLockKey = 727_002;

    /// <inheritdoc />
    public async Task<IngestBatchResult> IngestAsync(IReadOnlyList<IngestItem> items, IngestMeta meta, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({WriteLockKey})", ct);
        var now = time.GetUtcNow();
        var grace = await GraceDaysAsync(db, ct);
        if (meta.SimulationWorld is { } world)
        {
            var current = await db.SimulationState.AsNoTracking().Where(s => s.Id == 1).Select(s => (long?)s.WorldRevision).SingleOrDefaultAsync(ct);
            if (current != world) throw new SimulationWorldChangedException(current);
        }
        var appState = await db.AppState.FromSqlRaw("SELECT * FROM app_state WHERE id = 1 FOR UPDATE").SingleAsync(ct);
        // v2 с occurredAt не позже даты базы уже учтено в тестовых данных: не применяем, как и при переигрывании после замены снимка
        var baseAsOf = appState.LatestSourceSnapshotId is { } baseId
            ? await db.SourceSnapshots.AsNoTracking().Where(s => s.Id == baseId).Select(s => (DateTimeOffset?)s.SourceAsOf).SingleOrDefaultAsync(ct)
            : null;

        // Дедупликация: v1 — eventId записи в журнале изменений; v2 — eventId доменного события; плюс повторы внутри пакета
        var v1Ids = items.Where(i => i.Domain is null).SelectMany(i => i.Changes).Select(c => c.EventId).OfType<string>().Distinct().ToList();
        var knownV1 = v1Ids.Count == 0
            ? new HashSet<string>()
            : (await db.SourceEvents.AsNoTracking().Where(e => e.EventId != null && v1Ids.Contains(e.EventId)).Select(e => e.EventId!).ToListAsync(ct)).ToHashSet();
        var v2Ids = items.Select(i => i.Domain?.EventId).OfType<string>().Distinct().ToList();
        var knownV2 = v2Ids.Count == 0
            ? new HashSet<string>()
            : (await db.SourceDomainEvents.AsNoTracking().Where(e => v2Ids.Contains(e.EventId)).Select(e => e.EventId).ToListAsync(ct)).ToHashSet();

        var state = await CurrentStateStore.LoadForChangesAsync(db, items.SelectMany(i => i.Changes).ToList(), ct);
        var seqAccounts = items.Where(i => i.Domain is { AccountId: not null, AccountSeq: not null }).Select(i => i.Domain!.AccountId!.Value).Distinct().ToList();
        var streams = seqAccounts.Count == 0
            ? new Dictionary<long, SourceAccountStreamEntity>()
            : await db.SourceAccountStreams.Where(s => seqAccounts.Contains(s.AccountId)).ToDictionaryAsync(s => s.AccountId, ct);

        var batchId = Guid.NewGuid();
        var records = new List<(SourceEventEntity Row, ChangeResult Result, SourceDomainEventEntity? Domain)>();
        var v1Items = new List<(int Index, SourceEventEntity Row, ChangeResult Result)>();
        var v2Items = new List<(int Index, SourceDomainEventEntity Row, List<ChangeResult> Results)>();
        var results = new SortedDictionary<int, IngestItemResult>();
        var expiryActions = new List<ExpiryAction>();
        var dirtyAccounts = new Dictionary<long, (AccountInput Row, SourceEventEntity Event)>();
        var dirtyServices = new Dictionary<long, (ServiceInput Row, SourceEventEntity Event)>();
        var dirtyTx = new Dictionary<long, (TransactionInput Row, SourceEventEntity Event)>();
        var activity = new Dictionary<(long Account, DateOnly Day), ActivityDelta>();
        var duplicates = 0;
        var unknownTypes = 0;
        var accepted = 0;
        DateTimeOffset? fresh = null;
        DateTimeOffset? watermark = null;
        DateTimeOffset? lastOccurred = null;

        // observedAt — момент наблюдения состояния: v2 — время события в источнике, v1 — время получения
        SourceEventEntity ApplyRecord(SourceChange change, DateTimeOffset observedAt, string? eventType)
        {
            ServiceInput? before = change.Kind == SourceRecordKind.Service ? state.Services.GetValueOrDefault(change.Service!.Id) : null;
            var r = eventType is not null && baseAsOf is { } b && observedAt <= b
                ? ChangeResult.StaleBy(StaleReasons.BeforeBase, OwnerOf(state, change))
                : state.Apply(change);
            var row = new SourceEventEntity
            {
                EventId = change.EventId,
                ReceivedAt = now,
                OccurredAt = observedAt,
                EventType = eventType,
                Kind = change.Kind.ToApi(),
                EntityId = change.EntityId,
                OwnerId = r.OwnerId,
                Outcome = r.Outcome.ToApi(),
                StaleReason = r.StaleReason,
                Changes = r.Changes.ToArray(),
                Effect = AppJson.Serialize(r.Effect),
                Payload = Payload(change),
                Origin = meta.Origin,
                Actor = meta.Actor,
                Synthetic = meta.Synthetic,
                BatchId = batchId,
            };
            records.Add((row, r, null));
            if (r.Outcome != ChangeOutcome.Applied) return row;
            switch (change.Kind)
            {
                case SourceRecordKind.Account: dirtyAccounts[change.Account!.Id] = (change.Account, row); break;
                case SourceRecordKind.Service:
                    dirtyServices[change.Service!.Id] = (change.Service, row);
                    expiryActions.AddRange(ExpiryOutcomeRules.OnServiceObserved(before, change.Service, observedAt, grace));
                    break;
                default: dirtyTx[change.Transaction!.Id] = (change.Transaction, row); break;
            }
            return row;
        }

        foreach (var item in items)
        {
            if (item.Domain is not { } d)
            {
                var change = item.Changes[0];
                if (change.EventId is { } eid && !knownV1.Add(eid))
                {
                    duplicates++;
                    results[item.Index] = new IngestItemResult(item.Index, eid, SourceCodes.Duplicate, null, null, []);
                    continue;
                }
                accepted++;
                var row = ApplyRecord(change, now, null);
                v1Items.Add((item.Index, row, records[^1].Result));
                fresh = Max(fresh, now); // любое полученное событие v1 — свидетельство свежести источника
                continue;
            }

            if (!knownV2.Add(d.EventId))
            {
                duplicates++;
                results[item.Index] = new IngestItemResult(item.Index, d.EventId, SourceCodes.Duplicate, null, null, []);
                continue;
            }
            accepted++;
            if (!d.KnownType) unknownTypes++;
            var domainRow = new SourceDomainEventEntity
            {
                EventId = d.EventId,
                Type = d.Type,
                KnownType = d.KnownType,
                OccurredAt = d.OccurredAt,
                ReceivedAt = now,
                AccountId = d.AccountId,
                AccountSeq = d.AccountSeq,
                ServiceId = d.ServiceId,
                Connector = meta.Connector,
                Data = d.DataJson,
                Synthetic = meta.Synthetic,
                BatchId = batchId,
            };
            var changeResults = new List<ChangeResult>();
            foreach (var change in item.Changes)
            {
                ApplyRecord(change, d.OccurredAt, d.Type);
                var last = records[^1];
                last.Domain = domainRow;
                records[^1] = last;
                changeResults.Add(last.Result);
            }
            domainRow.StateApplied = changeResults.Count(r => r.Outcome == ChangeOutcome.Applied);
            domainRow.Outcome = changeResults.Count == 0 ? SourceCodes.Recorded
                : domainRow.StateApplied > 0 ? SourceCodes.Applied
                : changeResults.Any(r => r.Outcome == ChangeOutcome.Stale) ? SourceCodes.Stale
                : SourceCodes.NoChange;
            v2Items.Add((item.Index, domainRow, changeResults));

            if (d is { AccountId: { } acc, AccountSeq: { } aseq })
            {
                var current = streams.GetValueOrDefault(acc) is { } s ? new AccountSeqState(s.MaxSeq, s.Missing) : null;
                var (next, _) = AccountSeqState.Observe(current, aseq);
                if (!streams.TryGetValue(acc, out var entity))
                {
                    entity = new SourceAccountStreamEntity { AccountId = acc };
                    db.SourceAccountStreams.Add(entity);
                    streams[acc] = entity;
                }
                entity.MaxSeq = next.MaxSeq;
                entity.Missing = next.Missing.ToArray();
                entity.GapSince = next.Missing.Count == 0 ? null : entity.GapSince ?? now;
                entity.UpdatedAt = now;
            }
            // Дневная активность клиента — по времени события, поздние события дописывают свой день
            if (ActivityProjection.Fold(d) is var (accountId, day, delta))
            {
                if (activity.TryGetValue((accountId, day), out var sum)) sum.Add(delta);
                else activity[(accountId, day)] = delta;
            }
            if (d.WatermarkUpTo is { } up) watermark = Max(watermark, up);
            lastOccurred = Max(lastOccurred, d.OccurredAt);
            fresh = Max(fresh, d.WatermarkUpTo ?? d.OccurredAt);
        }

        // Доменные события получают seq первыми: изменения записей ссылаются на них; порядок записей — порядок пакета
        db.SourceDomainEvents.AddRange(v2Items.Select(v => v.Row));
        await db.SaveChangesAsync(ct);
        foreach (var (row, _, domain) in records)
            if (domain is not null) row.DomainSeq = domain.Seq;
        db.SourceEvents.AddRange(records.Select(r => r.Row));
        await db.SaveChangesAsync(ct); // seq назначает БД
        foreach (var (index, row, r) in v1Items)
            results[index] = new IngestItemResult(index, row.EventId, row.Outcome, row.Seq, r.StaleReason, r.Changes);
        foreach (var (index, row, rs) in v2Items)
            results[index] = new IngestItemResult(index, row.EventId, row.Outcome, row.Seq, rs.Select(r => r.StaleReason).FirstOrDefault(r => r is not null),
                rs.SelectMany(r => r.Changes).Distinct().ToList());

        await CurrentStateStore.UpsertAsync(db,
            dirtyAccounts.ToDictionary(k => k.Key, k => (k.Value.Row, k.Value.Event.Seq)),
            dirtyServices.ToDictionary(k => k.Key, k => (k.Value.Row, k.Value.Event.Seq)),
            dirtyTx.ToDictionary(k => k.Key, k => (k.Value.Row, k.Value.Event.Seq)), now, ct);
        await ExpiryStore.ApplyAsync(db, expiryActions, dirtyServices.ToDictionary(k => k.Key, k => k.Value.Row.PaymentTerm), grace, meta.Synthetic, ct);
        await UpsertActivityAsync(db, activity, now, ct);

        if (accepted > 0 || duplicates > 0 || meta.Rejected > 0)
        {
            var connector = await db.SourceConnectors.FindAsync([meta.Connector], ct);
            if (connector is null) db.SourceConnectors.Add(connector = new SourceConnectorEntity { Name = meta.Connector });
            connector.Events += accepted;
            connector.Duplicates += duplicates;
            connector.Rejected += meta.Rejected;
            if (meta.LastError is { } error)
            {
                connector.LastError = error.Length > 200 ? error[..200] : error;
                connector.LastErrorAt = now;
            }
            connector.LastReceivedAt = now;
            if (accepted > 0) connector.LastOccurredAt = Max(connector.LastOccurredAt, lastOccurred ?? now);
            connector.Watermark = Max(connector.Watermark, watermark);
        }

        var applied = records.Count(r => r.Result.Outcome == ChangeOutcome.Applied);
        if (applied > 0) appState.SourceSeq = records.Where(r => r.Result.Outcome == ChangeOutcome.Applied).Max(r => r.Row.Seq);
        // Свежесть источника не откатывается назад: история v2 (occurredAt в прошлом) не делает источник «устаревшим»
        if (fresh is { } f && (appState.SourceUpdatedAt is null || appState.SourceUpdatedAt < f)) appState.SourceUpdatedAt = f;

        RecalcJob? job = null;
        if (applied > 0 && !meta.DeferRecalc)
        {
            job = RecalcQueue.Enqueue(db, "events", now, meta.Actor);
            job.AvailableAt = now.AddSeconds(stream.Value.DebounceSeconds);
        }
        var counts = records.SelectMany(r => r.Result.Changes).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var noChange = v1Items.Count(v => v.Row.Outcome == SourceCodes.NoChange) + v2Items.Count(v => v.Row.Outcome == SourceCodes.NoChange);
        var stale = v1Items.Count(v => v.Row.Outcome == SourceCodes.Stale) + v2Items.Count(v => v.Row.Outcome == SourceCodes.Stale);
        var appliedItems = v1Items.Count(v => v.Row.Outcome == SourceCodes.Applied) + v2Items.Count(v => v.Row.Outcome == SourceCodes.Applied);
        var recorded = v2Items.Count(v => v.Row.Outcome == SourceCodes.Recorded);
        if (meta.Chunk is { } chunk)
        {
            // Кусок импорта выполнен ровно один раз: отметка и счётчики — в транзакции приёма
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE source_import_chunks SET done = true, data = ''::bytea WHERE import_id = {0} AND no = {1}", [chunk.ImportId, chunk.No], ct);
            await db.Database.ExecuteSqlRawAsync(@"
UPDATE source_imports SET chunks_done = chunks_done + 1, applied = applied + {1}, recorded = recorded + {2}, no_change = no_change + {3},
  stale = stale + {4}, duplicates = duplicates + {5}, rejected = rejected + {6} WHERE id = {0}",
                [chunk.ImportId, appliedItems, recorded, noChange, stale, duplicates, meta.Rejected], ct);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        log.LogInformation(
            "Source events ({Connector}, synthetic={Synthetic}): {Applied} applied, {NoChange} no_change, {Stale} stale, {Recorded} recorded, {Duplicates} duplicates, {Unknown} unknown types; seq {Seq}, job {JobId}",
            meta.Connector, meta.Synthetic, appliedItems, noChange, stale, recorded, duplicates, unknownTypes, appState.SourceSeq, job?.Id);
        return new IngestBatchResult(items.Count, appliedItems, noChange, stale, duplicates, recorded, unknownTypes, appState.SourceSeq, job?.Id,
            results.Values.ToList(), counts);
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>Добавить дневную активность пакета (суммы складываются, минимумы оценок — минимум)</summary>
    private static async Task UpsertActivityAsync(AppDbContext db, Dictionary<(long Account, DateOnly Day), ActivityDelta> activity, DateTimeOffset now, CancellationToken ct)
    {
        if (activity.Count == 0) return;
        var rows = activity.OrderBy(k => k.Key.Account).ThenBy(k => k.Key.Day).Select(kv => new Dictionary<string, object?>
        {
            ["account_id"] = kv.Key.Account, ["day"] = kv.Key.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["logins"] = kv.Value.Logins, ["logins_failed"] = kv.Value.LoginsFailed, ["usage_samples"] = kv.Value.UsageSamples, ["cpu_sum"] = kv.Value.CpuSum,
            ["traffic_out_gb"] = kv.Value.TrafficOutGb, ["incident_minutes"] = kv.Value.IncidentMinutes, ["tickets_opened"] = kv.Value.TicketsOpened,
            ["tickets_unsolved"] = kv.Value.TicketsUnsolved, ["csat_min"] = kv.Value.CsatMin, ["nps_min"] = kv.Value.NpsMin,
            ["payments_count"] = kv.Value.PaymentsCount, ["payments_minor"] = kv.Value.PaymentsMinor, ["payments_failed"] = kv.Value.PaymentsFailed,
            ["top_ups_count"] = kv.Value.TopUpsCount, ["top_ups_minor"] = kv.Value.TopUpsMinor, ["downgrades"] = kv.Value.Downgrades,
            ["upgrades"] = kv.Value.Upgrades, ["auto_prolong_off"] = kv.Value.AutoProlongOff, ["suspends"] = kv.Value.Suspends,
            ["withdrawals_minor"] = kv.Value.WithdrawalsMinor, ["backups"] = kv.Value.Backups, ["renewals"] = kv.Value.Renewals,
            ["deletions"] = kv.Value.Deletions, ["bonus_expired"] = kv.Value.BonusExpired, ["backup_downloads"] = kv.Value.BackupDownloads,
            ["dns_external"] = kv.Value.DnsExternal, ["retention_offers"] = kv.Value.RetentionOffers, ["retention_holdouts"] = kv.Value.RetentionHoldouts,
            ["events"] = kv.Value.Events,
        }).ToList();
        await db.Database.ExecuteSqlRawAsync(ActivityUpsertSql,
            [new NpgsqlParameter("rows", NpgsqlDbType.Jsonb) { Value = AppJson.Serialize(rows) }, new NpgsqlParameter("now", now)], ct);
    }

    private const string ActivityUpsertSql = @"
INSERT INTO customer_activity_daily (account_id, day, logins, logins_failed, usage_samples, cpu_sum, traffic_out_gb, incident_minutes,
  tickets_opened, tickets_unsolved, csat_min, nps_min, payments_count, payments_minor, payments_failed, top_ups_count, top_ups_minor,
  downgrades, upgrades, auto_prolong_off, suspends, withdrawals_minor, backups, renewals, deletions, bonus_expired,
  backup_downloads, dns_external, retention_offers, retention_holdouts, events, updated_at)
SELECT r.account_id, r.day, r.logins, r.logins_failed, r.usage_samples, r.cpu_sum, r.traffic_out_gb, r.incident_minutes,
  r.tickets_opened, r.tickets_unsolved, r.csat_min, r.nps_min, r.payments_count, r.payments_minor, r.payments_failed, r.top_ups_count, r.top_ups_minor,
  r.downgrades, r.upgrades, r.auto_prolong_off, r.suspends, r.withdrawals_minor, r.backups, r.renewals, r.deletions, r.bonus_expired,
  r.backup_downloads, r.dns_external, r.retention_offers, r.retention_holdouts, r.events, @now
FROM jsonb_to_recordset(@rows) AS r(account_id bigint, day date, logins int, logins_failed int, usage_samples int, cpu_sum double precision,
  traffic_out_gb double precision, incident_minutes int, tickets_opened int, tickets_unsolved int, csat_min int, nps_min int, payments_count int,
  payments_minor bigint, payments_failed int, top_ups_count int, top_ups_minor bigint, downgrades int, upgrades int, auto_prolong_off int,
  suspends int, withdrawals_minor bigint, backups int, renewals int, deletions int, bonus_expired int,
  backup_downloads int, dns_external int, retention_offers int, retention_holdouts int, events int)
ON CONFLICT (account_id, day) DO UPDATE SET
  logins = customer_activity_daily.logins + excluded.logins,
  logins_failed = customer_activity_daily.logins_failed + excluded.logins_failed,
  usage_samples = customer_activity_daily.usage_samples + excluded.usage_samples,
  cpu_sum = customer_activity_daily.cpu_sum + excluded.cpu_sum,
  traffic_out_gb = customer_activity_daily.traffic_out_gb + excluded.traffic_out_gb,
  incident_minutes = customer_activity_daily.incident_minutes + excluded.incident_minutes,
  tickets_opened = customer_activity_daily.tickets_opened + excluded.tickets_opened,
  tickets_unsolved = customer_activity_daily.tickets_unsolved + excluded.tickets_unsolved,
  csat_min = LEAST(customer_activity_daily.csat_min, excluded.csat_min),
  nps_min = LEAST(customer_activity_daily.nps_min, excluded.nps_min),
  payments_count = customer_activity_daily.payments_count + excluded.payments_count,
  payments_minor = customer_activity_daily.payments_minor + excluded.payments_minor,
  payments_failed = customer_activity_daily.payments_failed + excluded.payments_failed,
  top_ups_count = customer_activity_daily.top_ups_count + excluded.top_ups_count,
  top_ups_minor = customer_activity_daily.top_ups_minor + excluded.top_ups_minor,
  downgrades = customer_activity_daily.downgrades + excluded.downgrades,
  upgrades = customer_activity_daily.upgrades + excluded.upgrades,
  auto_prolong_off = customer_activity_daily.auto_prolong_off + excluded.auto_prolong_off,
  suspends = customer_activity_daily.suspends + excluded.suspends,
  withdrawals_minor = customer_activity_daily.withdrawals_minor + excluded.withdrawals_minor,
  backups = customer_activity_daily.backups + excluded.backups,
  renewals = customer_activity_daily.renewals + excluded.renewals,
  deletions = customer_activity_daily.deletions + excluded.deletions,
  bonus_expired = customer_activity_daily.bonus_expired + excluded.bonus_expired,
  backup_downloads = customer_activity_daily.backup_downloads + excluded.backup_downloads,
  dns_external = customer_activity_daily.dns_external + excluded.dns_external,
  retention_offers = customer_activity_daily.retention_offers + excluded.retention_offers,
  retention_holdouts = customer_activity_daily.retention_holdouts + excluded.retention_holdouts,
  events = customer_activity_daily.events + excluded.events,
  updated_at = excluded.updated_at";

    private static long? OwnerOf(SourceState state, SourceChange c) => c.Kind switch
    {
        SourceRecordKind.Account => c.Account!.Id,
        SourceRecordKind.Service => c.Service!.OwnerId,
        _ => state.Services.TryGetValue(c.Transaction!.ServiceId, out var s) ? s.OwnerId : null,
    };

    /// <summary>Состояние записи как пришло — оригинальные имена полей источника</summary>
    internal static string Payload(SourceChange change) => change.Kind switch
    {
        SourceRecordKind.Account => AppJson.Serialize(change.Account),
        SourceRecordKind.Service => AppJson.Serialize(change.Service),
        _ => AppJson.Serialize(change.Transaction),
    };

    /// <summary>Изменение из сохранённого состояния (переигрывание после снимка)</summary>
    internal static SourceChange FromPayload(string kind, string payload, string? eventId) => SourceCodes.ParseKind(kind) switch
    {
        SourceRecordKind.Account => SourceChange.Of(AppJson.Deserialize<AccountInput>(payload), eventId),
        SourceRecordKind.Service => SourceChange.Of(AppJson.Deserialize<ServiceInput>(payload), eventId),
        _ => SourceChange.Of(AppJson.Deserialize<TransactionInput>(payload), eventId),
    };

    /// <summary>N из последней ревизии модели: «не продлено» через N дней после срока</summary>
    internal static async Task<int> GraceDaysAsync(AppDbContext db, CancellationToken ct)
    {
        var json = await db.ModelRevisions.AsNoTracking().OrderByDescending(m => m.Id).Select(m => m.SettingsJson).FirstOrDefaultAsync(ct);
        return json is null ? ChurnSettings.Default.NonRenewalDays : AppJson.Deserialize<ModelSettingsDto>(json).ToModel().Churn.NonRenewalDays;
    }

    /// <summary>До какого момента источник полон — для решения «не продлено»</summary>
    internal static async Task<DateTimeOffset?> CompleteUpToAsync(AppDbContext db, DateTimeOffset? sourceUpdatedAt, CancellationToken ct)
    {
        var watermark = await db.SourceConnectors.AsNoTracking().Where(c => c.Watermark != null).MinAsync(c => c.Watermark, ct);
        return watermark ?? sourceUpdatedAt;
    }
}

/// <summary>Регистрация приёма событий источника</summary>
public static class SourceEntry
{
    /// <summary>Приём событий (push и тестовые данные); исходящих запросов к источнику нет</summary>
    public static IServiceCollection AddSourceConnectors(this IServiceCollection services)
    {
        services.AddScoped<ISourceIngestion, SourceIngestion>();
        return services;
    }
}
