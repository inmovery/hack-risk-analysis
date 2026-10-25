using Aeza.Risk.Core;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Generations;
using Aeza.Risk.Core.Source;

using Xunit;

using static Aeza.Risk.UnitTests.EngineExamplesTests;

namespace Aeza.Risk.UnitTests;

/// <summary>Проекция дневной активности и признаки поведения</summary>
public sealed class BehaviorFeaturesTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 25);
    private static readonly RiskEngine Engine = new();

    private static DomainEventInput Ev(string type, string data, long? account = 5, DateTimeOffset? at = null) =>
        new($"e-{Guid.NewGuid():N}", type, at ?? AsOf, account, 1, null, data, true, null);

    private static ActivityDay Day(int daysAgo, Action<ActivityDelta> set)
    {
        var d = new ActivityDelta { Events = 1 };
        set(d);
        return new ActivityDay(Today.AddDays(-daysAgo), d);
    }

    private static AccountBehavior Behavior(DateOnly knownSince, DateOnly? lastLogin, params ActivityDay[] days) => new(knownSince, lastLogin, days);

    private static ModelSettings Weights(params (string Key, decimal Weight)[] extra)
    {
        var s = ModelSettings.Default(FeatureCatalog.All);
        var w = s.Weights.ToDictionary(k => k.Key, k => k.Value);
        foreach (var (k, v) in extra) w[k] = v;
        return s with { Weights = w };
    }

    private static FeatureValue Eval(IRiskFeature f, AccountBehavior? b)
    {
        var data = new SourceData([Account(5, balance: 10_000)], [Service(1, 5, "active", "month", 500, AsOf.AddDays(20), true)], []);
        var c = Engine.Evaluate(data, ModelSettings.Default(FeatureCatalog.All), AsOf, b is null ? null : new Dictionary<long, AccountBehavior> { [5] = b });
        var r = c.Customers.Single().Services.Single().Features.Single(x => x.Key == f.Key);
        return new FeatureValue(r.Value, r.MissingInputs, r.Evidence, r.Text);
    }

    // ---------- Свёртка ----------

    [Fact]
    public void Fold_MapsKnownTypes_ToDailyCounters()
    {
        var login = ActivityProjection.Fold(Ev("account.login", """{"channel":"panel","success":true}"""))!.Value;
        Assert.Equal((5L, Today), (login.AccountId, login.Day));
        Assert.Equal(1, login.Delta.Logins);
        Assert.Equal(1, ActivityProjection.Fold(Ev("account.login", """{"success":false}"""))!.Value.Delta.LoginsFailed);

        // Телеметрия за сутки приходит в 00:05 следующего дня — день берётся из data.date
        var usage = ActivityProjection.Fold(Ev("usage.daily", """{"serviceId":1,"date":"2026-10-24","cpuAvgPct":42.5,"trafficOutGb":3.2}""", at: AsOf.Date.AddMinutes(5)))!.Value;
        Assert.Equal(Today.AddDays(-1), usage.Day);
        Assert.Equal((1, 42.5, 3.2), (usage.Delta.UsageSamples, usage.Delta.CpuSum, usage.Delta.TrafficOutGb));

        Assert.Equal(90, ActivityProjection.Fold(Ev("service.incident", """{"durationMin":90}"""))!.Value.Delta.IncidentMinutes);
        var closed = ActivityProjection.Fold(Ev("ticket.closed", """{"resolution":"unsolved","csat":1}"""))!.Value.Delta;
        Assert.Equal((1, 1), (closed.TicketsUnsolved, closed.CsatMin!.Value));
        Assert.Equal(960, ActivityProjection.Fold(Ev("payment.charged", """{"amount":960}"""))!.Value.Delta.PaymentsMinor);
        Assert.Equal(1, ActivityProjection.Fold(Ev("payment.failed", """{"amount":960}"""))!.Value.Delta.PaymentsFailed);
        Assert.Equal(1, ActivityProjection.Fold(Ev("service.plan_changed", """{"direction":"downgrade"}"""))!.Value.Delta.Downgrades);
        Assert.Equal(1, ActivityProjection.Fold(Ev("service.autoprolong_changed", """{"enabled":false}"""))!.Value.Delta.AutoProlongOff);
        var unknown = ActivityProjection.Fold(Ev("crm.note_added", "{}"))!.Value.Delta;
        Assert.Equal((1, 0), (unknown.Events, unknown.Logins));
        Assert.Null(ActivityProjection.Fold(Ev("datacenter.incident", """{"durationMin":90}""", account: null)));
    }

    [Fact]
    public void Fold_IgnoresBrokenValues_InsteadOfFailing()
    {
        var d = ActivityProjection.Fold(Ev("usage.daily", """{"date":"2026-02-30","cpuAvgPct":"high"}"""))!.Value;
        Assert.Equal(Today, d.Day); // неверная дата — день события
        Assert.Equal(0, d.Delta.UsageSamples);
        Assert.Equal(0, ActivityProjection.Fold(Ev("payment.charged", """{"amount":9.5}"""))!.Value.Delta.PaymentsMinor);
    }

    [Fact]
    public void Delta_Add_SumsCounters_AndTakesMinimumOfScores()
    {
        var a = new ActivityDelta { Logins = 1, CsatMin = 4, NpsMin = null, PaymentsMinor = 100 };
        a.Add(new ActivityDelta { Logins = 2, CsatMin = 2, NpsMin = 7, PaymentsMinor = 50 });
        Assert.Equal((3, 2, 7, 150L), (a.Logins, a.CsatMin!.Value, a.NpsMin!.Value, a.PaymentsMinor));
    }

    // ---------- Признаки: тест-векторы ----------

    [Fact]
    public void NoBehaviorData_IsMissing_NotZero()
    {
        foreach (var f in FeatureCatalog.All.Where(f => f.Behavioral))
        {
            var v = Eval(f, null);
            Assert.True(v.IsMissing, f.Key);
            Assert.Equal(0m, f.DefaultWeight);
        }
    }

    [Fact]
    public void UsageDrop_ComparesLast7DaysWith30DaysBefore()
    {
        // До: 40% CPU каждый день; последние 7 дней — 10%: падение 75% → 1
        var days = Enumerable.Range(7, 30).Select(i => Day(i, d => { d.UsageSamples = 1; d.CpuSum = 40; }))
            .Concat(Enumerable.Range(0, 7).Select(i => Day(i, d => { d.UsageSamples = 1; d.CpuSum = 10; }))).ToArray();
        var v = Eval(new UsageDropFeature(), Behavior(Today.AddDays(-40), Today, days));
        Assert.Equal(1m, v.Value);
        Assert.Equal(75m, v.Evidence["dropPct"]);

        // 30% падения → (0,3 − 0,2) / 0,5 = 0,2
        var mild = Enumerable.Range(7, 30).Select(i => Day(i, d => { d.UsageSamples = 1; d.CpuSum = 50; }))
            .Concat(Enumerable.Range(0, 7).Select(i => Day(i, d => { d.UsageSamples = 1; d.CpuSum = 35; }))).ToArray();
        Assert.Equal(0.2m, Eval(new UsageDropFeature(), Behavior(Today.AddDays(-40), Today, mild)).Value);

        // Мало телеметрии — «нет данных»
        Assert.True(Eval(new UsageDropFeature(), Behavior(Today.AddDays(-3), Today, Day(0, d => { d.UsageSamples = 1; d.CpuSum = 5; }))).IsMissing);
    }

    [Fact]
    public void InactiveLogins_CountsDaysSinceLastLogin_OrSinceDataStart()
    {
        Assert.Equal(0m, Eval(new InactiveLoginsFeature(), Behavior(Today.AddDays(-100), Today.AddDays(-7))).Value);
        Assert.Equal(1m, Eval(new InactiveLoginsFeature(), Behavior(Today.AddDays(-100), Today.AddDays(-30))).Value);
        var mid = Eval(new InactiveLoginsFeature(), Behavior(Today.AddDays(-100), Today.AddDays(-18))).Value!.Value;
        Assert.Equal(Math.Round(11m / 23m, 6), Math.Round(mid, 6)); // линейно между 7 и 30 днями
        // Ни одного входа за 40 дней данных — считается от первого дня данных
        var never = Eval(new InactiveLoginsFeature(), Behavior(Today.AddDays(-40), null));
        Assert.Equal(1m, never.Value);
        Assert.Contains("Входов не было", never.Text);
    }

    [Fact]
    public void PaymentFailures_Support_Incidents_Downgrade()
    {
        var b = Behavior(Today.AddDays(-90), Today,
            Day(3, d => d.PaymentsFailed = 1),
            Day(45, d => d.PaymentsFailed = 5), // вне окна 30 дней
            Day(10, d => { d.TicketsOpened = 1; d.TicketsUnsolved = 1; }),
            Day(2, d => d.IncidentMinutes = 120),
            Day(50, d => d.Downgrades = 1));
        Assert.Equal(0.5m, Eval(new PaymentFailuresFeature(), b).Value);
        Assert.Equal(1m, Eval(new SupportIssuesFeature(), b).Value);
        Assert.Equal(0.5m, Eval(new IncidentsFeature(), b).Value);
        Assert.Equal(1m, Eval(new DowngradeFeature(), b).Value);

        var calm = Behavior(Today.AddDays(-90), Today, Day(5, d => { d.TicketsOpened = 1; d.CsatMin = 2; }), Day(70, d => d.Downgrades = 1));
        Assert.Equal(0.7m, Eval(new SupportIssuesFeature(), calm).Value);
        Assert.Equal(0m, Eval(new DowngradeFeature(), calm).Value);
        Assert.Equal(0m, Eval(new PaymentFailuresFeature(), calm).Value);
    }

    // ---------- Движок ----------

    [Fact]
    public void WeightZero_ChangesNothing_ButFeaturesAreVisible()
    {
        var data = new SourceData([Account(5, balance: 0)], [Service(1, 5, "active", "month", 500, AsOf.AddDays(2), false)], []);
        var b = new Dictionary<long, AccountBehavior> { [5] = Behavior(Today.AddDays(-60), Today.AddDays(-45), Day(1, d => d.PaymentsFailed = 3)) };
        var without = Engine.Evaluate(data, ModelSettings.Default(FeatureCatalog.All), AsOf).Customers.Single();
        var with = Engine.Evaluate(data, ModelSettings.Default(FeatureCatalog.All), AsOf, b).Customers.Single();
        Assert.Equal(without.Score, with.Score);
        Assert.Equal(without.Reasons.Select(r => r.Code), with.Reasons.Select(r => r.Code));
        var f = with.Services.Single().Features.Single(x => x.Key == "payment_failures");
        Assert.Equal((1m, 0m, 0m), (f.Value!.Value, f.Weight, f.Contribution));
        Assert.DoesNotContain(QualityFlags.BehaviorDataMissing, without.Flags); // вес 0 — пропуск не помечается
    }

    [Fact]
    public void WeightedBehavior_MissingData_KeepsScore_WithFlag()
    {
        var data = new SourceData([Account(5, balance: 0)], [Service(1, 5, "active", "month", 500, AsOf.AddDays(2), false)], []);
        var settings = Weights(("usage_drop", 30m));
        var c = Engine.Evaluate(data, settings, AsOf).Customers.Single();
        var baseline = Engine.Evaluate(data, ModelSettings.Default(FeatureCatalog.All), AsOf).Customers.Single();
        Assert.Equal(ScoreState.Complete, c.State); // клиент только с v1 не теряет балл
        Assert.Equal(baseline.Score, c.Score);
        Assert.Contains(QualityFlags.BehaviorDataMissing, c.Flags);
        Assert.Empty(c.Services.Single().MissingInputs);
    }

    [Fact]
    public void WeightedBehavior_AddsContribution_AsOneReasonPerCustomer()
    {
        var data = new SourceData([Account(5, balance: 100_000)],
            [Service(1, 5, "active", "month", 500, AsOf.AddDays(20), true), Service(2, 5, "active", "month", 500, AsOf.AddDays(25), true)], []);
        var b = new Dictionary<long, AccountBehavior> { [5] = Behavior(Today.AddDays(-60), Today.AddDays(-40)) };
        var c = Engine.Evaluate(data, Weights(("inactive_logins", 50m)), AsOf, b).Customers.Single();
        Assert.Equal(50m, c.Score);
        Assert.Equal("inactive_logins", c.PrimaryReason.Code);
        Assert.Single(c.Reasons, r => r.Code == "inactive_logins");
    }

    [Fact]
    public void Generation_WithOlderActivity_IsNotPublishedOverNewer()
    {
        var active = new GenerationEntity { SourceRevision = 1, SourceSeq = 10, ModelRevisionId = 1, ActivitySeq = 500, AsOf = AsOf };
        Assert.False(GenerationPublishing.ShouldPublish(active, new GenerationEntity { SourceRevision = 1, SourceSeq = 10, ModelRevisionId = 1, ActivitySeq = 400, AsOf = AsOf.AddMinutes(5) }));
        Assert.True(GenerationPublishing.ShouldPublish(active, new GenerationEntity { SourceRevision = 1, SourceSeq = 10, ModelRevisionId = 1, ActivitySeq = 600, AsOf = AsOf }));
    }

    [Fact]
    public void FeedText_HasDomain_AndNoRawData()
    {
        Assert.Equal(DomainEventText.Money, DomainEventText.DomainOf("payment.failed"));
        Assert.Equal(DomainEventText.Usage, DomainEventText.DomainOf("service.incident"));
        Assert.Equal(DomainEventText.Service, DomainEventText.DomainOf("service.renewed"));
        Assert.Equal(DomainEventText.Other, DomainEventText.DomainOf("crm.note_added"));
        Assert.Equal("Списание не прошло €9,60: insufficient_funds", DomainEventText.Describe("payment.failed", """{"amount":960,"reason":"insufficient_funds","card":"4111"}"""));
        Assert.Equal("crm.note_added", DomainEventText.Describe("crm.note_added", """{"text":"секрет"}"""));
    }
}
