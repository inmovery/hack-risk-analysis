using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Source;

namespace Aeza.Risk.Core.Abstractions;

/// <summary>Итог сохранения снимка</summary>
public enum StoreOutcome
{
    /// <summary>Принят, поставлен пересчёт</summary>
    Accepted,

    /// <summary>Тот же контент уже принят</summary>
    Idempotent,

    /// <summary>Снимок старше уже принятого</summary>
    Stale,

    /// <summary>Ревизия не совпала с If-Match</summary>
    PreconditionFailed,
}

/// <summary>Результат сохранения снимка</summary>
public sealed record StoreResult(StoreOutcome Outcome, long? Revision, long? JobId, long? LatestRevision);

/// <summary>Метаданные запроса загрузки снимка</summary>
public sealed record SnapshotRequestMeta(DateTimeOffset SourceAsOf, bool SourceAsOfAssumed, string Actor, string Provenance, long? IfMatchRevision);

/// <summary>Снимки источника</summary>
public interface ISnapshotRepository
{
    /// <summary>Атомарная полная замена снимка: сырой файл, канонические строки, наблюдённые изменения и job — одна транзакция</summary>
    Task<StoreResult> StoreAsync(ParsedSnapshot parsed, byte[] raw, SnapshotRequestMeta meta, CancellationToken cancellationToken);

    /// <summary>Есть ли хотя бы один снимок</summary>
    Task<bool> AnyAsync(CancellationToken cancellationToken);

    /// <summary>Текущий снимок или null</summary>
    Task<SourceSnapshotEntity?> GetCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Снимок по идентификатору</summary>
    Task<SourceSnapshotEntity> GetAsync(long snapshotId, CancellationToken cancellationToken);

    /// <summary>Последние снимки и признак текущего</summary>
    Task<IReadOnlyList<(SourceSnapshotEntity Snapshot, bool Active)>> ListAsync(int take, CancellationToken cancellationToken);

    /// <summary>Входные данные движка из снимка</summary>
    Task<SourceData> LoadDataAsync(long snapshotId, CancellationToken cancellationToken);

    /// <summary>Текущее состояние источника (снимок ⊕ события)</summary>
    Task<SourceData> LoadCurrentAsync(CancellationToken cancellationToken);
}
