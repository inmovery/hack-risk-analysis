using Aeza.Risk.Core;
using Aeza.Risk.Core.Generations;
using Aeza.Risk.Core.Source;

using Xunit;

using static Aeza.Risk.UnitTests.EngineExamplesTests;

namespace Aeza.Risk.UnitTests;

/// <summary>ML в ядре: вектор признаков (схема, нейтральные значения без поведения, окна), метки тем же кодом, что expiry_outcomes, теневой расчёт (область применения, сбой модели не трогает правила, сравнение top-K) и вход поколения ml_revision</summary>
public sealed class MlShadowTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 25);
    private static readonly RiskEngine Engine = new();

    private static float F(float[] v, string name) => v[MlFeatures.Index[name]];

    private static ActivityDay Day(int daysAgo, Action<ActivityDelta> set)
    {
        var d = new ActivityDelta { Events = 1 };
        set(d);
        return new ActivityDay(Today.AddDays(-daysAgo), d);
    }

    // ---------- Признаки ----------

    [Fact]
    public void Features_FixedSchema_NoBehavior_GivesNeutralValuesAndFlag()
    {
        var s = Service(1, 5, "active", "month", 1000, AsOf.AddDays(7), autoProlong: false);
        var v = MlFeatures.Compute(s, Account(5, balance: 500, bonus: 300, bonusState: "unlocked"), [s], null, AsOf);

        Assert.Equal(43, MlFeatures.Names.Count);
        Assert.Equal(MlFeatures.Names.Count, v.Length);
        Assert.Equal(MlFeatures.Names.Count, MlFeatures.Names.Distinct().Count());
        Assert.Equal(1, F(v, "term_month"));
        Assert.Equal(10f, F(v, "price_month_eur"));
        Assert.Equal(0, F(v, "auto_prolong"));
        Assert.Equal(7f, F(v, "days_to_expiry"), 3);
        Assert.Equal(0.8f, F(v, "funds_cover"), 3); // баланс 5 € + разблокированный бонус 3 € против цены 10 €
        Assert.Equal(1, F(v, "bonus_unlocked"));
        Assert.Equal(1, F(v, "live_services"));
        Assert.Equal(0, F(v, "has_behavior"));
        Assert.Equal(1, F(v, "cpu_ratio"));
        Assert.Equal(5, F(v, "csat_min_30"));
        Assert.Equal(10, F(v, "nps_min_90"));
        Assert.Equal(0, F(v, "logins_30"));
    }

    [Fact]
    public void Features_BehaviorWindows_CountOnlyDaysUpToDecision()
    {
        var s = Service(1, 5, "active", "month", 1000, AsOf.AddDays(14), autoProlong: true);
        var b = new AccountBehavior(Today.AddDays(-80), Today.AddDays(-10),
        [
            Day(30, d => { d.UsageSamples = 1; d.CpuSum = 60; }),
            Day(40, d => { d.UsageSamples = 1; d.CpuSum = 99; }), // раньше базового окна −36…−7
            Day(20, d => { d.UsageSamples = 1; d.CpuSum = 40; d.Logins = 2; d.Downgrades = 1; }),
            Day(10, d => { d.Logins = 1; d.TicketsOpened = 1; d.TicketsUnsolved = 1; d.CsatMin = 2; }),
            Day(3, d => { d.UsageSamples = 2; d.CpuSum = 20; d.BonusExpired = 1; d.PaymentsFailed = 1; }),
            Day(-1, d => { d.Logins = 9; d.Deletions = 1; }), // завтра: в окно не попадает
        ]);
        var v = MlFeatures.Compute(s, Account(5, balance: 5000), [s], b, AsOf);

        Assert.Equal(1, F(v, "has_behavior"));
        Assert.Equal(0, F(v, "logins_7"));
        Assert.Equal(3, F(v, "logins_30"));
        Assert.Equal(10, F(v, "days_since_login"));
        Assert.Equal(10f, F(v, "cpu_7"), 3);       // (20) / 2 замера
        Assert.Equal(50f, F(v, "cpu_base"), 3);    // дни −36…−7: 60 и 40
        Assert.Equal(0.2f, F(v, "cpu_ratio"), 3);
        Assert.Equal(1, F(v, "unsolved_30"));
        Assert.Equal(2, F(v, "csat_min_30"));
        Assert.Equal(1, F(v, "downgrades_60"));
        Assert.Equal(1, F(v, "bonus_expired_45"));
        Assert.Equal(1, F(v, "failed_payments_30"));
        Assert.Equal(0, F(v, "deletions_90"));
    }

    // ---------- Метки ----------

    [Fact]
    public void Ledger_RenewedBeforeGrace_NotRenewedAfterFreshSource_DeletedImmediately_BlockedExcluded()
    {
        var e = AsOf.AddDays(10);
        var ledger = new ExpiryLedger();
        var s1 = Service(1, 5, "active", "month", 1000, e, true);
        var s2 = Service(2, 5, "active", "month", 1000, e, true);
        var s3 = Service(3, 5, "active", "month", 1000, e, true);
        var s4 = Service(4, 5, "active", "month", 1000, e, true);
        foreach (var s in new[] { s1, s2, s3, s4 })
            Assert.Single(ledger.Apply(ExpiryOutcomeRules.OnServiceObserved(null, s, AsOf, 5), 5));

        // Продление: срок сдвинулся до E + 30
        ledger.Apply(ExpiryOutcomeRules.OnServiceObserved(s1, s1 with { ExpiresAt = e.AddDays(30) }, e.AddDays(-1), 5), 5);
        // Удаление до срока — «не продлено» сразу
        ledger.Apply(ExpiryOutcomeRules.OnServiceObserved(s3, s3 with { Status = "deleted" }, e.AddDays(-2), 5), 5);
        // Блокировка — не решение клиента, исключается
        ledger.Apply(ExpiryOutcomeRules.OnServiceObserved(s4, s4 with { Status = "blocked" }, e.AddDays(1), 5), 5);
        // До E + N исход s2 неизвестен (цензура), после — «не продлено» только при полном источнике
        Assert.Equal(0, ledger.ResolveDue(e.AddDays(4), e.AddDays(4), 5, _ => s2));
        Assert.Equal(0, ledger.ResolveDue(e.AddDays(6), e.AddDays(4), 5, _ => s2));
        Assert.Equal(1, ledger.ResolveDue(e.AddDays(6), e.AddDays(6), 5, _ => s2));

        Assert.Equal(ExpiryOutcomes.Renewed, ledger.Find(1, e)!.Outcome);
        Assert.Equal(ExpiryOutcomes.NotRenewed, ledger.Find(2, e)!.Outcome);
        Assert.Equal(ExpiryOutcomes.NotRenewed, ledger.Find(3, e)!.Outcome);
        Assert.Equal(ExpiryOutcomes.Excluded, ledger.Find(4, e)!.Outcome);
    }

    [Fact]
    public void Ledger_LateRenewal_StaysNotRenewed()
    {
        var e = AsOf;
        var ledger = new ExpiryLedger();
        var s = Service(1, 5, "prolong_wait", "month", 1000, e, false);
        ledger.Apply(ExpiryOutcomeRules.OnServiceObserved(null, s, e.AddDays(-3), 5), 5);
        ledger.Apply(ExpiryOutcomeRules.OnServiceObserved(s, s with { Status = "active", ExpiresAt = e.AddDays(30) }, e.AddDays(9), 5), 5);
        Assert.Equal(ExpiryOutcomes.NotRenewed, ledger.Find(1, e)!.Outcome);
        Assert.Equal(ExpiryOutcomes.ResolutionRenewedLate, ledger.Find(1, e)!.Resolution);
    }

    // ---------- Теневой расчёт ----------

    private static (EngineResult Result, SourceData Data) Generation()
    {
        var data = new SourceData(
            [Account(1, balance: 0), Account(2, balance: 100_000), Account(3, balance: 1000)],
            [
                Service(10, 1, "active", "month", 1000, AsOf.AddDays(5), false),   // в окне, правила видят риск
                Service(11, 1, "active", "month", 1000, AsOf.AddDays(40), true),   // вне окна (срок далеко)
                Service(20, 2, "active", "month", 1000, AsOf.AddDays(12), true),   // в окне
                Service(30, 3, "active", "hour", 10, AsOf.AddHours(5), true),      // почасовая: модель не применяется
                Service(31, 3, "prolong_wait", "month", 1000, AsOf.AddDays(-1), false), // срок прошёл: не в окне
            ],
            []);
        return (Engine.Evaluate(data, ModelSettings.Default(FeatureCatalog.All), AsOf), data);
    }

    [Fact]
    public void Shadow_ScoresOnlyServicesInScope_KeepsRulesUntouched()
    {
        var (result, data) = Generation();
        var before = result.Customers.Select(c => (c.Account.Id, c.Score, c.Level)).ToList();
        float[]? seen = null;
        var run = MlShadow.Run(new MlModelRef(7, "v1"), result, data, new Dictionary<long, AccountBehavior>(), AsOf, (batch, rows) =>
        {
            seen ??= batch; // первый вызов — прогноз, следующие — объяснение
            return Enumerable.Range(0, rows).Select(i => i == 0 ? 0.9f : 0.2f).ToArray();
        });

        Assert.Equal("scored", run.Summary.State);
        Assert.Equal(2, run.Summary.ServicesInScope);
        Assert.Equal(2 * MlFeatures.Names.Count, seen!.Length);
        Assert.Equal("scored", run.Customers[1].State);
        Assert.Equal(10, run.Customers[1].ServiceId);
        Assert.Equal(0.9, run.Customers[1].Probability);
        Assert.Equal("scored", run.Customers[2].State);
        Assert.Equal("out_of_scope", run.Customers[3].State);
        Assert.Null(run.Customers[3].Probability);
        Assert.Equal(1, run.Summary.TopK);
        Assert.Equal(1, run.Summary.TopKOverlap); // и модель, и правила ставят услугу 10 первой
        Assert.Equal([7, 14], run.Predictions.OrderBy(p => p.ServiceId).Select(p => p.OffsetDays));
        Assert.Equal(before, result.Customers.Select(c => (c.Account.Id, c.Score, c.Level)).ToList());
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("inference_error")]
    [InlineData("not_probability")]
    public void Shadow_ModelFailure_GivesFailedStateWithoutPredictions(string failure)
    {
        var (result, data) = Generation();
        var run = MlShadow.Run(new MlModelRef(7, "v1"), result, data, new Dictionary<long, AccountBehavior>(), AsOf, (_, rows) => failure switch
        {
            "timeout" => throw new TimeoutException(),
            "inference_error" => throw new InvalidOperationException(),
            _ => Enumerable.Repeat(float.NaN, rows).ToArray(),
        });
        Assert.Equal("failed", run.Summary.State);
        Assert.Equal(failure == "timeout" ? "timeout" : "inference_error", run.Summary.ErrorCode);
        Assert.Empty(run.Predictions);
        Assert.All(run.Customers.Values, c => Assert.Equal("failed", c.State));
    }

    [Fact]
    public void Shadow_DecisionOffset_IsNearestPassedDecisionPoint()
    {
        Assert.Equal(14, MlShadow.DecisionOffset(Service(1, 1, "active", "month", 1, AsOf.AddDays(13.5), true), AsOf));
        Assert.Equal(7, MlShadow.DecisionOffset(Service(1, 1, "active", "year", 1, AsOf.AddDays(6), true), AsOf));
        Assert.Equal(3, MlShadow.DecisionOffset(Service(1, 1, "active", "week", 1, AsOf.AddDays(2), true), AsOf));
        Assert.False(MlDecision.InScope(Service(1, 1, "active", "week", 1, AsOf.AddDays(5), true), AsOf));
        Assert.False(MlDecision.InScope(Service(1, 1, "suspended", "month", 1, AsOf.AddDays(15), true), AsOf));
        Assert.True(MlDecision.InScope(Service(1, 1, "suspended", "month", 1, AsOf.AddDays(14), true), AsOf));
    }

    [Fact]
    public void Generation_MlRevisionIsAnInput_OlderRevisionNeverOverwritesNewer()
    {
        var active = new Core.Entities.GenerationEntity { SourceRevision = 1, SourceSeq = 5, ModelRevisionId = 2, ActivitySeq = 9, MlRevision = 3, AsOf = AsOf };
        Assert.False(GenerationPublishing.ShouldPublish(active, new Core.Entities.GenerationEntity
            { SourceRevision = 1, SourceSeq = 5, ModelRevisionId = 2, ActivitySeq = 9, MlRevision = 2, AsOf = AsOf.AddMinutes(5) }));
        Assert.True(GenerationPublishing.ShouldPublish(active, new Core.Entities.GenerationEntity
            { SourceRevision = 1, SourceSeq = 5, ModelRevisionId = 2, ActivitySeq = 9, MlRevision = 4, AsOf = AsOf.AddMinutes(-5) }));
    }

    [Fact]
    public void RankingMetrics_PerfectAndRandomRankings()
    {
        var labels = new[] { 1, 0, 1, 0, 0, 0, 0, 0, 0, 0 };
        var perfect = RankingMetrics.Evaluate(labels, labels.Select(l => (double)l).ToList(), false);
        Assert.Equal(1, perfect.PrAuc, 6);
        Assert.Equal(1, perfect.RocAuc, 6);
        var constant = RankingMetrics.Evaluate(labels, labels.Select(_ => 0.0).ToList(), false);
        Assert.Equal(0.5, constant.RocAuc, 6);
    }
}
