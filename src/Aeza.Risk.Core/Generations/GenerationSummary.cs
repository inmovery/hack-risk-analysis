namespace Aeza.Risk.Core.Generations;

public sealed record LevelMoneyDto(int Customers, long PlannedRenewalsMinor, long UnfundedMinor, long AtRiskMinor);

public sealed record SummaryMoneyDto(int HorizonDays, long PlannedRenewalsMinor, long UnfundedMinor, long AtRiskMinor, long UnscoredPlannedMinor, long UnfundedUnknownPlannedMinor, IReadOnlyDictionary<string, LevelMoneyDto> ByLevel,
    LossInterval? AtRiskInterval = null);

public sealed record SummaryCustomersDto(int Total, IReadOnlyDictionary<string, int> ByLevel, IReadOnlyDictionary<string, int> ByState, int Scored, decimal ScoreSum);

public sealed record SummaryServicesDto(int Total, int Renewable, int Scored, int Unscored, int Unschedulable, IReadOnlyDictionary<string, int> ByStatus);

public sealed record TopCustomerDto(long Id, string Email, decimal? RiskScore, string RiskLevel, long AtRiskMinor, string PrimaryReasonText);

public sealed record ForecastPointDto(DateOnly Date, long PlannedMinor, long AtRiskMinor, int Renewals);

public sealed record DefinitionsDto(string AtRisk, string Planned, string Unfunded, string Score, string FullChurn, string PaymentTrend, string ExpiryOutcomes);

/// <summary>Индекс «уход целиком»: клиенты по уровню и состоянию</summary>
public sealed record FullChurnSummaryDto(IReadOnlyDictionary<string, int> ByLevel, IReadOnlyDictionary<string, int> ByState);

/// <summary>Тренд платежей: клиенты по состоянию</summary>
public sealed record PaymentTrendSummaryDto(IReadOnlyDictionary<string, int> ByState, int WindowDays);

/// <summary>Типы оттока помимо непродления</summary>
public sealed record ChurnSummaryDto(FullChurnSummaryDto FullChurn, PaymentTrendSummaryDto PaymentTrend);

/// <summary>Неизменяемая сводка поколения (хранится в generations.summary_json, читается и API, и /metrics)</summary>
public sealed record GenerationSummary(
    SummaryCustomersDto Customers, SummaryServicesDto Services, SummaryMoneyDto Money, IReadOnlyDictionary<string, int> QualityFlags,
    IReadOnlyList<TopCustomerDto> TopAtRisk, ChurnSummaryDto? Churn = null, MlShadowSummaryDto? MlShadow = null, ScoringInfo? Scoring = null);

public sealed record ObservedEventDto(long ServiceId, long OwnerId, string Kind, string? FromStatus, string? ToStatus, DateTimeOffset ObservedFrom, DateTimeOffset ObservedTo, long SourceRevision);

public sealed record HistoryDto(int ObservedSnapshots, int EventsTotal, IReadOnlyDictionary<string, int> DeletedByObservedDay, IReadOnlyList<ObservedEventDto> RecentEvents, string Note);


public static class Definitions
{
    public static readonly DefinitionsDto Default = new(
        AtRisk: "Выручка под риском: сумма плановых продлений в горизонте, взвешенная риск-индексом услуги (score/100). Индекс — не вероятность; это сценарная оценка возможных потерь для приоритизации.",
        Planned: "Плановые продления: все ожидаемые оплаты active/suspended/prolong_wait в горизонте при сохранении услуг и текущих цен.",
        Unfunded: "Не покрыто балансом: часть плановых продлений, на которую не хватает основного баланса и разрешённых бонусов (общий баланс клиента распределяется один раз; withdrawBalance не тратится автоматически).",
        Score: "Score 0–100 — индекс приоритета внимания: максимум по услугам клиента от Σ вес × сила признака (ограничено 100). Не вероятность оттока.",
        FullChurn: "Уход клиента целиком: минимальный риск-индекс среди действующих услуг клиента — клиент уходит целиком, только если уходят все его услуги. Индекс, не вероятность.",
        PaymentTrend: "Снижение платежей: выполненные платежи за последние W дней против предыдущих W дней; снижение — не меньше порога. Если клиент моложе 2W — «мало истории» (не 0).",
        ExpiryOutcomes: "Исходы сроков (факт из журнала): услуга не продлена в течение N дней после срока или удалена. Фиксируется, только если источник обновлялся после срока + N дней.");

    /// <summary>Основной балл — ML: балл, деньги и уход целиком считаются от вероятности модели</summary>
    public static readonly DefinitionsDto Ml = Default with
    {
        AtRisk = "Выручка под риском: сумма плановых продлений в горизонте, взвешенная вероятностью непродления услуги по ML-модели. Услуги вне окна модели не оцениваются и в сумму не входят.",
        Score = "Score 0–100 — вероятность непродления по ML-модели × 100, максимум по услугам клиента. Модель оценивает услуги active и suspended за 14 и 7 дней до срока (недельные — за 3 дня); остальные — «нет оценки».",
        FullChurn = "Уход клиента целиком: минимальный балл ML среди действующих услуг клиента — клиент уходит целиком, только если уходят все его услуги.",
    };

    public static DefinitionsDto For(string? scoring) => scoring == ScoringModes.Ml ? Ml : Default;
}
