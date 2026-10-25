using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aeza.Risk.Infrastructure.Data;

/// <summary>Разделение прав: Migrator (владелец схемы) создаёт роль приложения только с DML-правами</summary>
public static partial class DbRoles
{
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();

    /// <summary>Создать/обновить роль приложения с DML-правами</summary>
    public static async Task EnsureAppRoleAsync(AppDbContext db, string? user, string? password, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
        {
            logger.LogWarning("App DB role not configured; the app will use the migrator credentials");
            return;
        }
        if (!RoleName().IsMatch(user)) throw new InvalidOperationException("App:AppDbUser: допустимы только [a-z0-9_].");
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(cancellationToken);
        await using (var build = conn.CreateCommand())
        {
            // Текст CREATE/ALTER ROLE собирает сам PostgreSQL (format %I/%L): пароль не склеивается в SQL на клиенте
            build.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @u) " +
                                "THEN format('ALTER ROLE %I LOGIN PASSWORD %L', @u::text, @p::text) " +
                                "ELSE format('CREATE ROLE %I LOGIN PASSWORD %L', @u::text, @p::text) END";
            var pu = build.CreateParameter();
            pu.ParameterName = "u";
            pu.Value = user;
            build.Parameters.Add(pu);
            var pp = build.CreateParameter();
            pp.ParameterName = "p";
            pp.Value = password;
            build.Parameters.Add(pp);
            var ddl = (string)(await build.ExecuteScalarAsync(cancellationToken))!;
            await using var exec = conn.CreateCommand();
            exec.CommandText = ddl;
            await exec.ExecuteNonQueryAsync(cancellationToken);
        }
        var database = conn.Database.Replace("\"", "\"\"", StringComparison.Ordinal);
        foreach (var sql in new[]
                 {
                     $"GRANT CONNECT ON DATABASE \"{database}\" TO {user}",
                     $"GRANT USAGE ON SCHEMA public TO {user}",
                     $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {user}",
                     $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {user}",
                     $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {user}",
                     $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO {user}",
                 })
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        logger.LogInformation("App DB role {Role} ensured (DML only)", user);
    }
}
