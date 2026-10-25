using System.Text.Json;
using Aeza.Risk.Core;
using Xunit;
using static Aeza.Risk.UnitTests.EngineExamplesTests;

namespace Aeza.Risk.UnitTests;

public sealed class DomainSemanticsTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly ModelSettings Defaults = ModelSettings.Default(FeatureCatalog.All);
    private static readonly RiskEngine Engine = new();

    [Fact]
    public void Suspended_IsBilledAndScheduled()
    {
        var data = new SourceData([Account(1, balance: 0)], [Service(1, 1, "suspended", "week", 300, AsOf.AddDays(1), true)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.True(c.Services.Single().Renewable);
        Assert.True(c.Money.PlannedRenewalsMinor > 0); // 1 + ~4 недельных оплаты в горизонте
        Assert.Equal(5 * 300, c.Money.PlannedRenewalsMinor);
    }

    [Fact]
    public void BlockedAndDeleted_HaveNoFutureRenewals_EvenWithFutureExpiry()
    {
        var data = new SourceData([Account(1, balance: 0)],
            [Service(1, 1, "blocked", "hour", 300, AsOf.AddHours(2), false), Service(2, 1, "active", "month", 100, AsOf.AddDays(10), true)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Equal(100, c.Money.PlannedRenewalsMinor);
        Assert.False(c.Services.Single(s => s.Id == 1).Renewable);
        Assert.Equal(ScoreState.Complete, c.State); // blocked не делает оценку частичной
    }

    [Fact]
    public void WithdrawBalance_IsNeverSpentAutomatically()
    {
        var data = new SourceData([Account(1, balance: 0, withdraw: 1_000_000)], [Service(1, 1, "active", "month", 500, AsOf.AddDays(1), true)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Equal(500, c.Money.UnfundedMinor);
        Assert.True(c.Score > 0);
        Assert.Equal("funds_shortfall", c.PrimaryReason.Code);
    }

    [Theory]
    [InlineData("unlocked", 0)]
    [InlineData("locked", 500)]
    [InlineData(null, 500)]
    public void BonusCountsOnlyWhenUnlocked(string? bonusState, long expectedUnfunded)
    {
        var data = new SourceData([Account(1, balance: 0, bonus: 500, bonusState: bonusState)], [Service(1, 1, "active", "month", 500, AsOf.AddDays(1), true)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Equal(expectedUnfunded, c.Money.UnfundedMinor);
    }

    [Fact]
    public void ZeroPrice_DoesNotDivideByZero_AndIsAlwaysCovered()
    {
        var data = new SourceData([Account(1, balance: 0)], [Service(1, 1, "active", "hour", 0, AsOf.AddMinutes(30), true)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Equal(0m, c.Score);
        Assert.Equal(0, c.Money.PlannedRenewalsMinor);
        Assert.Equal("no_risk_signals", c.PrimaryReason.Code);
    }

    [Fact]
    public void MissingBalance_IsUnknownNotZero()
    {
        var data = new SourceData([Account(1, balance: null)], [Service(1, 1, "active", "month", 100, AsOf.AddDays(1), true)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Null(c.Score);
        Assert.Equal(ScoreState.InsufficientData, c.State);
        Assert.Contains(QualityFlags.MissingBalance, c.Flags);
        Assert.Equal(0, c.Money.AtRiskMinor);
        Assert.Equal(0, c.Money.UnfundedMinor);              // покрытие неизвестно — не «всё не покрыто»
        Assert.False(c.Funding.Known);
        var r = Engine.Evaluate(data, Defaults, AsOf);
        Assert.Equal(100, r.Summary.UnfundedUnknownPlannedMinor);
        Assert.Equal(0, r.Summary.UnfundedMinor);
    }

    [Fact]
    public void ZeroContribution_TextDoesNotClaimFundsAreCovered()
    {
        var data = new SourceData([Account(1, balance: 0)], [Service(1, 1, "active", "month", 100, AsOf.AddDays(20), true)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Equal(0m, c.Score);
        Assert.Equal(100, c.Money.UnfundedMinor);
        Assert.DoesNotContain("продления покрыты", c.PrimaryReason.Text);
    }

    [Fact]
    public void UnknownStatusService_MarksFundingIncomplete()
    {
        var data = new SourceData([Account(1, balance: 1000)],
            [Service(1, 1, "active", "month", 100, AsOf.AddDays(3), true), Service(2, 1, "frozen", "month", 900, AsOf.AddDays(1), true)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Equal(ScoreState.Partial, c.State);
        Assert.Contains(QualityFlags.FundingIncomplete, c.Flags);
        Assert.Contains(QualityFlags.UnknownStatus, c.Flags);
    }

    [Fact]
    public void ActiveExpiryPassed_ProjectsOnlyThePendingPayment()
    {
        var data = new SourceData([Account(1, balance: 100_000)], [Service(1, 1, "active", "hour", 10, AsOf.AddDays(-4), true)], []);
        var s = Engine.Evaluate(data, Defaults, AsOf).Customers.Single().Services.Single();
        Assert.Equal(1, s.RenewalsInHorizon);
        Assert.Contains(QualityFlags.ExpiryPassedStatusNotUpdated, s.Flags);
    }

    [Theory]
    [InlineData("quarter_year", 3)]
    [InlineData("half_year", 6)]
    [InlineData("year", 12)]
    public void LongTerms_AreAnchoredCalendarMonths(string term, int months)
    {
        var anchor = new DateTimeOffset(2027, 8, 31, 9, 0, 0, TimeSpan.Zero);
        var plan = RenewalSchedule.Plan(Service(1, 1, "active", term, 100, anchor, true), ServiceStatus.Active, anchor.AddDays(-1));
        Assert.Equal(anchor.AddMonths(months), RenewalSchedule.DueAt(plan.Anchor!.Value, plan.Term, 1));
        Assert.Equal(anchor.AddMonths(2 * months), RenewalSchedule.DueAt(plan.Anchor!.Value, plan.Term, 2));
    }

    [Fact]
    public void HorizonEnd_IsExclusive()
    {
        var plan = RenewalSchedule.Plan(Service(1, 1, "active", "week", 100, AsOf.AddDays(2), true), ServiceStatus.Active, AsOf);
        var dues = RenewalSchedule.Renewals(plan, AsOf.AddDays(9)).Select(r => r.Due).ToList(); // +2, +9(исключено)
        Assert.Equal([AsOf.AddDays(2)], dues);
    }

    [Fact]
    public void NonUtcOffsets_AreComparedAsInstants()
    {
        var plus3 = new DateTimeOffset(2026, 10, 19, 18, 0, 0, TimeSpan.FromHours(3)); // = AsOf в UTC
        var data = new SourceData([Account(1, balance: 0)], [Service(1, 1, "active", "month", 100, plus3.AddDays(1), false)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Equal(60m, c.Score); // (35 + 35) × (1 − 1/7)
    }

    [Fact]
    public void MoneyOverflow_IsDetectedNotWrapped()
    {
        var data = new SourceData([Account(1, balance: 0)],
            [Service(1, 1, "active", "hour", long.MaxValue / 2, AsOf.AddMinutes(30), true)], []);
        Assert.Throws<OverflowException>(() => Engine.Evaluate(data, Defaults, AsOf));
    }

    [Fact]
    public void ZeroWeightFeature_DoesNotBlockScoring_WhenItsInputIsMissing()
    {
        var settings = Defaults with { Weights = Defaults.Weights.ToDictionary(k => k.Key, k => k.Key == "funds_shortfall" ? 0m : k.Value) };
        var data = new SourceData([Account(1, balance: null)], [Service(1, 1, "prolong_wait", "month", 100, AsOf.AddDays(-1), true)], []);
        var c = Engine.Evaluate(data, settings, AsOf).Customers.Single();
        Assert.Equal(40m, c.Score);
    }

    [Fact]
    public void ProlongWait_IsNotRewrittenToDeleted_AndHasSinglePendingPayment()
    {
        var data = new SourceData([Account(1, balance: 10_000)], [Service(1, 1, "prolong_wait", "month", 700, AsOf.AddDays(-400), true)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        var s = c.Services.Single();
        Assert.Equal("prolong_wait", s.Status);
        Assert.Equal(AsOf, s.FirstDue);
        Assert.Equal(1, s.RenewalsInHorizon); // следующий месяц от момента оплаты за горизонтом? нет: +1 мес = 19.11 ≥ 18.11
        Assert.Contains(QualityFlags.ProlongWaitBeyondGrace, s.Flags);
        Assert.Equal(40m, c.Score); // ожидание оплаты; деньги есть, автопродление включено
    }

    [Fact]
    public void ActiveWithPastExpiry_IsDueNow_AndFlagged()
    {
        var data = new SourceData([Account(1, balance: 0)], [Service(1, 1, "active", "year", 100, AsOf.AddDays(-3), false)], []);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Contains(QualityFlags.ExpiryPassedStatusNotUpdated, c.Flags);
        Assert.Equal(70m, c.Score); // нет денег (35) + нет автопродления (35), оба срочные
    }

    [Fact]
    public void OnlyTimeMoves_ScoreGrowsAsExpiryApproaches()
    {
        var data = new SourceData([Account(1, balance: 100_000)], [Service(1, 1, "active", "month", 100, AsOf.AddDays(6), false)], []);
        var early = Engine.Evaluate(data, Defaults, AsOf).Customers.Single().Score!.Value;
        var later = Engine.Evaluate(data, Defaults, AsOf.AddDays(5)).Customers.Single().Score!.Value;
        Assert.Equal(5m, early);   // 35 × (1 − 6/7)
        Assert.Equal(30m, later);  // 35 × (1 − 1/7)
    }

    [Fact]
    public void RecordOrder_DoesNotChangeResults()
    {
        var accounts = new[] { Account(1, balance: 700), Account(2, balance: 50, bonus: 1000, bonusState: "unlocked") };
        var services = new[]
        {
            Service(10, 1, "active", "week", 300, AsOf.AddDays(1), false), Service(11, 1, "suspended", "month", 400, AsOf.AddDays(1), true),
            Service(12, 2, "prolong_wait", "hour", 20, AsOf.AddDays(-1), true), Service(13, 2, "active", "month", 100, AsOf.AddHours(5), false),
        };
        var tx = new[]
        {
            new TransactionInput(1, 10, 100, "not_performed", null, AsOf.AddDays(-1), "prolong"),
            new TransactionInput(2, 10, 100, "performed", AsOf.AddDays(-2), AsOf.AddDays(-2), "order"),
        };
        var a = Engine.Evaluate(new SourceData(accounts, services, tx), Defaults, AsOf);
        var b = Engine.Evaluate(new SourceData(accounts.Reverse().ToArray(), services.Reverse().ToArray(), tx.Reverse().ToArray()), Defaults, AsOf);
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
    }

    [Fact]
    public void UnpaidLastTransaction_IsSignalNotDecline()
    {
        var tx = new[] { new TransactionInput(1, 1, 100, "performed", AsOf.AddDays(-3), AsOf.AddDays(-3), "order"), new TransactionInput(2, 1, 100, "not_performed", null, AsOf.AddDays(-1), "prolong") };
        var data = new SourceData([Account(1, balance: 100_000)], [Service(1, 1, "active", "month", 100, AsOf.AddDays(20), true)], tx);
        var c = Engine.Evaluate(data, Defaults, AsOf).Customers.Single();
        Assert.Equal(10m, c.Score);
        Assert.DoesNotContain("отказ", c.PrimaryReason.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("не проведена", c.PrimaryReason.Text);
    }

    [Fact]
    public void MoneyIsNotSharedBetweenCustomers()
    {
        var data = new SourceData([Account(1, balance: 1_000_000), Account(2, balance: 0)],
            [Service(1, 2, "active", "month", 500, AsOf.AddDays(1), true)], []);
        var r = Engine.Evaluate(data, Defaults, AsOf);
        Assert.Equal(500, r.Customers.Single(c => c.Account.Id == 2).Money.UnfundedMinor);
        Assert.Equal(500, r.Summary.UnfundedMinor);
    }

    [Fact]
    public void Summary_LevelsAreMutuallyExclusive_AndMoneyMatchesCustomers()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "organizer", "data.json");
        var data = OrganizerData.Load(path);
        var r = Engine.Evaluate(data, Defaults, AsOf);
        Assert.Equal(r.Summary.CustomersTotal, r.Summary.ByLevel.Values.Sum());
        Assert.Equal(r.Customers.Sum(c => c.Money.AtRiskMinor), r.Summary.AtRiskMinor);
        Assert.Equal(r.Summary.ByLevel.Sum(kv => r.Summary.MoneyByLevel[kv.Key].AtRiskMinor), r.Summary.AtRiskMinor);
        Assert.Equal(r.Forecast.Sum(d => d.PlannedMinor), r.Summary.PlannedRenewalsMinor);
    }
}
