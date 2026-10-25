using Aeza.Risk.Core;

using FluentValidation;

namespace Aeza.Risk.Feature.Customers.List;

/// <summary>Валидатор для <see cref="CustomerListQuery"/>: допустимые поля сортировки, направления, фильтры и размеры страниц</summary>
internal sealed class CustomerListQueryValidator : AbstractValidator<CustomerListQuery>
{
    /// <summary>Допустимые поля сортировки</summary>
    public static readonly string[] SortFields = ["id", "email", "balance", "totalReplenished", "riskScore", "atRisk", "fullChurnRisk", "paymentChange"];

    private static readonly string[] Levels = ["high", "medium", "low", "unknown"];
    private static readonly string[] States = ["complete", "partial", "no_renewable_services", "insufficient_data"];

    public CustomerListQueryValidator()
    {
        RuleFor(q => q.SortBy)
            .Must(s => s is null || SortFields.Contains(s))
            .WithMessage($"Допустимо: {string.Join(", ", SortFields)}.")
            .OverridePropertyName("sortBy");

        RuleFor(q => q.SortDirection)
            .Must(d => d is null or "asc" or "desc")
            .WithMessage("Допустимо: asc, desc.")
            .OverridePropertyName("sortDirection");

        RuleFor(q => q.RiskLevel)
            .Must(levels => levels is null || levels.All(Levels.Contains))
            .WithMessage("Допустимо: high, medium, low, unknown.")
            .OverridePropertyName("riskLevel");

        RuleFor(q => q.ScoreState)
            .Must(s => s is null || States.Contains(s))
            .WithMessage($"Допустимо: {string.Join(", ", States)}.")
            .OverridePropertyName("scoreState");

        RuleFor(q => q.FullChurnLevel)
            .Must(levels => levels is null || levels.All(Levels.Contains))
            .WithMessage("Допустимо: high, medium, low, unknown.")
            .OverridePropertyName("fullChurnLevel");

        RuleFor(q => q.FullChurnState)
            .Must(s => s is null || FullChurnStates.All.Contains(s))
            .WithMessage($"Допустимо: {string.Join(", ", FullChurnStates.All)}.")
            .OverridePropertyName("fullChurnState");

        RuleFor(q => q.PaymentTrend)
            .Must(t => t is null || t.All(PaymentTrendStates.All.Contains))
            .WithMessage($"Допустимо: {string.Join(", ", PaymentTrendStates.All)}.")
            .OverridePropertyName("paymentTrend");

        RuleFor(q => q.Page)
            .Must(p => p is null or >= 1)
            .WithMessage("page ≥ 1.")
            .OverridePropertyName("page");

        RuleFor(q => q.PageSize)
            .Must(p => p is null or >= 1 and <= 200)
            .WithMessage("pageSize от 1 до 200.")
            .OverridePropertyName("pageSize");

        RuleFor(q => q.Search)
            .Must(s => s is null || s.Length <= 320)
            .WithMessage("Слишком длинный запрос.")
            .OverridePropertyName("search");
    }
}
