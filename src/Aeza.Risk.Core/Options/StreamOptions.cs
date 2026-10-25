using System.ComponentModel.DataAnnotations;

using JetBrains.Annotations;

namespace Aeza.Risk.Core.Options;

/// <summary>Поток событий источника (секция Events)</summary>
[UsedImplicitly]
public sealed class StreamOptions
{
    /// <summary>Имя секции</summary>
    public const string Section = "Events";

    /// <summary>Пересчёт после события откладывается на столько секунд: пачка и задержка до 5 с схлопываются в один расчёт</summary>
    [Range(0, 300)]
    public int DebounceSeconds { get; set; } = 3;

    /// <summary>Сколько дней хранить журнал событий (факты исходов сроков хранятся отдельно)</summary>
    [Range(30, 3650)]
    public int RetentionDays { get; set; } = 400;

    /// <summary>Поток считается «молчащим», если последнее событие старше, минут</summary>
    [Range(1, 10080)]
    public int SilentAfterMinutes { get; set; } = 60;
}
