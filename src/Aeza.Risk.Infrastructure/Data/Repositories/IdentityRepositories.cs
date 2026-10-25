using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Json;

using Microsoft.EntityFrameworkCore;

namespace Aeza.Risk.Infrastructure.Data.Repositories;

/// <inheritdoc />
public sealed class UserRepository(AppDbContext db) : IUserRepository
{
    /// <inheritdoc />
    public Task<UserEntity?> FindByLoginAsync(string normalizedLogin, CancellationToken cancellationToken) =>
        db.Users.SingleOrDefaultAsync(u => u.LoginNormalized == normalizedLogin, cancellationToken);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string normalizedLogin, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.LoginNormalized == normalizedLogin, cancellationToken);

    /// <inheritdoc />
    public Task<bool> AnyAdminAsync(CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.Role == Roles.Admin, cancellationToken);

    /// <inheritdoc />
    public void Add(UserEntity user) => db.Users.Add(user);

    /// <inheritdoc />
    public Task RevokeSessionsAsync(long userId, DateTimeOffset at, CancellationToken cancellationToken) =>
        db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, at), cancellationToken);

    /// <inheritdoc />
    public Task CommitAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <inheritdoc />
public sealed class AuditLog(AppDbContext db, TimeProvider time) : IAuditLog
{
    /// <inheritdoc />
    public async Task WriteAsync(string actor, string action, object details, CancellationToken cancellationToken)
    {
        db.Audit.Add(new AuditEntry { At = time.GetUtcNow(), Actor = actor, Action = action, Details = AppJson.Serialize(details) });
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <inheritdoc />
public sealed class RecalcJobRepository(AppDbContext db) : IRecalcJobRepository
{
    /// <inheritdoc />
    public Task<RecalcJob?> GetAsync(long id, CancellationToken cancellationToken) =>
        db.RecalcJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
}

/// <summary>Постановка durable-задания пересчёта в текущей транзакции контекста</summary>
internal static class RecalcQueue
{
    /// <summary>Вызывающий делает SaveChanges/Commit</summary>
    public static RecalcJob Enqueue(AppDbContext db, string reason, DateTimeOffset now, string? requestedBy, long? sourceSnapshotId = null, long? modelRevisionId = null)
    {
        var job = new RecalcJob
        {
            Reason = reason,
            Status = JobStatus.Pending,
            CreatedAt = now,
            AvailableAt = now,
            RequestedBy = requestedBy,
            SourceSnapshotId = sourceSnapshotId,
            ModelRevisionId = modelRevisionId,
        };
        db.RecalcJobs.Add(job);
        return job;
    }
}
