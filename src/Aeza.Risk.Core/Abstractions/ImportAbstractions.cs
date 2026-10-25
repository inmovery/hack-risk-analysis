namespace Aeza.Risk.Core.Abstractions;

/// <summary>Пакетный импорт истории событий (коннектор batch-import)</summary>
/// <param name="Id">Номер импорта</param>
/// <param name="CreatedAt">Загружен</param>
/// <param name="Actor">Кто загрузил</param>
/// <param name="Connector">Входящий коннектор</param>
/// <param name="Synthetic">Синтетика</param>
/// <param name="Status">uploading | pending | running | succeeded | failed</param>
/// <param name="Bytes">Размер файла</param>
/// <param name="ChunksTotal">Кусков по 5000 строк</param>
/// <param name="ChunksDone">Применено кусков</param>
/// <param name="EventsTotal">Строк-событий</param>
/// <param name="Applied">Изменили состояние</param>
/// <param name="Recorded">Записаны в журнал доменных событий</param>
/// <param name="NoChange">То же состояние</param>
/// <param name="Stale">Устаревшие (в том числе раньше даты базы)</param>
/// <param name="Duplicates">Повтор eventId</param>
/// <param name="Rejected">Отклонены (ошибки полей, не JSON)</param>
/// <param name="Errors">Первые ошибки: номер строки и путь поля, без значений</param>
/// <param name="ErrorCode">Причина неудачи</param>
/// <param name="StartedAt">Начало применения</param>
/// <param name="FinishedAt">Окончание</param>
/// <param name="JobId">Пересчёт после импорта</param>
public sealed record ImportView(
    long Id, DateTimeOffset CreatedAt, string Actor, string Connector, bool Synthetic, string Status, long Bytes, int ChunksTotal, int ChunksDone,
    long EventsTotal, long Applied, long Recorded, long NoChange, long Stale, long Duplicates, long Rejected, IReadOnlyList<string> Errors,
    string? ErrorCode, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, long? JobId);

/// <summary>Файл импорта не принят</summary>
/// <param name="code">Код (line_too_long, too_many_lines, empty, invalid_gzip)</param>
/// <param name="message">Описание для пользователя</param>
public sealed class ImportRejectedException(string code, string message) : Exception(message)
{
    /// <summary>Код ошибки</summary>
    public string Code { get; } = code;
}

/// <summary>Пакетные импорты</summary>
public interface ISourceImports
{
    /// <summary>Сохранить файл JSONL (можно gzip) кусками и поставить импорт в очередь</summary>
    Task<ImportView> UploadAsync(Stream body, string actor, string connector, bool synthetic, CancellationToken cancellationToken);

    /// <summary>Импорт по номеру</summary>
    Task<ImportView?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Последние импорты (новые сверху)</summary>
    Task<IReadOnlyList<ImportView>> ListAsync(int limit, CancellationToken cancellationToken);
}
