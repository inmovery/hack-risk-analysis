using System.ComponentModel.DataAnnotations;

using JetBrains.Annotations;

namespace Aeza.Risk.Core.Options;

/// <summary>Работа только на событиях симулятора (секция Simulation)</summary>
[UsedImplicitly]
public sealed class SimulationOptions
{
    /// <summary>Имя секции</summary>
    public const string Section = "Simulation";

    /// <summary>Данные приходят только событиями: при пустой БД создаётся пустая база вместо загрузки тестовых данных</summary>
    public bool EventsOnly { get; set; }

    /// <summary>Симулятор считается на связи, если последний отчёт моложе, секунд</summary>
    [Range(5, 600)]
    public int AgentOnlineSeconds { get; set; } = 30;
}
