namespace Aeza.Risk.Core.Generations;

/// <summary>Идентификаторы поколения — в каждом аналитическом ответе (согласованность UI/API/metrics)</summary>
public sealed record GenerationInfo(
    long Id, long SourceRevision, long ModelRevision, DateTimeOffset AsOf, DateTimeOffset SourceAsOf, bool SourceAsOfAssumed,
    DateTimeOffset PublishedAt, string ClockMode, string EngineVersion, bool Synthetic, long SourceAgeSeconds, int HorizonDays,
    long SourceSeq, DateTimeOffset SourceUpdatedAt, string SourceKind, long ActivitySeq, long? MlModelId = null, string Scoring = ScoringModes.Rules);

public sealed record PendingInfo(long JobId, string Status, string Reason, long? ModelRevision, long? SourceRevision, DateTimeOffset CreatedAt);

public sealed record LastFailureInfo(long JobId, string? ErrorCode, DateTimeOffset? FinishedAt, long? ModelRevision, long? SourceRevision);

public sealed record ReasonDto(string Code, string FeatureKey, string Title, string Text, long? ServiceId, decimal Value, decimal Weight, decimal Contribution, IReadOnlyDictionary<string, object?> Evidence);

public sealed record MoneyDto(long PlannedRenewalsMinor, long UnfundedMinor, long AtRiskMinor);

public sealed record ServiceCountsDto(int Total, int Renewable, int Scored);



public sealed record AccountDto(long Id, string Email, long? Balance, long? WithdrawBalance, long? TotalReplenished, long? BonusBalance, string? BonusState);

public sealed record FeatureDto(string Key, string Title, decimal? Value, decimal Weight, decimal Contribution, IReadOnlyList<string> MissingInputs, IReadOnlyDictionary<string, object?> Evidence, string Text);

public sealed record TransactionDto(long Id, long ServiceId, long? Amount, string? Status, string? Type, DateTimeOffset? CreatedAt, DateTimeOffset? PerformedAt);

public sealed record ServiceDto(
    long Id, string Name, string? Status, string? PaymentTerm, bool? AutoProlong, DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt, long? Price,
    bool Renewable, bool Scored, decimal? Score, decimal? RawScore, bool Capped, IReadOnlyList<FeatureDto> Features,
    DateTimeOffset? NextDue, int RenewalsInHorizon, DateTimeOffset? FirstDue, MoneyDto Money, IReadOnlyList<string> Flags,
    IReadOnlyList<string> MissingInputs, TransactionDto? LastTransaction);

public sealed record FundingDto(
    bool Known, long FundsMinor, long BalanceAvailableMinor, long BonusAvailableMinor, long BalanceUsedMinor, long BonusUsedMinor,
    bool BonusEligible, string? BonusState, long WithdrawExcludedMinor, DateTimeOffset? FirstUnfundedDue);

/// <summary>Деталь клиента, сохраняемая в поколении (jsonb) — карточка читает ровно то, что посчитал движок</summary>
public sealed record CustomerDetailBody(
    AccountDto Customer, decimal? RiskScore, string RiskLevel, string ScoreState, ReasonDto PrimaryReason, IReadOnlyList<ReasonDto> Reasons,
    IReadOnlyList<ServiceDto> Services, FundingDto Funding, MoneyDto Money, long UnscoredPlannedMinor, IReadOnlyList<string> Flags,
    ServiceCountsDto ServiceCounts, IReadOnlyList<TransactionDto> Transactions, string MoneyFormula, ChurnDetailDto? Churn = null,
    MlShadowDto? MlShadow = null);

/// <summary>Индекс «уход целиком» в детали</summary>
public sealed record FullChurnDto(decimal? Score, string Level, string State, long? WeakestServiceId, string Text);

/// <summary>Тренд платежей в детали</summary>
public sealed record PaymentTrendDto(string State, decimal? ChangePct, long CurrentMinor, long PreviousMinor, int WindowDays, DateTimeOffset? HistoryStart, string Text);

/// <summary>Типы оттока клиента</summary>
public sealed record ChurnDetailDto(FullChurnDto FullChurn, PaymentTrendDto PaymentTrend);


public static class CustomerMapping
{
    public const string MoneyFormula =
        "D = Σ цен продлений в горизонте; покрытие: основной баланс, затем бонусы (если bonusState = unlocked), один раз по всем услугам по сроку; " +
        "U = D − покрытие; G = Σ по оценённым услугам D_услуги × score_услуги / 100 (индекс, не вероятность). withdrawBalance не тратится.";

    public static ReasonDto ToDto(Reason r) => new(r.Code, r.FeatureKey, r.Title, r.Text, r.ServiceId, r.Value, r.Weight, r.Contribution, r.Evidence);

    public static TransactionDto ToDto(TransactionInput t) => new(t.Id, t.ServiceId, t.Amount, t.Status, t.Type, t.CreatedAt, t.PerformedAt);

    public static CustomerDetailBody ToDetail(CustomerAssessment c, IReadOnlyList<TransactionInput> transactions, MlShadowDto? mlShadow = null) => new(
        new AccountDto(c.Account.Id, c.Account.Email, c.Account.Balance, c.Account.WithdrawBalance, c.Account.TotalReplenished, c.Account.BonusBalance, c.Account.BonusState),
        c.Score, c.Level.ToApi(), c.State.ToApi(), ToDto(c.PrimaryReason), c.Reasons.Select(ToDto).ToList(),
        c.Services.Select(s => new ServiceDto(s.Id, s.Name, s.Status, s.PaymentTerm, s.AutoProlong, s.CreatedAt, s.ExpiresAt, s.Price,
            s.Renewable, s.Scored, s.Score, s.RawScore, s.Capped,
            s.Features.Select(f => new FeatureDto(f.Key, f.Title, f.Value, f.Weight, f.Contribution, f.MissingInputs, f.Evidence, f.Text)).ToList(),
            s.NextDue, s.RenewalsInHorizon, s.FirstDue, new MoneyDto(s.PlannedMinor, s.UnfundedMinor, RiskEngine.RoundMinor(s.AtRiskExact)),
            s.Flags, s.MissingInputs, s.LastTransaction is null ? null : ToDto(s.LastTransaction))).ToList(),
        new FundingDto(c.Funding.Known, c.Funding.FundsMinor, c.Funding.BalanceAvailableMinor, c.Funding.BonusAvailableMinor, c.Funding.BalanceUsedMinor,
            c.Funding.BonusUsedMinor, c.Funding.BonusEligible, c.Funding.BonusState, c.Funding.WithdrawExcludedMinor, c.Funding.FirstUnfundedDue),
        new MoneyDto(c.Money.PlannedRenewalsMinor, c.Money.UnfundedMinor, c.Money.AtRiskMinor), c.UnscoredPlannedMinor, c.Flags,
        new ServiceCountsDto(c.ServicesTotal, c.ServicesRenewable, c.ServicesScored),
        transactions.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(50).Select(ToDto).ToList(),
        MoneyFormula,
        new ChurnDetailDto(
            new FullChurnDto(c.FullChurn.Score, c.FullChurn.Level.ToApi(), c.FullChurn.State, c.FullChurn.WeakestServiceId, c.FullChurn.Text),
            new PaymentTrendDto(c.PaymentTrend.State, c.PaymentTrend.ChangePct, c.PaymentTrend.CurrentMinor, c.PaymentTrend.PreviousMinor,
                c.PaymentTrend.WindowDays, c.PaymentTrend.HistoryStart, c.PaymentTrend.Text)),
        mlShadow);
}
