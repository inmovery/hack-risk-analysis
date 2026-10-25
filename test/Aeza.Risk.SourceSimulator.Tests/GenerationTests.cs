using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Aeza.Risk.Core;
using Aeza.Risk.Core.Source;

using Xunit;

namespace Aeza.Risk.SourceSimulator.Tests;

/// <summary>История симулятора: один seed — одна история</summary>
public sealed class GenerationTests
{
    internal static readonly DateTimeOffset End = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    internal static SimConfig Small(int customers = 150, int days = 60) => new() { Customers = customers, Days = days };

    internal static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aeza-sim-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SameSeed_GivesSameFiles_DifferentSeed_DifferentHistory()
    {
        var a = TempDir();
        var b = TempDir();
        var c = TempDir();
        Dataset.Generate(Small(), 42, End, a);
        Dataset.Generate(Small(), 42, End, b);
        Dataset.Generate(Small(), 43, End, c);
        foreach (var file in new[] { Dataset.SnapshotFile, Dataset.EventsFile, Dataset.TruthFile, Dataset.StateFile })
            Assert.Equal(Dataset.Sha256(Path.Combine(a, file)), Dataset.Sha256(Path.Combine(b, file)));
        Assert.NotEqual(Dataset.Sha256(Path.Combine(a, Dataset.EventsFile)), Dataset.Sha256(Path.Combine(c, Dataset.EventsFile)));
    }

    [Fact]
    public void SavedState_ContinuesExactlyLikeUninterruptedRun()
    {
        var config = Small(120, 60);
        var t0 = End.AddDays(-60);
        var straight = Simulation.CreateHistory(config, 7, t0);
        var all = new ListSink();
        straight.AdvanceTo(t0.AddDays(25), all, all);
        straight.AdvanceTo(t0.AddDays(60), all, all);

        var first = Simulation.CreateHistory(config, 7, t0);
        var resumed = new ListSink();
        first.AdvanceTo(t0.AddDays(25), resumed, resumed);
        var second = Simulation.Load(first.Save()); // «перезапуск процесса»
        second.AdvanceTo(t0.AddDays(60), resumed, resumed);

        Assert.True(all.Events.Count > 1000);
        Assert.Equal(all.Events.Select(e => e.ToJsonLine()), resumed.Events.Select(e => e.ToJsonLine()));
        Assert.Equal(straight.Save(), second.Save());
    }

    [Fact]
    public void EveryEvent_PassesAnalyzerContract_AndConsistentSourceIsNeverStale()
    {
        var dir = TempDir();
        var manifest = Dataset.Generate(Small(200, 90), 11, End, dir);
        var t0 = Iso.Parse(manifest["start"]!.GetValue<string>());

        var snapshot = SnapshotParser.Parse(File.ReadAllBytes(Path.Combine(dir, Dataset.SnapshotFile)));
        Assert.True(snapshot.IsValid, string.Join("; ", snapshot.Errors.Keys.Take(5)));
        Assert.Equal(t0, snapshot.SourceAsOf);
        Assert.True(snapshot.Synthetic);
        var state = SourceState.From(snapshot.Data);

        var lines = File.ReadAllLines(Path.Combine(dir, Dataset.EventsFile));
        Assert.Equal(manifest["events"]!.GetValue<long>(), lines.Length);
        var problems = new List<string>();
        var seqs = new Dictionary<long, long>();
        var ids = new HashSet<string>();
        var last = t0;
        foreach (var chunk in lines.Chunk(1000))
        {
            var body = Encoding.UTF8.GetBytes("{\"synthetic\":true,\"events\":[" + string.Join(',', chunk) + "]}");
            var batch = EventBatchParser.Parse(body, End.AddMinutes(1));
            Assert.True(batch.IsValid);
            Assert.False(batch.Warnings.ContainsKey("unknown_event_type"));
            Assert.False(batch.Warnings.ContainsKey("unknown_event_field"));
            foreach (var e in batch.Events)
            {
                if (!e.IsValid)
                {
                    problems.Add($"rejected {chunk[e.Index][..Math.Min(120, chunk[e.Index].Length)]}: {string.Join(",", e.Errors.Keys)}");
                    continue;
                }
                var d = e.Domain!;
                if (!ids.Add(d.EventId)) problems.Add($"duplicate eventId {d.EventId}");
                if (d.OccurredAt <= t0) problems.Add($"{d.EventId}: occurredAt не позже даты базы");
                if (d.OccurredAt < last) problems.Add($"{d.EventId}: время назад");
                last = d.OccurredAt;
                if (d is { AccountId: { } acc, AccountSeq: { } seq })
                {
                    var expected = seqs.GetValueOrDefault(acc) + 1;
                    if (seq != expected) problems.Add($"{d.EventId}: accountSeq {seq}, ожидалось {expected}");
                    seqs[acc] = seq;
                }
                foreach (var change in e.Changes)
                {
                    var r = state.Apply(change);
                    if (r.Outcome == ChangeOutcome.Stale) problems.Add($"{d.EventId} {d.Type}: stale {r.StaleReason}");
                    if (r.Changes.Contains(ChangeCodes.OwnerUnknown) || r.Changes.Contains(ChangeCodes.ServiceUnknown))
                        problems.Add($"{d.EventId} {d.Type}: запись раньше владельца");
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
    }

    [Fact]
    public void Manifest_DescribesFiles_AndHistory()
    {
        var dir = TempDir();
        var m = Dataset.Generate(Small(80, 30), 5, End, dir);
        Assert.True(m["synthetic"]!.GetValue<bool>());
        Assert.Equal(Iso.Format(End), m["end"]!.GetValue<string>());
        Assert.Equal(Iso.Format(End.AddDays(-30)), m["start"]!.GetValue<string>());
        Assert.Equal(80, m["customers"]!.GetValue<int>());
        foreach (var (file, info) in m["files"]!.AsObject())
            Assert.Equal(Dataset.Sha256(Path.Combine(dir, file)), info!["sha256"]!.GetValue<string>());
        var byType = m["eventsByType"]!.AsObject();
        Assert.Equal(m["events"]!.GetValue<long>(), byType.Sum(kv => kv.Value!.GetValue<long>()));
        foreach (var type in new[] { "account.registered", "account.login", "usage.daily", "payment.charged", "service.renewed", "service.expired", "balance.topped_up" })
            Assert.True(byType.ContainsKey(type), type);
        // Истина отдельно: в событиях нет скрытого состояния
        var events = File.ReadAllText(Path.Combine(dir, Dataset.EventsFile));
        Assert.DoesNotContain("\"segment\"", events);
        Assert.DoesNotContain("\"intent", events);
        using var truth = new StreamReader(Path.Combine(dir, Dataset.TruthFile));
        Assert.Equal("profile", JsonNode.Parse(truth.ReadLine()!)!["k"]!.GetValue<string>());
        _ = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, Dataset.ManifestFile)));
    }
}
