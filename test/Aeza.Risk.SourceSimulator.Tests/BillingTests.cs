using System.Text.Json.Nodes;

using Xunit;

namespace Aeza.Risk.SourceSimulator.Tests;

/// <summary>Биллинг по правилам источника: срок → автопродление при наличии средств, иначе prolong_wait</summary>
public sealed class BillingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static SimConfig Passive() => new()
    {
        LiveRegistrationsPerDay = 0,
        IncidentsPerLocationPerDay = 0,
        PriceChanges = [],
        Segments =
        [
            new SegmentConfig
            {
                Name = "passive", Share = 1, Attentive = false, TopUpAttention = 0, Satisfaction = 0.8, Need = 0.5, Finance = 0.8,
                Terms = new() { ["month"] = 1 }, Plans = new() { ["vps-s"] = 1 }, Habits = new() { ["manual"] = 1 }, LoginsPerDay = 0,
            },
        ],
    };

    private static JsonObject Svc(long id, long owner, string status, DateTimeOffset expires, bool auto, long price = 549, string term = "month") => new()
    {
        ["id"] = id, ["name"] = $"service-{id:0000}", ["ownerId"] = owner, ["paymentTerm"] = term, ["autoProlong"] = auto,
        ["createdAt"] = Iso.Format(expires.AddMonths(-1)), ["expiresAt"] = Iso.Format(expires), ["status"] = status, ["price"] = price,
    };

    private static JsonObject Acc(long id, long balance) => new()
    {
        ["id"] = id, ["email"] = $"user-{id}@example.test", ["balance"] = balance, ["withdrawBalance"] = 0, ["totalReplenished"] = balance,
        ["bonusBalance"] = 0, ["bonusState"] = "locked",
    };

    private static (Simulation Sim, ListSink Sink) Run(JsonObject data, TimeSpan duration)
    {
        var sim = Simulation.FromSourceData(data, Passive(), 1, Now, "test");
        var sink = new ListSink();
        sim.AdvanceTo(Now + duration, sink, sink);
        return (sim, sink);
    }

    /// <summary>События услуги без телеметрии</summary>
    private static List<SimEvent> Of(ListSink sink, long serviceId) => sink.Events.Where(e => e.ServiceId == serviceId && e.Type != "usage.daily").ToList();

    [Fact]
    public void Renewal_ChargesBalance_AndMovesExpiryOneTerm()
    {
        var expires = Now.AddHours(1);
        var (sim, sink) = Run(new JsonObject
        {
            ["accounts"] = new JsonArray(Acc(1, 10_000)), ["services"] = new JsonArray(Svc(10, 1, "active", expires, auto: true)), ["transactions"] = new JsonArray(),
        }, TimeSpan.FromDays(2));

        var events = Of(sink, 10);
        Assert.Equal(["payment.charged", "service.renewed"], events.Select(e => e.Type));
        Assert.Equal(expires, events[0].OccurredAt);
        Assert.Equal(549, events[0].Data["amount"]!.GetValue<long>());
        Assert.Equal(10_000 - 549, events[0].State!["account"]!["balance"]!.GetValue<long>());
        Assert.Equal("performed", events[0].State!["transaction"]!["status"]!.GetValue<string>());
        Assert.Equal(Iso.Format(expires.AddMonths(1)), events[1].State!["service"]!["expiresAt"]!.GetValue<string>());
        Assert.Equal(10_000 - 549, sim.W.Accounts[1].Balance);
    }

    [Fact]
    public void NoFunds_FailedPayment_ProlongWait_ThenDeletedAfterFiveDays()
    {
        var expires = Now.AddHours(1);
        var (_, sink) = Run(new JsonObject
        {
            ["accounts"] = new JsonArray(Acc(1, 100)), ["services"] = new JsonArray(Svc(10, 1, "active", expires, auto: true)), ["transactions"] = new JsonArray(),
        }, TimeSpan.FromDays(8));

        var events = Of(sink, 10);
        Assert.Equal("payment.failed", events[0].Type);
        Assert.Equal("not_performed", events[0].State!["transaction"]!["status"]!.GetValue<string>());
        Assert.Equal("service.expired", events[1].Type);
        Assert.Equal("no_funds", events[1].Data["reason"]!.GetValue<string>());
        Assert.Equal("prolong_wait", events[1].State!["service"]!["status"]!.GetValue<string>());
        Assert.Equal(3, events.Count(e => e.Type == "payment.failed")); // первая попытка и два повтора
        var deleted = events.Single(e => e.Type == "service.deleted");
        Assert.Equal(expires.AddDays(5), deleted.OccurredAt);
        Assert.Equal("non_payment", deleted.Data["reason"]!.GetValue<string>());
        Assert.Equal("service.deleted", events[^1].Type);
        Assert.Contains(sink.Truth, t => t["k"]!.GetValue<string>() == "lost_all_services" && t["cause"]!.GetValue<string>() == Causes.Neglect);
    }

    [Fact]
    public void AutoProlongOff_ExpiresWithoutCharge()
    {
        var expires = Now.AddHours(3);
        var (_, sink) = Run(new JsonObject
        {
            ["accounts"] = new JsonArray(Acc(1, 50_000)), ["services"] = new JsonArray(Svc(10, 1, "active", expires, auto: false)), ["transactions"] = new JsonArray(),
        }, TimeSpan.FromDays(6));
        var events = Of(sink, 10);
        Assert.Equal(["service.expired", "service.deleted"], events.Select(e => e.Type));
        Assert.Equal("autoprolong_off", events.First(e => e.Type == "service.expired").Data["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Suspended_IsBilled_AndStaysSuspended_BlockedIsNotRenewed()
    {
        var expires = Now.AddHours(2);
        var (sim, sink) = Run(new JsonObject
        {
            ["accounts"] = new JsonArray(Acc(1, 10_000), Acc(2, 10_000)),
            ["services"] = new JsonArray(Svc(10, 1, "suspended", expires, auto: true), Svc(20, 2, "blocked", expires, auto: true)),
            ["transactions"] = new JsonArray(),
        }, TimeSpan.FromDays(3));

        var renewed = Of(sink, 10).Single(e => e.Type == "service.renewed");
        Assert.Equal("suspended", renewed.State!["service"]!["status"]!.GetValue<string>());
        Assert.Equal(10_000 - 549, sim.W.Accounts[1].Balance);
        Assert.DoesNotContain(Of(sink, 20), e => e.Type is "payment.charged" or "service.renewed" or "service.expired");
        Assert.Equal(10_000, sim.W.Accounts[2].Balance);
    }

    [Fact]
    public void OverdueProlongWait_FromTestData_IsDeletedRightAway_InTheFutureOnly()
    {
        var (_, sink) = Run(new JsonObject
        {
            ["accounts"] = new JsonArray(Acc(1, 0)), ["services"] = new JsonArray(Svc(10, 1, "prolong_wait", Now.AddDays(-9), auto: false)), ["transactions"] = new JsonArray(),
        }, TimeSpan.FromHours(1));
        var deleted = Of(sink, 10).Single(e => e.Type == "service.deleted");
        Assert.True(deleted.OccurredAt > Now); // не в прошлом относительно момента запуска
    }
}
