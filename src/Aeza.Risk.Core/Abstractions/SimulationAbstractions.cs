using Aeza.Risk.Core.Simulation;

namespace Aeza.Risk.Core.Abstractions;

/// <summary>Текущий snapshot системы</summary>
/// <param name="Revision">Ревизия snapshot системы: растёт при пересоздании</param>
/// <param name="StartedAt">Момент создания snapshot системы: от него симулятор детерминированно строит snapshot системы</param>
/// <param name="Settings">Параметры, с которыми snapshot системы создан</param>
/// <param name="ResetAt">Последнее пересоздание</param>
/// <param name="ResetBy">Кем пересоздан</param>
public sealed record SimulationWorldView(long Revision, DateTimeOffset StartedAt, WorldSettings Settings, DateTimeOffset? ResetAt, string? ResetBy);

/// <summary>Связь с симулятором</summary>
/// <param name="Online">Отчёт моложе порога</param>
/// <param name="ReportedAt">Последний отчёт</param>
/// <param name="Status">Последний отчёт симулятора</param>
public sealed record SimulationAgentView(bool Online, DateTimeOffset? ReportedAt, SimulationAgentStatus? Status);

/// <summary>Что ждёт применения</summary>
/// <param name="WorldChanged">Параметры snapshot системы в настройках отличаются от текущего snapshot системы — применятся после пересоздания</param>
public sealed record SimulationPendingView(bool WorldChanged);

/// <summary>Симуляция для панели</summary>
/// <param name="EventsOnly">Анализатор работает только на событиях</param>
/// <param name="Settings">Настройки</param>
/// <param name="Revision">Ревизия настроек</param>
/// <param name="UpdatedAt">Изменены</param>
/// <param name="UpdatedBy">Кем</param>
/// <param name="World">Текущий snapshot системы</param>
/// <param name="ScenarioRequest">Запросов сценария</param>
/// <param name="Agent">Связь с симулятором</param>
/// <param name="Pending">Что ждёт применения</param>
public sealed record SimulationView(
    bool EventsOnly, SimulationSettings Settings, long Revision, DateTimeOffset UpdatedAt, string? UpdatedBy, SimulationWorldView World,
    long ScenarioRequest, SimulationAgentView Agent, SimulationPendingView Pending);

/// <summary>Инструкции для симулятора</summary>
/// <param name="EventsOnly">false — симуляция выключена, симулятор ждёт</param>
/// <param name="Settings">Настройки</param>
/// <param name="World">Snapshot системы, который нужно вести</param>
/// <param name="ScenarioRequest">Номер последнего запроса сценария</param>
/// <param name="Commands">События, собранные в панели и ещё не применённые симулятором</param>
public sealed record SimulationAgentInstructions(
    bool EventsOnly, SimulationSettings Settings, SimulationWorldView World, long ScenarioRequest, IReadOnlyList<SimulationAgentCommand> Commands);

/// <summary>Клиент или услуга для события не найдены в данных анализатора</summary>
/// <param name="path">Поле запроса</param>
/// <param name="message">Описание</param>
public sealed class ComposerTargetException(string path, string message) : Exception(message)
{
    /// <summary>Поле запроса</summary>
    public string Path { get; } = path;
}

/// <summary>Настройки и управление симуляцией</summary>
public interface ISimulationStore
{
    /// <summary>Симуляция для панели</summary>
    Task<SimulationView> GetAsync(CancellationToken cancellationToken);

    /// <summary>Сохранить настройки (без пересоздания snapshot системы)</summary>
    Task<SimulationView> SaveSettingsAsync(SimulationSettings settings, string actor, CancellationToken cancellationToken);

    /// <summary>Пересоздать snapshot системы: удалить данные анализатора, создать пустую базу, ревизия snapshot системы + 1</summary>
    Task<SimulationView> ResetAsync(string actor, CancellationToken cancellationToken);

    /// <summary>Запросить сценарий</summary>
    Task<SimulationView> RequestScenarioAsync(string actor, CancellationToken cancellationToken);

    /// <summary>Инструкции для симулятора</summary>
    Task<SimulationAgentInstructions> GetInstructionsAsync(CancellationToken cancellationToken);

    /// <summary>Принять отчёт симулятора</summary>
    Task ReportAsync(SimulationAgentStatus status, CancellationToken cancellationToken);

    /// <summary>Создать пустую базу, если данных ещё нет (режим только событий)</summary>
    Task EnsureEmptyBaseAsync(CancellationToken cancellationToken);

    /// <summary>Поставить событие из конструктора в очередь симулятора; клиент и услуга проверяются по текущим данным
    /// (<see cref="ComposerTargetException"/>)</summary>
    Task<SimulationCommandView> EnqueueCommandAsync(string type, long accountId, long? serviceId, IReadOnlyDictionary<string, object> data, string actor,
        CancellationToken cancellationToken);

    /// <summary>Последние события конструктора</summary>
    Task<IReadOnlyList<SimulationCommandView>> ListCommandsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Итоги событий у симулятора</summary>
    Task ReportCommandsAsync(IReadOnlyList<SimulationCommandResult> results, CancellationToken cancellationToken);
}
