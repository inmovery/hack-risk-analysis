using Aeza.Risk.Core;
using Aeza.Risk.Core.Source;
using Aeza.Risk.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;

namespace Aeza.Risk.Infrastructure.Source;

/// <summary>Исходы сроков (expiry_outcomes): выполнение действий <see cref="ExpiryOutcomeRules"/> и разрешение созревших сроков</summary>
internal static class ExpiryStore
{
    /// <summary>Применить действия, вычисленные чистыми правилами</summary>
    public static async Task ApplyAsync(AppDbContext db, IEnumerable<ExpiryAction> actions, IReadOnlyDictionary<long, string?> terms, int graceDays, bool synthetic, CancellationToken ct)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case RegisterExpiry r:
                    await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO expiry_outcomes (service_id, expires_at, owner_id, payment_term, registered_at, synthetic)
VALUES ({0}, {1}, {2}, NULLIF({3}, ''), {4}, {5}) ON CONFLICT (service_id, expires_at) DO NOTHING",
                        [r.ServiceId, r.ExpiresAt, r.OwnerId, terms.GetValueOrDefault(r.ServiceId) ?? "", r.ObservedAt, synthetic], ct);
                    break;
                case ResolveExpiry { LateOutcome: null, ExpiresBefore: null } x:
                    await db.Database.ExecuteSqlRawAsync(@"
UPDATE expiry_outcomes SET outcome = {1}, resolution = {2}, resolved_at = {3}
WHERE service_id = {0} AND outcome IS NULL",
                        [x.ServiceId, x.Outcome, x.Resolution, x.At], ct);
                    break;
                case ResolveExpiry { LateOutcome: null } x:
                    await db.Database.ExecuteSqlRawAsync(@"
UPDATE expiry_outcomes SET outcome = {1}, resolution = {2}, resolved_at = {3}
WHERE service_id = {0} AND outcome IS NULL AND expires_at < {4}",
                        [x.ServiceId, x.Outcome, x.Resolution, x.At, x.ExpiresBefore!.Value], ct);
                    break;
                case ResolveExpiry x:
                    // Продление пришло позже E + N — по определению срок не продлён в течение N дней
                    await db.Database.ExecuteSqlRawAsync(@"
UPDATE expiry_outcomes SET
  outcome = CASE WHEN {3} > expires_at + make_interval(days => {5}) THEN {6} ELSE {1} END,
  resolution = CASE WHEN {3} > expires_at + make_interval(days => {5}) THEN {7} ELSE {2} END,
  resolved_at = {3}
WHERE service_id = {0} AND outcome IS NULL AND expires_at < {4}",
                        [x.ServiceId, x.Outcome, x.Resolution, x.At, x.ExpiresBefore!.Value, graceDays, x.LateOutcome!, ExpiryOutcomes.ResolutionRenewedLate], ct);
                    break;
            }
        }
    }

    /// <summary>Созревшие сроки (E + N прошло)</summary>
    public static Task<int> ResolveDueAsync(AppDbContext db, DateTimeOffset now, DateTimeOffset? completeUpTo, int graceDays, CancellationToken ct)
    {
        if (completeUpTo is null) return Task.FromResult(0);
        return db.Database.ExecuteSqlRawAsync(@"
UPDATE expiry_outcomes o SET
  outcome = CASE WHEN (SELECT s.expires_at FROM cur_services s WHERE s.id = o.service_id) > o.expires_at THEN 'renewed' ELSE 'not_renewed' END,
  resolution = CASE WHEN (SELECT s.expires_at FROM cur_services s WHERE s.id = o.service_id) > o.expires_at THEN 'extended' ELSE 'expired_fresh_source' END,
  resolved_at = {0}
WHERE o.outcome IS NULL
  AND o.expires_at + make_interval(days => {2}) <= {0}
  AND o.expires_at + make_interval(days => {2}) <= {1}
  -- Пропуск accountSeq у владельца — возможно, потеряно продление: «не продлено» ждёт сверки тестовыми данными
  AND ((SELECT s.expires_at FROM cur_services s WHERE s.id = o.service_id) > o.expires_at
       OR NOT EXISTS (SELECT 1 FROM source_account_streams g WHERE g.account_id = o.owner_id AND cardinality(g.missing) > 0))",
            [now, completeUpTo.Value, graceDays], ct);
    }
}
