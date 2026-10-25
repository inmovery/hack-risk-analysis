using Aeza.Risk.Core;
using Aeza.Risk.Core.Settings;
using Aeza.Risk.SharedKernel.Json;

using Xunit;

namespace Aeza.Risk.UnitTests;

/// <summary>Типы оттока (примеры 7–8)</summary>
public sealed class ChurnIndicatorsTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly ModelSettings Defaults = ModelSettings.Default(FeatureCatalog.All);
    private static readonly RiskEngine Engine = new();

    private static ServiceInput Svc(long id, long owner, string status, string term, long price, DateTimeOffset expires, bool auto, DateTimeOffset? created = null) =>
        new(id, owner, $"service-{id:0000}", term, auto, created ?? expires.AddDays(-30), expires, status, price);

    private static TransactionInput Paid(long id, long service, long amount, DateTimeOffset at) =>
        new(id, service, amount, "performed", at, at, "prolong");

    [Fact]
    public void Example7_FullChurn_IsMinimumOfActiveServices()
    {
        var data = new SourceData(
            [
                EngineExamplesTests.Account(1, balance: 0),
                EngineExamplesTests.Account(2, balance: 0, bonus: 1000, bonusState: "unlocked"),
                EngineExamplesTests.Account(3, balance: 500),
            ],
            [
                Svc(1, 1, "prolong_wait", "hour", 50, AsOf.AddDays(-2), false),
                Svc(2, 1, "active", "month", 1000, AsOf.AddDays(200), true),
                Svc(3, 2, "prolong_wait", "hour", 50, AsOf.AddDays(-10), false),
                Svc(4, 3, "deleted", "month", 1000, AsOf.AddDays(-40), false),
                Svc(5, 3, "deleted", "month", 1000, AsOf.AddDays(-70), false),
            ],
            []);

        var r = Engine.Evaluate(data, Defaults, AsOf).Customers.ToDictionary(c => c.Account.Id);

        Assert.Equal(100m, r[1].Score);
        Assert.Equal(0m, r[1].FullChurn.Score);
        Assert.Equal(RiskLevel.Low, r[1].FullChurn.Level);
        Assert.Equal(FullChurnStates.Scored, r[1].FullChurn.State);
        Assert.Equal(2, r[1].FullChurn.WeakestServiceId);

        Assert.Equal(75m, r[2].FullChurn.Score);
        Assert.Equal(RiskLevel.High, r[2].FullChurn.Level);

        Assert.Null(r[3].FullChurn.Score);
        Assert.Equal(FullChurnStates.Churned, r[3].FullChurn.State);
        Assert.Equal(ScoreState.NoRenewableServices, r[3].State);
    }

    [Fact]
    public void FullChurn_OnlyBlockedLeft_IsBlockedNotChurned()
    {
        var data = new SourceData([EngineExamplesTests.Account(1, balance: 0)],
            [Svc(1, 1, "blocked", "month", 100, AsOf.AddDays(10), false), Svc(2, 1, "deleted", "month", 100, AsOf.AddDays(-10), false)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Equal(FullChurnStates.Blocked, c.FullChurn.State);
        Assert.Null(c.FullChurn.Score);
    }

    [Fact]
    public void FullChurn_WithUnscoredService_IsPartialUpperBound()
    {
        var data = new SourceData([EngineExamplesTests.Account(1, balance: 5000)],
            [Svc(1, 1, "active", "month", 1000, AsOf.AddDays(1), false), Svc(2, 1, "active", "decade", 500, AsOf.AddDays(3), true)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Equal(FullChurnStates.Partial, c.FullChurn.State);
        Assert.Equal(30m, c.FullChurn.Score); // только оценённая услуга: верхняя граница
    }

    [Fact]
    public void Example8_PaymentDecline_ComparesTwoWindows()
    {
        var created = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var services = new[] { Svc(1, 1, "active", "month", 1000, AsOf.AddDays(10), true, created) };
        var tx = new[]
        {
            Paid(1, 1, 1000, new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero)),
            Paid(2, 1, 500, new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero)),
            new TransactionInput(3, 1, 9999, "not_performed", null, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), "prolong"),
        };

        var t = ChurnIndicators.PaymentTrend(services, tx, AsOf, ChurnSettings.Default);

        Assert.Equal(PaymentTrendStates.Decline, t.State);
        Assert.Equal(-50m, t.ChangePct);
        Assert.Equal(500, t.CurrentMinor);
        Assert.Equal(1000, t.PreviousMinor);
    }

    [Fact]
    public void PaymentTrend_ShortHistory_IsInsufficientNotZero()
    {
        var services = new[] { Svc(1, 1, "active", "month", 1000, AsOf.AddDays(10), true, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)) };
        var t = ChurnIndicators.PaymentTrend(services, [Paid(1, 1, 1000, new DateTimeOffset(2026, 10, 1, 0, 5, 0, TimeSpan.Zero))], AsOf, ChurnSettings.Default);
        Assert.Equal(PaymentTrendStates.InsufficientHistory, t.State);
        Assert.Null(t.ChangePct);
    }

    [Theory]
    [InlineData(1000, 1000, "stable", 0)]
    [InlineData(1000, 1300, "growth", 30)]
    [InlineData(1000, 710, "stable", -29)]
    [InlineData(1000, 700, "decline", -30)]
    public void PaymentTrend_ThresholdIsInclusive(long previous, long current, string state, decimal pct)
    {
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var services = new[] { Svc(1, 1, "active", "month", 1000, AsOf.AddDays(10), true, created) };
        var tx = new[] { Paid(1, 1, previous, AsOf.AddDays(-45)), Paid(2, 1, current, AsOf.AddDays(-5)) };
        var t = ChurnIndicators.PaymentTrend(services, tx, AsOf, ChurnSettings.Default);
        Assert.Equal(state, t.State);
        Assert.Equal(pct, t.ChangePct);
    }

    [Fact]
    public void PaymentTrend_NoPaymentsInBothWindows_IsNoPayments()
    {
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var services = new[] { Svc(1, 1, "active", "year", 1000, AsOf.AddDays(100), true, created) };
        var t = ChurnIndicators.PaymentTrend(services, [Paid(1, 1, 1000, created)], AsOf, ChurnSettings.Default);
        Assert.Equal(PaymentTrendStates.NoPayments, t.State);
    }

    [Fact]
    public void SchemaV1Revision_ReadsWithDefaultChurnDefinitions()
    {
        const string v1 = """{"schemaVersion":1,"horizonDays":30,"urgencyWindowDays":7,"thresholds":{"medium":30,"high":60},"clock":{"mode":"live","tickMinutes":5},"weights":{"waiting_payment":40}}""";
        var model = AppJson.Deserialize<ModelSettingsDto>(v1).ToModel();
        Assert.Equal(ModelSettings.CurrentSchemaVersion, model.SchemaVersion);
        Assert.Equal(ChurnSettings.Default, model.Churn);
        Assert.Empty(ModelSettingsValidator.Validate(model, FeatureCatalog.All.ToList()));
    }

    [Theory]
    [InlineData(0, 30, 30, "churn.nonRenewalDays")]
    [InlineData(5, 6, 30, "churn.paymentWindowDays")]
    [InlineData(5, 30, 0, "churn.paymentDeclinePct")]
    [InlineData(5, 30, 30.25, "churn.paymentDeclinePct")]
    public void ChurnSettings_AreValidated(int n, int window, decimal pct, string path)
    {
        var s = Defaults with { Churn = new ChurnSettings(n, window, pct) };
        Assert.Contains(path, ModelSettingsValidator.Validate(s, FeatureCatalog.All.ToList()).Keys);
    }
}
