using Aeza.Risk.Core;
using Aeza.Risk.Core.Source;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

using NpgsqlTypes;

using Aeza.Risk.Infrastructure.Data;

namespace Aeza.Risk.Infrastructure.Source;

/// <summary>Текущее состояние источника (cur_*): чтение целиком (пересчёт), чтение нужной части (приём событий), upsert и замена снимком</summary>
internal static class CurrentStateStore
{
    /// <summary>Всё текущее состояние (для пересчёта)</summary>
    public static async Task<SourceData> LoadAllAsync(AppDbContext db, CancellationToken ct)
    {
        var accounts = await db.CurAccounts.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new AccountInput(a.Id, a.Email, a.Balance, a.WithdrawBalance, a.TotalReplenished, a.BonusBalance, a.BonusState)).ToListAsync(ct);
        var services = await db.CurServices.AsNoTracking().OrderBy(s => s.Id)
            .Select(s => new ServiceInput(s.Id, s.OwnerId, s.Name, s.PaymentTerm, s.AutoProlong, s.CreatedAt, s.ExpiresAt, s.Status, s.Price)).ToListAsync(ct);
        var transactions = await db.CurTransactions.AsNoTracking().OrderBy(t => t.Id)
            .Select(t => new TransactionInput(t.Id, t.ServiceId, t.Amount, t.Status, t.PerformedAt, t.CreatedAt, t.Type)).ToListAsync(ct);
        return new SourceData(accounts, services, transactions);
    }

    /// <summary>Часть состояния, нужная для пакета: сами записи, их владельцы и все услуги затронутых клиентов (чтобы распознать «клиент потерял все услуги»)</summary>
    public static async Task<SourceState> LoadForChangesAsync(AppDbContext db, IReadOnlyList<SourceChange> changes, CancellationToken ct)
    {
        var accountIds = new HashSet<long>();
        var serviceIds = new HashSet<long>();
        var txIds = new HashSet<long>();
        foreach (var c in changes)
        {
            switch (c.Kind)
            {
                case SourceRecordKind.Account: accountIds.Add(c.Account!.Id); break;
                case SourceRecordKind.Service: serviceIds.Add(c.Service!.Id); accountIds.Add(c.Service.OwnerId); break;
                default: txIds.Add(c.Transaction!.Id); serviceIds.Add(c.Transaction.ServiceId); break;
            }
        }
        var ownersOfServices = await db.CurServices.AsNoTracking().Where(s => serviceIds.Contains(s.Id)).Select(s => s.OwnerId).ToListAsync(ct);
        accountIds.UnionWith(ownersOfServices);
        var owners = accountIds.ToList();
        var services = await db.CurServices.AsNoTracking().Where(s => serviceIds.Contains(s.Id) || owners.Contains(s.OwnerId))
            .Select(s => new ServiceInput(s.Id, s.OwnerId, s.Name, s.PaymentTerm, s.AutoProlong, s.CreatedAt, s.ExpiresAt, s.Status, s.Price)).ToListAsync(ct);
        var accounts = await db.CurAccounts.AsNoTracking().Where(a => owners.Contains(a.Id))
            .Select(a => new AccountInput(a.Id, a.Email, a.Balance, a.WithdrawBalance, a.TotalReplenished, a.BonusBalance, a.BonusState)).ToListAsync(ct);
        var transactions = await db.CurTransactions.AsNoTracking().Where(t => txIds.Contains(t.Id))
            .Select(t => new TransactionInput(t.Id, t.ServiceId, t.Amount, t.Status, t.PerformedAt, t.CreatedAt, t.Type)).ToListAsync(ct);
        return SourceState.From(new SourceData(accounts, services, transactions));
    }

    /// <summary>Заменить текущее состояние строками снимка</summary>
    public static async Task ReplaceFromSnapshotAsync(AppDbContext db, long snapshotId, DateTimeOffset at, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM cur_transactions; DELETE FROM cur_services; DELETE FROM cur_accounts;", ct);
        await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO cur_accounts (id, email, balance, withdraw_balance, total_replenished, bonus_balance, bonus_state, updated_seq, updated_at)
SELECT id, email, balance, withdraw_balance, total_replenished, bonus_balance, bonus_state, 0, {1} FROM src_accounts WHERE snapshot_id = {0};
INSERT INTO cur_services (id, owner_id, name, payment_term, auto_prolong, created_at, expires_at, status, price, updated_seq, updated_at)
SELECT id, owner_id, name, payment_term, auto_prolong, created_at, expires_at, status, price, 0, {1} FROM src_services WHERE snapshot_id = {0};
INSERT INTO cur_transactions (id, service_id, amount, status, performed_at, created_at, type, updated_seq, updated_at)
SELECT id, service_id, amount, status, performed_at, created_at, type, 0, {1} FROM src_transactions WHERE snapshot_id = {0};",
            [snapshotId, at], ct);
    }

    /// <summary>Записать изменённые записи (seq — событие, последним изменившее запись)</summary>
    public static async Task UpsertAsync(
        AppDbContext db, IReadOnlyDictionary<long, (AccountInput Row, long Seq)> accounts, IReadOnlyDictionary<long, (ServiceInput Row, long Seq)> services,
        IReadOnlyDictionary<long, (TransactionInput Row, long Seq)> transactions, DateTimeOffset at, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var tx = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        await using var batch = new NpgsqlBatch(conn, tx);
        foreach (var (a, seq) in accounts.Values)
        {
            var c = new NpgsqlBatchCommand(@"INSERT INTO cur_accounts (id, email, balance, withdraw_balance, total_replenished, bonus_balance, bonus_state, updated_seq, updated_at)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
ON CONFLICT (id) DO UPDATE SET email = EXCLUDED.email, balance = EXCLUDED.balance, withdraw_balance = EXCLUDED.withdraw_balance,
  total_replenished = EXCLUDED.total_replenished, bonus_balance = EXCLUDED.bonus_balance, bonus_state = EXCLUDED.bonus_state,
  updated_seq = EXCLUDED.updated_seq, updated_at = EXCLUDED.updated_at");
            Add(c, a.Id, NpgsqlDbType.Bigint); Add(c, a.Email, NpgsqlDbType.Text); Add(c, a.Balance, NpgsqlDbType.Bigint); Add(c, a.WithdrawBalance, NpgsqlDbType.Bigint);
            Add(c, a.TotalReplenished, NpgsqlDbType.Bigint); Add(c, a.BonusBalance, NpgsqlDbType.Bigint); Add(c, a.BonusState, NpgsqlDbType.Text);
            Add(c, seq, NpgsqlDbType.Bigint); Add(c, at, NpgsqlDbType.TimestampTz);
            batch.BatchCommands.Add(c);
        }
        foreach (var (s, seq) in services.Values)
        {
            var c = new NpgsqlBatchCommand(@"INSERT INTO cur_services (id, owner_id, name, payment_term, auto_prolong, created_at, expires_at, status, price, updated_seq, updated_at)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
ON CONFLICT (id) DO UPDATE SET owner_id = EXCLUDED.owner_id, name = EXCLUDED.name, payment_term = EXCLUDED.payment_term, auto_prolong = EXCLUDED.auto_prolong,
  created_at = EXCLUDED.created_at, expires_at = EXCLUDED.expires_at, status = EXCLUDED.status, price = EXCLUDED.price,
  updated_seq = EXCLUDED.updated_seq, updated_at = EXCLUDED.updated_at");
            Add(c, s.Id, NpgsqlDbType.Bigint); Add(c, s.OwnerId, NpgsqlDbType.Bigint); Add(c, s.Name, NpgsqlDbType.Text); Add(c, s.PaymentTerm, NpgsqlDbType.Text);
            Add(c, s.AutoProlong, NpgsqlDbType.Boolean); Add(c, s.CreatedAt, NpgsqlDbType.TimestampTz); Add(c, s.ExpiresAt, NpgsqlDbType.TimestampTz);
            Add(c, s.Status, NpgsqlDbType.Text); Add(c, s.Price, NpgsqlDbType.Bigint); Add(c, seq, NpgsqlDbType.Bigint); Add(c, at, NpgsqlDbType.TimestampTz);
            batch.BatchCommands.Add(c);
        }
        foreach (var (t, seq) in transactions.Values)
        {
            var c = new NpgsqlBatchCommand(@"INSERT INTO cur_transactions (id, service_id, amount, status, performed_at, created_at, type, updated_seq, updated_at)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
ON CONFLICT (id) DO UPDATE SET service_id = EXCLUDED.service_id, amount = EXCLUDED.amount, status = EXCLUDED.status,
  performed_at = EXCLUDED.performed_at, created_at = EXCLUDED.created_at, type = EXCLUDED.type,
  updated_seq = EXCLUDED.updated_seq, updated_at = EXCLUDED.updated_at");
            Add(c, t.Id, NpgsqlDbType.Bigint); Add(c, t.ServiceId, NpgsqlDbType.Bigint); Add(c, t.Amount, NpgsqlDbType.Bigint); Add(c, t.Status, NpgsqlDbType.Text);
            Add(c, t.PerformedAt, NpgsqlDbType.TimestampTz); Add(c, t.CreatedAt, NpgsqlDbType.TimestampTz); Add(c, t.Type, NpgsqlDbType.Text);
            Add(c, seq, NpgsqlDbType.Bigint); Add(c, at, NpgsqlDbType.TimestampTz);
            batch.BatchCommands.Add(c);
        }
        if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(ct);
    }

    private static void Add(NpgsqlBatchCommand c, object? value, NpgsqlDbType type) =>
        c.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value switch
        {
            null => DBNull.Value,
            DateTimeOffset d => d.UtcDateTime,
            _ => value,
        } });
}
