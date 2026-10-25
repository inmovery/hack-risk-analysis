namespace Aeza.Risk.Core.Simulation;

/// <summary>Параметры snapshot системы: применяются при пересоздании snapshot системы</summary>
/// <param name="Source">organizer — тестовые данные организаторов и дополнительные клиенты; generated — сгенерированный snapshot системы с историей</param>
/// <param name="Seed">Seed генератора</param>
/// <param name="ExtraCustomers">Дополнительных клиентов к данным организаторов</param>
/// <param name="Customers">Клиентов сгенерированного snapshot системы</param>
/// <param name="HistoryDays">Дней истории сгенерированного snapshot системы</param>
public sealed record WorldSettings(string Source, int Seed, int ExtraCustomers, int Customers, int HistoryDays)
{
    public const string Organizer = "organizer";
    public const string Generated = "generated";
}

/// <summary>Настройки симуляции</summary>
/// <param name="Running">Snapshot системы живёт (false — пауза: события не генерируются)</param>
/// <param name="TickSeconds">Такт генерации событий, секунд</param>
/// <param name="ScenarioStepSeconds">Шаг сценария, секунд</param>
/// <param name="Chaos">Сбои доставки: перестановки и повторы событий</param>
/// <param name="World">Параметры snapshot системы для следующего пересоздания</param>
public sealed record SimulationSettings(bool Running, int TickSeconds, int ScenarioStepSeconds, bool Chaos, WorldSettings World)
{
    public static readonly SimulationSettings Default = new(true, 5, 15, true, new WorldSettings(WorldSettings.Organizer, 42, 50, 300, 90));
}

/// <summary>Отчёт симулятора о своём состоянии</summary>
/// <param name="AgentVersion">Версия симулятора</param>
/// <param name="WorldRevision">Ревизия snapshot системы, с которой работает симулятор</param>
/// <param name="RunId">Идентификатор snapshot системы</param>
/// <param name="Phase">idle | bootstrap | history | live | paused | error</param>
/// <param name="SimClock">Часы snapshot системы</param>
/// <param name="Customers">Клиентов в snapshot системы</param>
/// <param name="Services">Услуг</param>
/// <param name="LiveServices">Действующих услуг</param>
/// <param name="EventsGenerated">Событий сгенерировано</param>
/// <param name="EventsSent">Событий отправлено и подтверждено</param>
/// <param name="Backlog">Событий в очереди на отправку</param>
/// <param name="LastSentAt">Последняя успешная отправка</param>
/// <param name="LastError">Последняя ошибка</param>
/// <param name="LastNarration">Последний шаг сценария</param>
/// <param name="ScenarioHandled">Обработанных запросов сценария</param>
public sealed record SimulationAgentStatus(
    string AgentVersion, long WorldRevision, string? RunId, string Phase, DateTimeOffset? SimClock, int Customers, int Services, int LiveServices,
    long EventsGenerated, long EventsSent, long Backlog, DateTimeOffset? LastSentAt, string? LastError, string? LastNarration, long ScenarioHandled);

/// <summary>Проверка настроек симуляции</summary>
public static class SimulationRules
{
    public static readonly IReadOnlyList<string> Phases = ["idle", "bootstrap", "history", "live", "paused", "error"];

    public static IReadOnlyDictionary<string, string[]> Validate(SimulationSettings s)
    {
        var errors = new Dictionary<string, string[]>();
        void Check(bool ok, string path, string message)
        {
            if (!ok) errors[path] = [message];
        }

        Check(s.TickSeconds is >= 1 and <= 60, "tickSeconds", "Такт — от 1 до 60 секунд.");
        Check(s.ScenarioStepSeconds is >= 5 and <= 300, "scenarioStepSeconds", "Шаг сценария — от 5 до 300 секунд.");
        if (s.World is not { } w)
        {
            errors["world"] = ["Нужны параметры snapshot системы."];
            return errors;
        }
        Check(w.Source is WorldSettings.Organizer or WorldSettings.Generated, "world.source", "Источник snapshot системы: organizer или generated.");
        Check(w.Seed >= 1, "world.seed", "Seed — положительное число.");
        Check(w.ExtraCustomers is >= 0 and <= 2000, "world.extraCustomers", "Дополнительных клиентов — от 0 до 2000.");
        Check(w.Customers is >= 10 and <= 3000, "world.customers", "Клиентов сгенерированного snapshot системы — от 10 до 3000.");
        Check(w.HistoryDays is >= 0 and <= 365, "world.historyDays", "История — от 0 до 365 дней.");
        return errors;
    }
}
