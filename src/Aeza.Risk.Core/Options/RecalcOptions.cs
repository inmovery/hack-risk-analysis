using System.ComponentModel.DataAnnotations;

using JetBrains.Annotations;

namespace Aeza.Risk.Core.Options;

/// <summary>Параметры durable-пересчёта (секция Recalc)</summary>
[UsedImplicitly]
public sealed class RecalcOptions
{
    /// <summary>Имя секции</summary>
    public const string Section = "Recalc";

    /// <summary>Попыток до перевода задания в failed</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Аренда выполняющегося задания, секунды</summary>
    [Range(10, 3600)]
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Период опроса очереди без сигнала, секунды</summary>
    [Range(1, 300)]
    public int PollSeconds { get; set; } = 2;

    /// <summary>Сколько последних поколений хранить (плюс активное)</summary>
    [Range(2, 1000)]
    public int KeepGenerations { get; set; } = 20;

    /// <summary>Период проверки «пора пересчитать по времени», секунды</summary>
    [Range(1, 3600)]
    public int ClockCheckSeconds { get; set; } = 15;

    /// <summary>Таймаут теневого инференса ML за поколение, секунды: дольше — прогнозов нет, правила не затронуты</summary>
    [Range(1, 300)]
    public int MlShadowTimeoutSeconds { get; set; } = 10;

    /// <summary>Выключатель worker'а (для отдельных read-only экземпляров)</summary>
    public bool Enabled { get; set; } = true;
}
