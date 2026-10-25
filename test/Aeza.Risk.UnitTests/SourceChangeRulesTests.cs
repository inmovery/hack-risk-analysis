using System.Text;

using Aeza.Risk.Core;
using Aeza.Risk.Core.Source;

using Xunit;

namespace Aeza.Risk.UnitTests;

/// <summary>Применение событий без версии: монотонные инварианты, дедупликация по содержимому, производные факты и исходы сроков</summary>
public sealed class SourceChangeRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static ServiceInput Svc(long id, long owner, string status, DateTimeOffset expires, long price = 900, bool auto = true) =>
        new(id, owner, $"service-{id:0000}", "month", auto, expires.AddMonths(-1), expires, status, price);

    private static AccountInput Acc(long id, long balance, long total) => new(id, $"user-{id}@example.test", balance, 0, total, 0, "locked");

    [Fact]
    public void Renewal_IsApplied_WithRenewedAndStatusCodes()
    {
        var before = Svc(5, 5, "prolong_wait", T0.AddDays(-1));
        var after = Svc(5, 5, "active", T0.AddMonths(1).AddDays(-1));
        var r = SourceChangeRules.ApplyService(before, after);
        Assert.Equal(ChangeOutcome.Applied, r.Outcome);
        Assert.Contains(ChangeCodes.Renewed, r.Changes);
        Assert.Contains(ChangeCodes.StatusChanged, r.Changes);
        Assert.Equal("prolong_wait", r.Effect["statusFrom"]);
        Assert.Equal("active", r.Effect["statusTo"]);
    }

    [Fact]
    public void LateEventWithOlderExpiry_IsStale()
    {
        var current = Svc(5, 5, "active", T0.AddMonths(1));
        var late = Svc(5, 5, "prolong_wait", T0);
        var r = SourceChangeRules.ApplyService(current, late);
        Assert.Equal(ChangeOutcome.Stale, r.Outcome);
        Assert.Equal(StaleReasons.ExpiresRegressed, r.StaleReason);
    }

    [Fact]
    public void Deleted_IsTerminal()
    {
        var r = SourceChangeRules.ApplyService(Svc(5, 5, "deleted", T0), Svc(5, 5, "active", T0.AddMonths(1)));
        Assert.Equal(ChangeOutcome.Stale, r.Outcome);
        Assert.Equal(StaleReasons.DeletedIsTerminal, r.StaleReason);
    }

    [Fact]
    public void SameState_WithoutEventId_IsNoChange()
    {
        var s = Svc(5, 5, "active", T0);
        Assert.Equal(ChangeOutcome.NoChange, SourceChangeRules.ApplyService(s, s with { }).Outcome);
    }

    [Fact]
    public void SameExpiry_DifferentStatus_LastReceivedWins()
    {
        var r = SourceChangeRules.ApplyService(Svc(5, 5, "active", T0), Svc(5, 5, "suspended", T0));
        Assert.Equal(ChangeOutcome.Applied, r.Outcome);
        Assert.Equal([ChangeCodes.StatusChanged], r.Changes);
    }

    [Fact]
    public void TotalReplenished_NeverDecreases()
    {
        var current = Acc(1, 5000, 10_000);
        Assert.Equal(StaleReasons.TotalReplenishedRegressed, SourceChangeRules.ApplyAccount(current, Acc(1, 9000, 9_000)).StaleReason);
        var topUp = SourceChangeRules.ApplyAccount(current, Acc(1, 7000, 12_000));
        Assert.Equal(ChangeOutcome.Applied, topUp.Outcome);
        Assert.Contains(ChangeCodes.ToppedUp, topUp.Changes);
        Assert.Equal(2000L, topUp.Effect["toppedUpMinor"]);
        Assert.DoesNotContain("email", string.Join(',', topUp.Effect.Keys), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Performed_IsNotRolledBack()
    {
        var performed = new TransactionInput(1, 5, 900, "performed", T0, T0, "prolong");
        var pending = performed with { Status = "not_performed", PerformedAt = null };
        Assert.Equal(StaleReasons.PerformedIsTerminal, SourceChangeRules.ApplyTransaction(performed, pending, 5).StaleReason);
        var paid = SourceChangeRules.ApplyTransaction(pending, performed, 5);
        Assert.Contains(ChangeCodes.PaymentPerformed, paid.Changes);
    }

    [Fact]
    public void State_DetectsUnknownOwner_AndCustomerLostAllServices()
    {
        var state = SourceState.From(new SourceData([Acc(1, 0, 0)], [Svc(1, 1, "prolong_wait", T0), Svc(2, 1, "deleted", T0.AddDays(-40))], []));

        var orphan = state.Apply(SourceChange.Of(Svc(9, 77, "active", T0.AddMonths(1))));
        Assert.Contains(ChangeCodes.OwnerUnknown, orphan.Changes);

        var lastDeleted = state.Apply(SourceChange.Of(Svc(1, 1, "deleted", T0)));
        Assert.Contains(ChangeCodes.Deleted, lastDeleted.Changes);
        Assert.Contains(ChangeCodes.CustomerLostAllServices, lastDeleted.Changes);
        Assert.Equal(1, lastDeleted.OwnerId);
    }

    [Fact]
    public void Replay_OutOfOrderPair_ConvergesToNewestState()
    {
        // Две перестановки одного продления (задержка до 5 с): результат одинаков
        var start = new SourceData([Acc(1, 0, 0)], [Svc(1, 1, "prolong_wait", T0)], []);
        var renewal = SourceChange.Of(Svc(1, 1, "active", T0.AddMonths(1)));
        var expiryEcho = SourceChange.Of(Svc(1, 1, "prolong_wait", T0));

        var a = SourceState.From(start);
        a.Apply(expiryEcho);
        a.Apply(renewal);
        var b = SourceState.From(start);
        b.Apply(renewal);
        Assert.Equal(ChangeOutcome.Stale, b.Apply(expiryEcho).Outcome);
        Assert.Equal(a.Services[1], b.Services[1]);
    }

    [Fact]
    public void Expiry_RenewedBeforeGrace_IsRenewed_AfterGrace_IsLate()
    {
        var before = Svc(1, 1, "prolong_wait", T0);
        var after = Svc(1, 1, "active", T0.AddMonths(1));
        var actions = ExpiryOutcomeRules.OnServiceObserved(before, after, T0.AddDays(2), graceDays: 5);
        var resolve = Assert.Single(actions.OfType<ResolveExpiry>());
        Assert.Equal(ExpiryOutcomes.Renewed, resolve.Outcome);
        Assert.Equal(ExpiryOutcomes.NotRenewed, resolve.LateOutcome);
        Assert.Equal(T0.AddMonths(1), resolve.ExpiresBefore);
        var register = Assert.Single(actions.OfType<RegisterExpiry>());
        Assert.Equal(T0.AddMonths(1), register.ExpiresAt);
    }

    [Fact]
    public void Expiry_Deleted_IsNotRenewed_Blocked_IsExcluded()
    {
        var deleted = Assert.Single(ExpiryOutcomeRules.OnServiceObserved(Svc(1, 1, "prolong_wait", T0), Svc(1, 1, "deleted", T0), T0.AddDays(5), 5));
        Assert.Equal(ExpiryOutcomes.NotRenewed, ((ResolveExpiry)deleted).Outcome);
        var blocked = Assert.Single(ExpiryOutcomeRules.OnServiceObserved(Svc(1, 1, "active", T0), Svc(1, 1, "blocked", T0), T0, 5));
        Assert.Equal(ExpiryOutcomes.Excluded, ((ResolveExpiry)blocked).Outcome);
    }

    [Fact]
    public void Expiry_ObservedAfterGrace_IsNotRegistered()
    {
        // Тестовый снимок: prolong_wait просрочен на 35+ дней — исход неизвестен, не «не продлено»
        Assert.Empty(ExpiryOutcomeRules.OnServiceObserved(null, Svc(1, 1, "prolong_wait", T0.AddDays(-35)), T0, 5));
    }

    [Theory]
    [InlineData(3, null)] // источник не обновлялся после E + N — ждём
    [InlineData(7, "not_renewed")]
    public void Expiry_DueResolution_RequiresFreshSource(int freshDaysAfterExpiry, string? expected)
    {
        var expires = T0;
        var result = ExpiryOutcomeRules.ResolveDue(expires, Svc(1, 1, "prolong_wait", expires), expires.AddDays(freshDaysAfterExpiry), T0.AddDays(10), 5);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BatchParser_RejectsOnlyBrokenEvents_WithPaths()
    {
        const string json = """
        { "synthetic": true, "events": [
          { "eventId": "e1", "type": "service", "data": { "id": 1, "ownerId": 1, "name": "s", "status": "active", "paymentTerm": "month", "autoProlong": true, "expiresAt": "2026-11-24T10:00:00Z", "price": 900 } },
          { "type": "account", "data": { "id": 1, "email": "a@example.test", "balance": 1.5 } },
          { "type": "invoice", "data": {} }
        ] }
        """;
        var batch = EventBatchParser.Parse(Encoding.UTF8.GetBytes(json));
        Assert.True(batch.IsValid);
        Assert.True(batch.Synthetic);
        Assert.True(batch.Events[0].IsValid);
        Assert.Equal("e1", batch.Events[0].EventId);
        Assert.Contains("events[1].data.balance", batch.Events[1].Errors.Keys);
        Assert.Contains("events[2].type", batch.Events[2].Errors.Keys);
    }

    [Fact]
    public void BatchParser_RejectsEnvelopeOverLimit()
    {
        var events = string.Join(',', Enumerable.Repeat("""{"type":"account","data":{}}""", EventBatchParser.MaxEvents + 1));
        var batch = EventBatchParser.Parse(Encoding.UTF8.GetBytes($$"""{"events":[{{events}}]}"""));
        Assert.False(batch.IsValid);
        Assert.Contains("events", batch.EnvelopeErrors.Keys);
    }
}
