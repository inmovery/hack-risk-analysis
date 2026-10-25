using System.Text;

using Aeza.Risk.Core;
using Aeza.Risk.Core.Source;

using Xunit;

namespace Aeza.Risk.UnitTests;

/// <summary>Конверт доменного события v2: обязательные поля, каталог типов, state, водяной знак, смешанные пакеты с v1 и учёт пропусков accountSeq</summary>
public sealed class EventEnvelopeV2Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private const string Service5 =
        """{ "id": 5, "ownerId": 5, "name": "service-0005", "status": "active", "paymentTerm": "month", "autoProlong": true, "createdAt": "2026-09-24T10:00:00Z", "expiresAt": "2026-11-24T10:00:00Z", "price": 900 }""";

    private const string Account5 =
        """{ "id": 5, "email": "user-0005@example.test", "balance": 1000, "withdrawBalance": 0, "totalReplenished": 5000, "bonusBalance": 0, "bonusState": "locked" }""";

    private static ParsedEventBatch Parse(params string[] events) =>
        EventBatchParser.Parse(Encoding.UTF8.GetBytes($$"""{ "events": [{{string.Join(',', events)}}] }"""), Now);

    private static string V2(string type, string extra = "", string occurredAt = "2026-10-24T11:00:00Z", string id = "evt-1") =>
        $$"""{ "schemaVersion": 2, "eventId": "{{id}}", "type": "{{type}}", "occurredAt": "{{occurredAt}}"{{extra}} }""";

    [Fact]
    public void StateCarried_Renewal_ParsesDomainEvent_AndRecordState()
    {
        var b = Parse(V2("service.renewed", $$""", "accountId": 5, "accountSeq": 7, "serviceId": 5, "data": { "amount": 900 }, "state": { "service": {{Service5}} }"""));
        var e = Assert.Single(b.Events);
        Assert.True(e.IsValid, string.Join("; ", e.Errors.Keys));
        Assert.Equal("evt-1", e.EventId);
        Assert.NotNull(e.Domain);
        Assert.True(e.Domain.KnownType);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 11, 0, 0, TimeSpan.Zero), e.Domain.OccurredAt);
        Assert.Equal(7, e.Domain.AccountSeq);
        Assert.Contains("\"amount\"", e.Domain.DataJson);
        var change = Assert.Single(e.Changes);
        Assert.Equal(SourceRecordKind.Service, change.Kind);
        Assert.Null(change.EventId); // дедупликация — по eventId доменного события, не записи
    }

    [Fact]
    public void Behavioral_Event_WithoutState_IsValid_AndHasNoRecordChanges()
    {
        var b = Parse(V2("usage.daily", """, "accountId": 5, "accountSeq": 8, "serviceId": 5, "data": { "cpuAvgPct": 3.5, "netOutMb": 120 }"""));
        var e = Assert.Single(b.Events);
        Assert.True(e.IsValid);
        Assert.Empty(e.Changes);
        Assert.True(e.Domain!.KnownType);
    }

    [Fact]
    public void UnknownType_IsAccepted_WithWarning()
    {
        var b = Parse(V2("crm.note_added", """, "accountId": 5, "accountSeq": 9, "data": {}"""));
        var e = Assert.Single(b.Events);
        Assert.True(e.IsValid);
        Assert.False(e.Domain!.KnownType);
        Assert.Equal(1, b.Warnings["unknown_event_type"]);
    }

    [Fact]
    public void RequiredFields_AreReported_ByPath()
    {
        var b = Parse("""{ "schemaVersion": 2, "type": "service.renewed", "accountId": 5, "data": {}, "state": {} }""");
        var e = Assert.Single(b.Events);
        Assert.False(e.IsValid);
        Assert.Contains("events[0].eventId", e.Errors.Keys);
        Assert.Contains("events[0].occurredAt", e.Errors.Keys);
        Assert.Contains("events[0].accountSeq", e.Errors.Keys);
        Assert.Contains("events[0].state.service", e.Errors.Keys); // тип меняет услугу — нужно её состояние после события
    }

    [Fact]
    public void AccountEvent_WithNullAccount_IsMissing_NotSilentlyGlobal()
    {
        var e = Assert.Single(Parse(V2("account.login", """, "accountId": null, "accountSeq": null, "data": {}""")).Events);
        Assert.Contains("events[0].accountId", e.Errors.Keys);
        Assert.Contains("events[0].accountSeq", e.Errors.Keys);
        var invalid = Assert.Single(Parse(V2("account.login", """, "accountId": 0, "accountSeq": 1, "data": {}""")).Events);
        Assert.Single(invalid.Errors["events[0].accountId"]); // только invalid_id, без «обязателен»
    }

    [Theory]
    [InlineData("Service.Renewed")]
    [InlineData("renewed")]
    [InlineData("service..renewed")]
    public void Type_MustBe_DomainDotAction(string type)
    {
        var e = Assert.Single(Parse(V2(type, """, "accountId": 5, "accountSeq": 1, "data": {}""")).Events);
        Assert.Contains("events[0].type", e.Errors.Keys);
    }

    [Fact]
    public void State_MustMatch_EventIdentifiers()
    {
        var b = Parse(V2("service.renewed", $$""", "accountId": 6, "accountSeq": 1, "serviceId": 9, "data": {}, "state": { "service": {{Service5}} }"""));
        var e = Assert.Single(b.Events);
        Assert.Contains("events[0].state.service.ownerId", e.Errors.Keys);
        Assert.Contains("events[0].state.service.id", e.Errors.Keys);
    }

    [Fact]
    public void State_IsApplied_AccountFirst_ThenService()
    {
        var b = Parse(V2("service.ordered", $$""", "accountId": 5, "accountSeq": 1, "data": {}, "state": { "service": {{Service5}}, "account": {{Account5}} }"""));
        var e = Assert.Single(b.Events);
        Assert.True(e.IsValid, string.Join("; ", e.Errors.Keys));
        Assert.Equal([SourceRecordKind.Account, SourceRecordKind.Service], e.Changes.Select(c => c.Kind));
    }

    [Fact]
    public void BrokenStateRecord_ReportsFieldError_WithoutDuplicateRequired()
    {
        var broken = Service5.Replace("\"price\": 900", "\"price\": 9.5");
        var e = Assert.Single(Parse(V2("service.renewed", $$""", "accountId": 5, "accountSeq": 1, "data": {}, "state": { "service": {{broken}} }""")).Events);
        Assert.Contains("events[0].state.service.price", e.Errors.Keys);
        Assert.DoesNotContain("events[0].state.service", e.Errors.Keys);
    }

    [Fact]
    public void GlobalEvent_HasNoAccount_AndWatermarkNeedsUpTo()
    {
        var b = Parse(
            V2("tariff.price_changed", """, "accountId": 5, "data": {}""", id: "g1"),
            V2("stream.watermark", """, "data": {}""", id: "g2"),
            V2("stream.watermark", """, "data": { "upTo": "2026-10-24T11:30:00Z" }""", id: "g3"),
            V2("stream.watermark", """, "data": { "upTo": "2026-10-24T10:59:00Z" }""", id: "g4"));
        Assert.Contains("events[0].accountId", b.Events[0].Errors.Keys);
        Assert.Contains("events[1].data.upTo", b.Events[1].Errors.Keys);
        Assert.Contains("events[2].data.upTo", b.Events[2].Errors.Keys); // полнота позже самого подтверждения
        Assert.True(b.Events[3].IsValid);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 10, 59, 0, TimeSpan.Zero), b.Events[3].Domain!.WatermarkUpTo);
    }

    [Fact]
    public void OccurredAt_FromFuture_IsRejected_BeyondClockSkew()
    {
        var b = Parse(
            V2("account.login", """, "accountId": 5, "accountSeq": 1, "data": {}""", occurredAt: "2026-10-24T12:04:00Z", id: "a"),
            V2("account.login", """, "accountId": 5, "accountSeq": 2, "data": {}""", occurredAt: "2026-10-24T12:06:00Z", id: "b"));
        Assert.True(b.Events[0].IsValid);
        Assert.Contains("events[1].occurredAt", b.Events[1].Errors.Keys);
    }

    [Fact]
    public void Batch_MixesV1AndV2_AndRejectsUnknownSchemaVersion()
    {
        var b = Parse(
            $$"""{ "eventId": "v1-1", "type": "account", "data": {{Account5}} }""",
            V2("account.login", """, "accountId": 5, "accountSeq": 1, "data": {}"""),
            """{ "schemaVersion": 3, "eventId": "x", "type": "account.login", "data": {} }""");
        Assert.Null(b.Events[0].Domain);
        Assert.Equal("v1-1", Assert.Single(b.Events[0].Changes).EventId);
        Assert.NotNull(b.Events[1].Domain);
        Assert.Contains("events[2].schemaVersion", b.Events[2].Errors.Keys);
    }

    [Fact]
    public void AccountSeq_FirstIsBaseline_GapsOpenAndFill_OldIsLate()
    {
        var (s, o) = AccountSeqState.Observe(null, 10);
        Assert.Equal(SeqObservation.InOrder, o); // поток мог начаться после снимка: ранние номера — не пропуск
        (s, o) = AccountSeqState.Observe(s, 11);
        Assert.Equal(SeqObservation.InOrder, o);
        (s, o) = AccountSeqState.Observe(s, 14);
        Assert.Equal(SeqObservation.GapOpened, o);
        Assert.Equal([12L, 13L], s.Missing);
        (s, o) = AccountSeqState.Observe(s, 12);
        Assert.Equal(SeqObservation.GapFilled, o);
        Assert.Equal([13L], s.Missing);
        Assert.Equal(14, s.MaxSeq);
        (s, o) = AccountSeqState.Observe(s, 9);
        Assert.Equal(SeqObservation.Late, o);
        (_, o) = AccountSeqState.Observe(s, 14);
        Assert.Equal(SeqObservation.Late, o);
    }

    [Fact]
    public void AccountSeq_HugeGap_IsCapped()
    {
        var (s, _) = AccountSeqState.Observe(new AccountSeqState(1, []), 1_000_000);
        Assert.Equal(AccountSeqState.MaxTrackedMissing, s.Missing.Count);
        Assert.Equal(1_000_000, s.MaxSeq);
    }

    [Theory]
    [InlineData("crm-export", true)]
    [InlineData("billing2", true)]
    [InlineData("http-push", false)]
    [InlineData("panel", false)]
    [InlineData("snapshot", false)]
    [InlineData("CRM", false)]
    [InlineData("", false)]
    [InlineData("a_b", false)]
    public void ConnectorNames_AreLowercase_AndNotReserved(string name, bool valid) =>
        Assert.Equal(valid, ConnectorNames.IsValidCustom(name));
}
