using Aeza.Risk.Core;
using Xunit;

namespace Aeza.Risk.UnitTests;

/// <summary>Ручные примеры из Ожидаемые значения посчитаны вручную в документе, а не этим кодом</summary>
public sealed class EngineExamplesTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly ModelSettings Defaults = ModelSettings.Default(FeatureCatalog.All);
    private static readonly RiskEngine Engine = new();

    internal static AccountInput Account(long id, long? balance, long? bonus = 0, string? bonusState = "locked", long? withdraw = 0, long? total = 0) =>
        new(id, $"user-{id:0000}@example.test", balance, withdraw, total, bonus, bonusState);

    internal static ServiceInput Service(long id, long owner, string status, string term, long? price, DateTimeOffset? expires, bool? autoProlong) =>
        new(id, owner, $"service-{id:0000}", term, autoProlong, expires?.AddDays(-30), expires, status, price);

    private static CustomerAssessment Single(SourceData data, DateTimeOffset? asOf = null, ModelSettings? settings = null) =>
        Engine.Evaluate(data, settings ?? Defaults, asOf ?? AsOf).Customers.Single();

    [Fact]
    public void Example1_SharedBalanceIsSpentOnce()
    {
        var data = new SourceData(
            [Account(1, balance: 1500, bonus: 500, bonusState: "locked", withdraw: 10_000)],
            [Service(1, 1, "active", "month", 1000, AsOf.AddDays(2), true), Service(2, 1, "active", "month", 2000, AsOf.AddDays(5), false)],
            []);

        var c = Single(data);

        Assert.Equal(20m, c.Score);
        Assert.Equal(RiskLevel.Low, c.Level);
        Assert.Equal(0m, c.Services.Single(s => s.Id == 1).Score);
        Assert.Equal(20m, c.Services.Single(s => s.Id == 2).Score);
        Assert.Equal(3000, c.Money.PlannedRenewalsMinor);
        Assert.Equal(1500, c.Money.UnfundedMinor); // наивный расчёт «баланс на каждую услугу» дал бы 500
        Assert.Equal(400, c.Money.AtRiskMinor);
        Assert.Equal(1500, c.Funding.FundsMinor); // бонус заблокирован, withdraw не тратится
        Assert.Equal(10_000, c.Funding.WithdrawExcludedMinor);
    }

    [Fact]
    public void Example2_HourlyProlongWaitWithoutMoney_IsCappedHigh()
    {
        var data = new SourceData(
            [Account(1, balance: 0, bonus: 1000, bonusState: "unlocked")],
            [Service(3, 1, "prolong_wait", "hour", 50, AsOf.AddDays(-2), false)],
            []);

        var c = Single(data);
        var s = c.Services.Single();

        Assert.Equal(100m, c.Score);
        Assert.True(s.Capped);
        Assert.Equal(105.833333m, s.RawScore);
        Assert.Equal(RiskLevel.High, c.Level);
        Assert.Equal(720, s.RenewalsInHorizon);
        Assert.Equal(36_000, c.Money.PlannedRenewalsMinor);
        Assert.Equal(35_000, c.Money.UnfundedMinor);
        Assert.Equal(36_000, c.Money.AtRiskMinor);
        Assert.DoesNotContain(QualityFlags.ProlongWaitBeyondGrace, c.Flags); // 2 дня < 5 дней ожидания
        Assert.Equal("waiting_payment", c.PrimaryReason.Code);
        Assert.Equal(1000, c.Funding.BonusUsedMinor);
    }

    [Fact]
    public void Example2b_ProlongWaitBeyondGrace_CountsOnlyThePendingPayment()
    {
        var data = new SourceData(
            [Account(1, balance: 0, bonus: 1000, bonusState: "unlocked")],
            [Service(3, 1, "prolong_wait", "hour", 50, AsOf.AddDays(-10), false)],
            []);

        var c = Single(data);
        var s = c.Services.Single();

        Assert.Contains(QualityFlags.ProlongWaitBeyondGrace, c.Flags);
        Assert.Equal(1, s.RenewalsInHorizon);           // будущие часы не проецируются: по регламенту услуга уже должна быть удалена
        Assert.Equal(50, c.Money.PlannedRenewalsMinor);
        Assert.Equal(0, c.Money.UnfundedMinor);         // бонус покрывает ожидающую оплату
        Assert.Equal(75m, c.Score);                     // 40 ожидание + 35 автопродление выкл.; нехватки нет
        Assert.Equal(RiskLevel.High, c.Level);
        Assert.Equal(38, c.Money.AtRiskMinor);          // 50 × 0,75 = 37,5 → 38 (банковское округление к чётному)
    }

    [Fact]
    public void Example3_SuspendedFarExpiry_IsLowWithoutMoney()
    {
        var data = new SourceData([Account(1, balance: 100_000)], [Service(4, 1, "suspended", "year", 5000, AsOf.AddDays(200), true)], []);

        var c = Single(data);

        Assert.Equal(20m, c.Score);
        Assert.Equal(RiskLevel.Low, c.Level);
        Assert.Equal(0, c.Money.PlannedRenewalsMinor);
        Assert.Equal(0, c.Money.AtRiskMinor);
        Assert.Equal("suspended_billing", c.PrimaryReason.Code);
    }

    [Fact]
    public void Example4_OnlyBlockedOrDeleted_HasNoScore()
    {
        var data = new SourceData([Account(1, balance: 100)],
            [Service(1, 1, "blocked", "month", 100, AsOf.AddDays(3), false), Service(2, 1, "deleted", "week", 100, AsOf.AddDays(-30), false)], []);

        var c = Single(data);

        Assert.Null(c.Score);
        Assert.Equal(RiskLevel.Unknown, c.Level);
        Assert.Equal(ScoreState.NoRenewableServices, c.State);
        Assert.Equal("no_renewable_services", c.PrimaryReason.Code);
        Assert.Equal(0, c.Money.PlannedRenewalsMinor);
    }

    [Fact]
    public void Example5_CalendarMonthsAreAnchored()
    {
        var anchor = new DateTimeOffset(2027, 1, 31, 10, 0, 0, TimeSpan.Zero);
        var asOf = new DateTimeOffset(2027, 1, 20, 0, 0, 0, TimeSpan.Zero);
        var plan = RenewalSchedule.Plan(Service(1, 1, "active", "month", 100, anchor, true), ServiceStatus.Active, asOf);

        var dues = RenewalSchedule.Renewals(plan, asOf.AddDays(90)).Select(r => r.Due).ToList();

        Assert.Equal(
            [anchor, new DateTimeOffset(2027, 2, 28, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 3, 31, 10, 0, 0, TimeSpan.Zero)],
            dues);
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 10, 0, 0, TimeSpan.Zero),
            RenewalSchedule.DueAt(new DateTimeOffset(2028, 1, 31, 10, 0, 0, TimeSpan.Zero), PaymentTerm.Month, 1));
    }

    [Fact]
    public void Example6_UnknownTermAndBonusState_GivePartialScore()
    {
        var data = new SourceData(
            [Account(1, balance: 5000, bonus: 700, bonusState: "frozen")],
            [Service(5, 1, "active", "month", 1000, AsOf.AddDays(1), false), Service(6, 1, "active", "decade", 500, AsOf.AddDays(3), true)],
            []);

        var c = Single(data);

        Assert.Equal(30m, c.Score);
        Assert.Equal(RiskLevel.Medium, c.Level); // ровно на пороге → средний
        Assert.Equal(ScoreState.Partial, c.State);
        Assert.False(c.Services.Single(s => s.Id == 6).Scored);
        Assert.Contains(QualityFlags.BonusStateUnknown, c.Flags);
        Assert.Contains(QualityFlags.FundingIncomplete, c.Flags);
        Assert.Equal(1000, c.Money.PlannedRenewalsMinor);
        Assert.Equal(0, c.Money.UnfundedMinor);
        Assert.Equal(300, c.Money.AtRiskMinor);
        Assert.Equal(0, c.Funding.BonusAvailableMinor);
    }
}
