using Aeza.Risk.Core.Live;

namespace Aeza.Risk.Core.Abstractions;

/// <summary>Сигнал worker'у «появилась работа» (durable часть — строка recalc_jobs в БД)</summary>
public interface IRecalcSignal
{
    /// <summary>Разбудить worker</summary>
    void Notify();
}

/// <summary>Рассылка live-событий подписчикам панели</summary>
public interface ILiveNotifier
{
    /// <summary>Поставлено задание пересчёта</summary>
    Task JobQueuedAsync(JobQueuedEvent queued, CancellationToken cancellationToken);

    /// <summary>Опубликовано поколение</summary>
    Task GenerationPublishedAsync(GenerationPublishedEvent published, CancellationToken cancellationToken);

    /// <summary>Пересчёт окончательно не удался</summary>
    Task RecalcFailedAsync(RecalcFailedEvent failed, CancellationToken cancellationToken);

    /// <summary>Источник изменился</summary>
    Task SourceChangedAsync(SourceChangedEvent changed, CancellationToken cancellationToken);
}

/// <summary>Live-соединения сессий: закрываются при logout и истечении сессии</summary>
public interface ILiveSessions
{
    /// <summary>Закрыть все соединения сессии; возвращает их число</summary>
    int AbortSession(string sid);
}

