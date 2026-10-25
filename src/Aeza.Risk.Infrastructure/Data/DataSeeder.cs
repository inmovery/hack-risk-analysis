using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Options;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Settings;
using Aeza.Risk.Core.Source;
using Aeza.Risk.SharedKernel.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aeza.Risk.Infrastructure.Data;

/// <summary>Идемпотентная инициализация: app_state, стартовая ревизия модели, bootstrap-пользователи, seed снимка — только если пусто</summary>
public sealed class DataSeeder(
    AppDbContext db,
    IUserRepository users,
    IPasswordHasher passwords,
    ISnapshotRepository snapshots,
    IRecalcSignal signal,
    IOptions<AuthOptions> authOptions,
    IOptions<SeedOptions> seedOptions,
    TimeProvider time,
    ILogger<DataSeeder> logger)
{
    /// <summary>Минимальная длина пароля вне Development</summary>
    public const int MinPasswordLength = 12;

    /// <summary>Инициализировать БД</summary>
    public async Task InitializeAsync(bool requireStrongPasswords, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO app_state (id) VALUES (1) ON CONFLICT (id) DO NOTHING", cancellationToken);
        if (!await db.ModelRevisions.AnyAsync(cancellationToken))
        {
            var settings = ModelSettingsDto.From(ModelSettings.Default(FeatureCatalog.All));
            db.ModelRevisions.Add(new ModelRevisionEntity
            {
                Id = 1,
                SettingsJson = AppJson.Serialize(settings),
                SchemaVersion = settings.SchemaVersion,
                CreatedAt = now,
                CreatedBy = "system",
                Comment = "Стартовые веса (экспертные, не обучены)",
            });
            await db.SaveChangesAsync(cancellationToken);
            await db.AppState.Where(s => s.Id == 1 && s.LatestModelRevisionId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LatestModelRevisionId, 1L), cancellationToken);
            logger.LogInformation("Default model revision 1 created");
        }

        await EnsureUsersAsync(requireStrongPasswords, cancellationToken);
        await SeedSnapshotAsync(now, cancellationToken);
    }

    /// <summary>Создаёт bootstrap-пользователей, если их ещё нет</summary>
    private async Task EnsureUsersAsync(bool requireStrong, CancellationToken cancellationToken)
    {
        var o = authOptions.Value;
        foreach (var (login, password, role) in new[] { (o.AdminLogin, o.AdminPassword, Roles.Admin), (o.AnalystLogin, o.AnalystPassword, Roles.Analyst) })
        {
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password)) continue;
            var normalized = login.Trim().ToLowerInvariant();
            if (await users.ExistsAsync(normalized, cancellationToken)) continue;
            if (requireStrong && password.Length < MinPasswordLength)
                throw new InvalidOperationException($"Пароль bootstrap-пользователя с ролью {role} короче {MinPasswordLength} символов.");
            var user = new UserEntity { Login = login.Trim(), LoginNormalized = normalized, Role = role, CreatedAt = time.GetUtcNow() };
            user.PasswordHash = passwords.Hash(user, password);
            users.Add(user);
            logger.LogInformation("Bootstrap user created with role {Role}", role);
        }
        await users.CommitAsync(cancellationToken);
        if (!await users.AnyAdminAsync(cancellationToken))
            logger.LogWarning("No admin user exists: set BOOTSTRAP_ADMIN_LOGIN and BOOTSTRAP_ADMIN_PASSWORD");
    }

    private async Task SeedSnapshotAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var seed = seedOptions.Value;
        if (string.IsNullOrWhiteSpace(seed.File) || await snapshots.AnyAsync(cancellationToken)) return;
        if (!File.Exists(seed.File))
        {
            logger.LogWarning("Seed file not found; panel starts without data until a snapshot is uploaded");
            return;
        }
        var raw = await File.ReadAllBytesAsync(seed.File, cancellationToken);
        var parsed = SnapshotParser.Parse(raw);
        if (!parsed.IsValid)
        {
            logger.LogError("Seed snapshot invalid: {Errors}", string.Join(", ", parsed.ErrorCounts.Select(kv => $"{kv.Key}={kv.Value}")));
            return;
        }
        var asOf = parsed.SourceAsOf ?? seed.SourceAsOf;
        var provenance = AppJson.Serialize(new
        {
            via = "seed",
            label = seed.Label,
            file = Path.GetFileName(seed.File),
            sourceAsOfFrom = parsed.SourceAsOf is not null ? "envelope" : seed.SourceAsOf is not null ? "configuration" : "assumed-received-at",
        });
        var withFlag = parsed with { Synthetic = parsed.Synthetic || seed.Synthetic };
        var result = await snapshots.StoreAsync(withFlag, raw, new SnapshotRequestMeta(asOf ?? now, asOf is null, "seed", provenance, null), cancellationToken);
        logger.LogInformation("Seed snapshot {Outcome}: revision {Revision}, {Accounts}/{Services}/{Transactions}", result.Outcome, result.Revision,
            parsed.Data.Accounts.Count, parsed.Data.Services.Count, parsed.Data.Transactions.Count);
        signal.Notify();
    }
}
