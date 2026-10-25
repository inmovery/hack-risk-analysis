namespace Aeza.Risk.Core.Live;

/// <summary>Имена событий live-канала (SignalR)</summary>
public static class LiveEvents
{
    /// <summary>Опубликовано новое поколение результатов</summary>
    public const string GenerationPublished = "generationPublished";

    /// <summary>Пересчёт окончательно не удался (last-good поколение сохранено)</summary>
    public const string RecalcFailed = "recalcFailed";

    /// <summary>Поставлено задание пересчёта</summary>
    public const string JobQueued = "jobQueued";

    /// <summary>Источник изменился (пакет событий принят)</summary>
    public const string SourceChanged = "sourceChanged";

    /// <summary>Канал событий этой реплики восстановлен после разрыва: события могли потеряться, клиент перечитывает всё</summary>
    public const string Resync = "resync";
}

/// <summary>Принят пакет событий источника (коды и числа, без PII)</summary>
public sealed record SourceChangedEvent(long Seq, int Applied, int Received, IReadOnlyDictionary<string, int> Changes, bool Synthetic, string Origin);

/// <summary>Событие публикации поколения (только идентификаторы, без PII)</summary>
public sealed record GenerationPublishedEvent(long GenerationId, long SourceRevision, long SourceSeq, long ModelRevision, DateTimeOffset AsOf, string Reason, DateTimeOffset PublishedAt);

/// <summary>Событие окончательной ошибки пересчёта</summary>
public sealed record RecalcFailedEvent(long JobId, string ErrorCode, long? ModelRevision, long? SourceRevision);

/// <summary>Событие постановки задания пересчёта</summary>
public sealed record JobQueuedEvent(long JobId, string Reason, long? ModelRevision, long? SourceRevision);
