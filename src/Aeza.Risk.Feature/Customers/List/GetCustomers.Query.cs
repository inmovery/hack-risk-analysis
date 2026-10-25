using Aeza.Risk.Core.Abstractions;

using JetBrains.Annotations;

namespace Aeza.Risk.Feature.Customers.List;

/// <summary>Параметры списка клиентов (query string)</summary>
/// <param name="SortBy">id | email | balance | totalReplenished | riskScore | atRisk | fullChurnRisk | paymentChange (по умолчанию riskScore)</param>
/// <param name="SortDirection">asc | desc (по умолчанию desc)</param>
/// <param name="RiskLevel">Фильтр уровней: high, medium, low, unknown (повторяемый)</param>
/// <param name="ScoreState">Фильтр состояния оценки</param>
/// <param name="Flag">Фильтр флага качества данных</param>
/// <param name="Search">ID или часть email</param>
/// <param name="Page">Номер страницы (с 1)</param>
/// <param name="PageSize">Размер страницы (1–200, по умолчанию 50)</param>
/// <param name="GenerationId">Явное поколение (по умолчанию — активное)</param>
/// <param name="FullChurnLevel">Фильтр уровней индекса «уход целиком» (повторяемый)</param>
/// <param name="FullChurnState">Состояние индекса «уход целиком»: scored, partial, churned (уже ушли), blocked, no_services, insufficient_data</param>
/// <param name="PaymentTrend">Тренд платежей (повторяемый): decline, stable, growth, no_payments, insufficient_history</param>
[PublicAPI]
public sealed record CustomerListQuery(
    string? SortBy,
    string? SortDirection,
    string[]? RiskLevel,
    string? ScoreState,
    string? Flag,
    string? Search,
    int? Page,
    int? PageSize,
    long? GenerationId,
    string[]? FullChurnLevel,
    string? FullChurnState,
    string[]? PaymentTrend)
{
    /// <summary>Поле сортировки по умолчанию</summary>
    public const string DefaultSortBy = "riskScore";

    /// <summary>Проверенные критерии с подставленными значениями по умолчанию</summary>
    public CustomerListCriteria ToCriteria() => new(
        SortBy ?? DefaultSortBy,
        (SortDirection ?? "desc") == "asc",
        RiskLevel,
        ScoreState,
        Flag,
        Search,
        Page ?? 1,
        PageSize ?? 50,
        FullChurnLevel,
        FullChurnState,
        PaymentTrend);
}
