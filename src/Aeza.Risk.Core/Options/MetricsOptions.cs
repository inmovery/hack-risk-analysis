using System.ComponentModel.DataAnnotations;

using JetBrains.Annotations;

namespace Aeza.Risk.Core.Options;

/// <summary>Бизнес-метрики на OpenTelemetry (секция OpenTelemetry, как в Abdt.Infrastructure.OpenTelemetry.Metrics)</summary>
[UsedImplicitly]
public sealed class MetricsOptions
{
    /// <summary>Имя секции</summary>
    public const string Section = "OpenTelemetry";

    /// <summary>Имя Meter бизнес-метрик (регистрируется в MeterProvider)</summary>
    [Required(AllowEmptyStrings = false)]
    public string MeterName { get; set; } = "Aeza.Risk";

    /// <summary>Признак использования OpenTelemetry</summary>
    public bool EnableOpenTelemetry { get; set; } = true;

    /// <summary>Экспортёр и конечная точка Prometheus</summary>
    public bool EnablePrometheus { get; set; } = true;

    /// <summary>Путь конечной точки сбора метрик (без авторизации; наружу закрывается на уровне сети/ingress)</summary>
    [Required(AllowEmptyStrings = false)]
    public string MetricsEndpointPath { get; set; } = "/metrics";

    /// <summary>Период сверки сводки активного поколения с БД, секунды (несколько реплик видят одно и то же)</summary>
    [Range(1, 3600)]
    public int RefreshSeconds { get; set; } = 15;
}
