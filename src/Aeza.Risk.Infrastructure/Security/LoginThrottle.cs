using System.Security.Cryptography;
using System.Text;

using Aeza.Risk.Core.Abstractions;

using Npgsql;

namespace Aeza.Risk.Infrastructure.Security;

/// <summary>Блокировка перебора паролей по паре (логин, IP) в PostgreSQL: одна для всех реплик сервиса</summary>
public sealed class LoginThrottle(NpgsqlDataSource db) : ILoginThrottle
{
    /// <summary>Через сколько без новых неудач счётчик начинается заново</summary>
    public static readonly TimeSpan IdleReset = TimeSpan.FromMinutes(15);

    /// <summary>Хеш ключа</summary>
    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <inheritdoc />
    public async Task<bool> IsLockedAsync(string key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var cmd = db.CreateCommand("SELECT locked_until > $2 FROM login_throttle WHERE key_hash = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = Hash(key) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = now });
        return await cmd.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <inheritdoc />
    public async Task<bool> RegisterFailureAsync(string key, DateTimeOffset now, int maxFailures, TimeSpan lockout, CancellationToken cancellationToken)
    {
        // Порог достигнут — блокировка и счётчик с нуля (как раньше в памяти пода)
        await using var cmd = db.CreateCommand("""
            INSERT INTO login_throttle AS t (key_hash, failures, locked_until, updated_at)
            VALUES ($1, CASE WHEN $3 <= 1 THEN 0 ELSE 1 END, CASE WHEN $3 <= 1 THEN $4 END, $2)
            ON CONFLICT (key_hash) DO UPDATE SET
                failures = CASE WHEN (CASE WHEN t.updated_at < $5 THEN 0 ELSE t.failures END) + 1 >= $3 THEN 0
                                ELSE (CASE WHEN t.updated_at < $5 THEN 0 ELSE t.failures END) + 1 END,
                locked_until = CASE WHEN (CASE WHEN t.updated_at < $5 THEN 0 ELSE t.failures END) + 1 >= $3 THEN $4 ELSE t.locked_until END,
                updated_at = $2
            RETURNING locked_until = $4
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Hash(key) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = now });
        cmd.Parameters.Add(new NpgsqlParameter { Value = maxFailures });
        cmd.Parameters.Add(new NpgsqlParameter { Value = now + lockout });
        cmd.Parameters.Add(new NpgsqlParameter { Value = now - IdleReset });
        return await cmd.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <inheritdoc />
    public async Task ResetAsync(string key, CancellationToken cancellationToken)
    {
        await using var cmd = db.CreateCommand("DELETE FROM login_throttle WHERE key_hash = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = Hash(key) });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
