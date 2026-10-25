using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Aeza.Risk.SourceSimulator.Tests;

/// <summary>Исходящий коннектор: позиция сдвигается только после 200/202</summary>
public sealed class SenderTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeAnalyzer : HttpMessageHandler
    {
        public Func<int, List<JsonObject>, HttpResponseMessage?> Script { get; set; } = (_, _) => null;
        public List<List<JsonObject>> Accepted { get; } = [];
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = ++Calls;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var events = body["events"]!.AsArray().Select(e => e!.AsObject()).ToList();
            var scripted = Script(call, events);
            if (scripted is not null) return scripted;
            Accepted.Add(events);
            var result = new JsonObject { ["received"] = events.Count, ["applied"] = 0, ["recorded"] = events.Count, ["duplicates"] = 0, ["rejected"] = 0, ["stale"] = 0 };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private static string Outbox(int count, int accounts = 3)
    {
        var dir = GenerationTests.TempDir();
        var path = Path.Combine(dir, "outbox.jsonl");
        var seq = new Dictionary<long, long>();
        var lines = Enumerable.Range(1, count).Select(i =>
        {
            var a = (long)(i % accounts) + 1;
            seq[a] = seq.GetValueOrDefault(a) + 1;
            return new SimEvent($"e{i:D4}", "account.login", T.AddSeconds(i), a, seq[a], null, new JsonObject(), null).ToJsonLine();
        });
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static OutboxSender Sender(HttpMessageHandler handler, string outbox, SenderOptions? o = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://analyzer") }, outbox, outbox + ".cursor", "run1",
            o ?? new SenderOptions { BatchSize = 10, MinDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5), ClientErrorDelay = TimeSpan.FromMilliseconds(5) },
            NullLogger.Instance);

    private static Task Drain(OutboxSender s, string outbox, DateTimeOffset through) =>
        s.RunAsync(() => new FileInfo(outbox).Length, () => through, stopWhenDrained: true, CancellationToken.None);

    private static List<string> Ids(IEnumerable<List<JsonObject>> batches) =>
        batches.SelectMany(b => b).Where(e => e["type"]!.GetValue<string>() != "stream.watermark").Select(e => e["eventId"]!.GetValue<string>()).ToList();

    [Fact]
    public async Task Failures_AreRetried_PositionMovesOnlyOnSuccess_WatermarkAtTheEnd()
    {
        var outbox = Outbox(25);
        var fake = new FakeAnalyzer
        {
            Script = (call, _) => call switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                2 => throw new HttpRequestException("connection refused"),
                3 => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } },
                _ => null,
            },
        };
        var sender = Sender(fake, outbox);
        await Drain(sender, outbox, T.AddMinutes(5));

        Assert.Equal(Enumerable.Range(1, 25).Select(i => $"e{i:D4}"), Ids(fake.Accepted));
        Assert.Equal(new FileInfo(outbox).Length, sender.Cursor.Offset);
        Assert.Equal(25, sender.Cursor.Events);
        var wm = fake.Accepted[^1].Single();
        Assert.Equal("stream.watermark", wm["type"]!.GetValue<string>());
        Assert.Equal(Iso.Format(T.AddMinutes(5)), wm["data"]!["upTo"]!.GetValue<string>());
        Assert.Equal(T.AddMinutes(5), Cursor.Load(outbox + ".cursor").Watermark);
    }

    [Fact]
    public async Task Restart_ContinuesFromCursor_LostAckOnlyRepeatsThatBatch()
    {
        var outbox = Outbox(40);
        var fake = new FakeAnalyzer();
        var first = Sender(fake, outbox);
        first.StopAfterBatches = 2; // «падение» симулятора после двух подтверждённых пакетов
        await first.RunAsync(() => new FileInfo(outbox).Length, () => T, stopWhenDrained: false, CancellationToken.None);
        Assert.Equal(20, first.Cursor.Events);

        // Ответ на следующий пакет теряется: анализатор принял, источник не узнал и повторяет
        var lost = false;
        fake.Script = (_, events) =>
        {
            if (lost || events.Count != 10) return null;
            lost = true;
            fake.Accepted.Add(events);
            throw new HttpRequestException("connection reset");
        };
        var second = Sender(fake, outbox);
        Assert.Equal(20, second.Cursor.Events);
        await Drain(second, outbox, T.AddMinutes(1));

        var ids = Ids(fake.Accepted);
        Assert.Equal(Enumerable.Range(1, 40).Select(i => $"e{i:D4}"), ids.Distinct());
        Assert.Equal(50, ids.Count); // один пакет из 10 — дважды: анализатор ответит duplicate
        Assert.Equal(40, second.Cursor.Events);
    }

    [Fact]
    public async Task TooLarge_HalvesBatch()
    {
        var outbox = Outbox(23);
        var fake = new FakeAnalyzer { Script = (_, events) => events.Count > 4 ? new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge) : null };
        var sender = Sender(fake, outbox);
        await Drain(sender, outbox, T.AddMinutes(1));
        Assert.Equal(23, Ids(fake.Accepted).Count);
        Assert.All(fake.Accepted, b => Assert.True(b.Count <= 4));
    }

    [Fact]
    public async Task Shuffle_KeepsOrderWithinAccount()
    {
        var outbox = Outbox(60, accounts: 4);
        var fake = new FakeAnalyzer();
        var sender = Sender(fake, outbox, new SenderOptions { BatchSize = 30, Shuffle = 1, MinDelay = TimeSpan.FromMilliseconds(1) });
        await Drain(sender, outbox, T.AddMinutes(1));
        var events = fake.Accepted.SelectMany(b => b).Where(e => e["type"]!.GetValue<string>() != "stream.watermark").ToList();
        Assert.NotEqual(events.Select(e => e["eventId"]!.GetValue<string>()), events.Select(e => e["eventId"]!.GetValue<string>()).Order());
        foreach (var g in events.GroupBy(e => e["accountId"]!.GetValue<long>()))
            Assert.Equal(g.Select(e => e["accountSeq"]!.GetValue<long>()).Order(), g.Select(e => e["accountSeq"]!.GetValue<long>()));
    }

    [Fact]
    public async Task ClientError_IsNotSkipped_SenderWaitsForFix()
    {
        var outbox = Outbox(5);
        var fake = new FakeAnalyzer { Script = (call, _) => call <= 2 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : null };
        var sender = Sender(fake, outbox);
        await Drain(sender, outbox, T.AddMinutes(1));
        Assert.Equal(5, Ids(fake.Accepted).Count); // после исправления токена отправка продолжилась, ничего не потеряно
        _ = JsonDocument.Parse(File.ReadAllText(outbox + ".cursor"));
    }
}
