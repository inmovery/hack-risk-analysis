using Aeza.Risk.Core;

using Xunit;

using static Aeza.Risk.UnitTests.EngineExamplesTests;

namespace Aeza.Risk.UnitTests;

/// <summary>Основной балл только по ML: балл, уровень, причины и деньги считаются от вероятности модели; вне окна модели — «нет оценки»</summary>
public sealed class MlScoringTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly ModelSettings Defaults = ModelSettings.Default(FeatureCatalog.All);
    private static readonly RiskEngine Engine = new();
    private static readonly MlModelRef Model = new(7, "fasttree-test");

    // Клиент 1: S1 €10 через 2 дня (в окне модели), S2 €20 через 20 дней (вне окна). Клиент 2: только S3 через 20 дней.
    private static readonly SourceData Data = new(
        [Account(1, balance: 1500), Account(2, balance: 0)],
        [
            Service(1, 1, "active", "month", 1000, AsOf.AddDays(2), true),
            Service(2, 1, "active", "month", 2000, AsOf.AddDays(20), false),
            Service(3, 2, "active", "month", 500, AsOf.AddDays(20), true),
        ],
        []);

    private static EngineResult Ml(IReadOnlyDictionary<long, MlServiceScore> p) => Engine.Evaluate(Data, Defaults, AsOf, ml: new MlPrimary(Model, p));

    private static readonly MlDriver Funds = new("funds", "Средства на продление", 0.21, "Средства покрывают 30 % цены продления, баланс €3,00");
    private static readonly MlDriver AutoProlong = new("auto_prolong", "Автопродление", -0.02, "Автопродление включено");

    [Fact]
    public void ScoreIsProbability_MoneyIsWeightedByIt()
    {
        var r = Ml(new Dictionary<long, MlServiceScore> { [1] = new(0.63, [Funds, AutoProlong]) });
        var c = r.Customers.Single(x => x.Account.Id == 1);

        Assert.Equal(63m, c.Score);
        Assert.Equal(RiskLevel.High, c.Level); // те же пороги: 60 и выше
        Assert.Equal(ScoreState.Partial, c.State); // S2 вне окна модели — не 0, а без оценки
        Assert.Equal(63m, c.Services.Single(s => s.Id == 1).Score);
        Assert.Null(c.Services.Single(s => s.Id == 2).Score);
        Assert.Equal([MlPrimary.OutOfScopeInput], c.Services.Single(s => s.Id == 2).MissingInputs);

        Assert.Equal(3000, c.Money.PlannedRenewalsMinor);
        Assert.Equal(1500, c.Money.UnfundedMinor); // распределение баланса от режима не зависит
        Assert.Equal(630, c.Money.AtRiskMinor); // €10 × 0,63
        Assert.Equal(2000, c.UnscoredPlannedMinor);
        Assert.Equal(630, r.Forecast.Sum(d => d.AtRiskMinor));

        // Причина — фактор модели со значениями клиента; снижающий риск фактор в причины не попадает, вклады правил тоже
        Assert.Equal("ml.funds", c.PrimaryReason.Code);
        Assert.Equal(1, c.PrimaryReason.ServiceId);
        Assert.Equal(21m, c.PrimaryReason.Contribution);
        Assert.Equal("Средства на продление", c.PrimaryReason.Title);
        Assert.Contains("30 %", c.PrimaryReason.Text);
        Assert.Single(c.Reasons);
    }

    [Fact]
    public void NoServiceInModelWindow_IsUnknownNotZero()
    {
        var c = Ml(new Dictionary<long, MlServiceScore> { [1] = new(0.1, []) }).Customers.Single(x => x.Account.Id == 2);

        Assert.Null(c.Score);
        Assert.Equal(RiskLevel.Unknown, c.Level);
        Assert.Equal(ScoreState.InsufficientData, c.State);
        Assert.Equal("ml_out_of_scope", c.PrimaryReason.Code);
        Assert.Equal(0, c.Money.AtRiskMinor);
        Assert.Equal(500, c.UnscoredPlannedMinor);
    }

    [Fact]
    public void NoRiskRaisingFactor_ReasonStatesProbability()
    {
        var c = Ml(new Dictionary<long, MlServiceScore> { [1] = new(0.08, [AutoProlong]) }).Customers.Single(x => x.Account.Id == 1);

        Assert.Equal(8m, c.Score);
        Assert.Equal(MlPrimary.FeatureKey, c.PrimaryReason.Code);
        Assert.Contains("8 %", c.PrimaryReason.Text);
        Assert.Empty(c.Reasons);
    }

    [Fact]
    public void Explain_FactorDeltaIsProbabilityDropWhenSetToTypical()
    {
        // Модель-заглушка зависит только от покрытия продления средствами: p = 0,9 − 0,5 × min(cover, 1)
        var n = MlFeatures.Names.Count;
        var cover = MlFeatures.Index["funds_cover"];
        var batch = new float[3 * n];
        batch[cover] = 0.2f;
        batch[n + cover] = 1f;
        batch[2 * n + cover] = 1f;
        float[] Predict(float[] x, int rows) => Enumerable.Range(0, rows).Select(r => 0.9f - 0.5f * Math.Min(x[r * n + cover], 1f)).ToArray();

        var drivers = MlExplain.Explain(batch, 3, Predict(batch, 3), Predict);

        var d = Assert.Single(drivers[0]);
        Assert.Equal("funds", d.Key);
        Assert.Equal(0.4, d.Delta, 4); // 0,8 у клиента против 0,4 у типичного (покрытие 100 %)
        Assert.StartsWith("Средства покрывают 20 % цены продления", d.Text);
        Assert.Empty(drivers[1]); // типичный клиент — объяснять нечего
    }

    [Fact]
    public void ExpectedLoss_IntervalByHand()
    {
        // €10 с вероятностью 0,5 и €20 с 0,1: E = 5 + 2 = €7; σ = √(1000²·0,25 + 2000²·0,09) = √610 000 ≈ 781 коп.
        var i = ExpectedLoss.Of([(1000, 0.5), (2000, 0.1)]);
        Assert.Equal(700, i.ExpectedMinor);
        Assert.Equal(0, i.LowMinor); // 700 − 1,645·781 < 0 → 0
        Assert.Equal(1985, i.HighMinor); // 700 + 1,645·781 ≈ 1984,7
        Assert.Equal(0.9, i.Level);
        Assert.Equal(2, i.Items);
    }

    [Fact]
    public void MlMode_SummaryHasInterval_RulesModeDoesNot()
    {
        var ml = Ml(new Dictionary<long, MlServiceScore> { [1] = new(0.63, [Funds]) });
        var i = ml.Summary.AtRiskInterval!;
        Assert.Equal(630, i.ExpectedMinor);
        Assert.Equal(1, i.Items); // услуги без оценки в интервал не входят
        Assert.True(i.LowMinor <= 630 && i.HighMinor >= 630 && i.HighMinor <= 1000);
        Assert.Null(Engine.Evaluate(Data, Defaults, AsOf).Summary.AtRiskInterval);
    }

    [Fact]
    public void TreeShapDrivers_AreProbabilityPoints_SumToPredictionMinusMean()
    {
        // Одно дерево по покрытию продления средствами: мало средств → логит 2, иначе −2; в обучении таких 10 из 100
        var n = MlFeatures.Names.Count;
        var cover = MlFeatures.Index["funds_cover"];
        var tree = new TreeModel(1, [cover], [0.5], [~0], [~1], [2, -2], [100], [10, 90]);
        var model = new TreeEnsemble(MlFeatures.Names, 0, 1, 0, [tree]);
        var x = new float[n];
        x[cover] = 0.2f;
        var p = (float)model.Probability(x);

        var driver = Assert.Single(MlExplain.FromShap(model, x, 1, [p])[0]);

        var mean = TreeEnsemble.Sigmoid(-1.6); // 2·0,1 − 2·0,9
        Assert.Equal("funds", driver.Key);
        Assert.Equal(Math.Round(p - mean, 4), driver.Delta, 4);
        Assert.StartsWith("Средства покрывают 20 % цены продления", driver.Text);
    }

    [Fact]
    public void RulesModeIsUnchanged()
    {
        var rules = Engine.Evaluate(Data, Defaults, AsOf);
        var c = rules.Customers.Single(x => x.Account.Id == 1);

        Assert.Equal(0m, c.Score); // S1 покрыт балансом и с автопродлением, S2 дальше окна срочности
        Assert.Equal(ScoreState.Complete, c.State);
        Assert.NotEqual(MlPrimary.FeatureKey, c.PrimaryReason.Code);
    }
}
