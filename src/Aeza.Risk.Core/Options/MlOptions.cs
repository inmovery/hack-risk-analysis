using JetBrains.Annotations;

namespace Aeza.Risk.Core.Options;

/// <summary>ML по умолчанию (секция Ml)</summary>
[UsedImplicitly]
public sealed class MlOptions
{
    /// <summary>Имя секции</summary>
    public const string Section = "Ml";

    /// <summary>Пакет модели, который включается, если моделей ещё нет; относительный путь — от каталога сервиса; пусто — без модели по умолчанию</summary>
    public string? DefaultBundle { get; set; }
}
