using System.ComponentModel.DataAnnotations;

using JetBrains.Annotations;

namespace Aeza.Risk.Core.Options;

/// <summary>Основные параметры приложения (секция App в appsettings.json, переменные окружения ENV_App__*)</summary>
[UsedImplicitly]
public sealed class AppOptions
{
    /// <summary>Имя секции</summary>
    public const string Section = "App";

    /// <summary>Строка подключения к PostgreSQL (роль приложения: только DML)</summary>
    [Required(AllowEmptyStrings = false)]
    public string DbConnectionString { get; set; } = "";

    /// <summary>Применять миграции при старте веб-приложения (только для разработки и тестов; в Compose — отдельный Migrator)</summary>
    public bool AutoMigrate { get; set; }
}
