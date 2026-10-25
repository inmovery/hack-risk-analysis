using Aeza.Risk.Core;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Generations;

using JetBrains.Annotations;

namespace Aeza.Risk.Feature.Customers.List;

/// <summary>Индекс «уход целиком» в строке рейтинга</summary>
[PublicAPI]
public sealed record FullChurnRow(decimal? Score, string Level, string State);

/// <summary>Тренд платежей в строке рейтинга</summary>
[PublicAPI]
public sealed record PaymentTrendRow(string State, decimal? ChangePct, long CurrentMinor, long PreviousMinor);

/// <summary>Типы оттока помимо непродления</summary>
[PublicAPI]
public sealed record ChurnRow(FullChurnRow FullChurn, PaymentTrendRow PaymentTrend);

/// <summary>Строка рейтинга клиентов</summary>
[PublicAPI]
public sealed record CustomerListItem(
    long Id, string Email, long? Balance, long? TotalReplenished, long? WithdrawBalance, long? BonusBalance, string? BonusState,
    decimal? RiskScore, string RiskLevel, string ScoreState, ReasonDto PrimaryReason, string[] ReasonCodes, string[] Flags,
    MoneyDto Money, ServiceCountsDto Services, ChurnRow Churn);

/// <summary>Страница рейтинга клиентов; все строки — из одного поколения</summary>
[PublicAPI]
public sealed record CustomerListResponse(
    GenerationInfo Generation, PendingInfo? Pending, int Page, int PageSize, int Total, string SortBy, string SortDirection, IReadOnlyList<CustomerListItem> Items);

/// <summary>Маппинг строки результата в строку рейтинга</summary>
internal static class GetCustomersMappingExtensions
{
    private static readonly Dictionary<string, string> Titles = FeatureCatalog.All.ToDictionary(f => f.Key, f => f.Title);

    public static CustomerListItem ToListItem(this CustomerResultEntity r) => new(
        r.CustomerId, r.Email, r.Balance, r.TotalReplenished, r.WithdrawBalance, r.BonusBalance, r.BonusState,
        r.Score, r.Level, r.ScoreState,
        new ReasonDto(r.PrimaryReasonCode, Titles.ContainsKey(r.PrimaryReasonCode) ? r.PrimaryReasonCode : "",
            Titles.GetValueOrDefault(r.PrimaryReasonCode, r.Score is null ? "Нет оценки" : "Признаков риска нет"),
            r.PrimaryReasonText, r.PrimaryReasonServiceId, 0, 0, r.PrimaryReasonContribution, new Dictionary<string, object?>()),
        r.ReasonCodes, r.Flags, new MoneyDto(r.PlannedMinor, r.UnfundedMinor, r.AtRiskMinor),
        new ServiceCountsDto(r.ServicesTotal, r.ServicesRenewable, r.ServicesScored),
        new ChurnRow(new FullChurnRow(r.FullChurnScore, r.FullChurnLevel, r.FullChurnState),
            new PaymentTrendRow(r.PaymentTrend, r.PaymentChangePct, r.PaidCurrentMinor, r.PaidPreviousMinor)));
}
