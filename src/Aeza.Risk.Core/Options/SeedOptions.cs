using JetBrains.Annotations;

namespace Aeza.Risk.Core.Options;

/// <summary>Первичный импорт снимка в пустую БД (секция Seed)</summary>
[UsedImplicitly]
public sealed class SeedOptions
{
    /// <summary>Имя секции</summary>
    public const string Section = "Seed";

    /// <summary>Путь к файлу снимка для первичного импорта (только при пустой БД)</summary>
    public string? File { get; set; }

    /// <summary>Момент выгрузки снимка; для набора организаторов — опорная дата из его README</summary>
    public DateTimeOffset? SourceAsOf { get; set; }

    /// <summary>Метка происхождения в provenance</summary>
    public string Label { get; set; } = "seed";

    /// <summary>Пометить снимок как синтетический</summary>
    public bool Synthetic { get; set; }
}
