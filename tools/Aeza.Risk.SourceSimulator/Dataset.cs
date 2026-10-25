using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aeza.Risk.SourceSimulator;

/// <summary>Обогащённые тестовые данные: snapshot.json (состояние на T₀), events.jsonl (события v2 за T₀…T₁), truth.jsonl (скрытое состояние</summary>
public static class Dataset
{
    public const string SnapshotFile = "snapshot.json";
    public const string EventsFile = "events.jsonl";
    public const string TruthFile = "truth.jsonl";
    public const string ManifestFile = "manifest.json";
    public const string StateFile = "state.json";

    /// <summary>Сгенерировать историю <paramref name="config"/>.Days дней, заканчивающуюся в <paramref name="end"/></summary>
    public static JsonObject Generate(SimConfig config, ulong seed, DateTimeOffset end, string dir, bool truthDaily = true, Action<string>? progress = null)
    {
        Directory.CreateDirectory(dir);
        end = Iso.Sec(end);
        var t0 = end.AddDays(-config.Days);
        var sim = Simulation.CreateHistory(config, seed, t0);
        sim.TruthDaily = truthDaily;

        File.WriteAllText(Path.Combine(dir, SnapshotFile), Snapshot(sim, t0).ToJsonString(Iso.Compact) + "\n");
        var stats = new Stats();
        using (var events = new JsonlWriter(Path.Combine(dir, EventsFile)))
        using (var truth = new JsonlWriter(Path.Combine(dir, TruthFile)))
        {
            var sink = new TeeSink(events, stats);
            for (var at = t0.AddDays(30); ; at = at.AddDays(30))
            {
                var target = at < end ? at : end;
                sim.AdvanceTo(target, sink, truth);
                progress?.Invoke($"{Iso.Date(target)}: событий {stats.Total}");
                if (target >= end) break;
            }
        }
        File.WriteAllText(Path.Combine(dir, StateFile), sim.Save());

        var w = sim.W;
        var manifest = new JsonObject
        {
            ["generator"] = "Aeza.Risk.SourceSimulator",
            ["formatVersion"] = 1,
            ["synthetic"] = true,
            ["runId"] = w.RunId,
            ["seed"] = seed,
            ["start"] = Iso.Format(t0),
            ["end"] = Iso.Format(end),
            ["customers"] = w.Accounts.Count,
            ["services"] = w.Services.Count,
            ["servicesLive"] = w.Services.Values.Count(s => s.Live),
            ["events"] = stats.Total,
            ["eventsByType"] = new JsonObject(stats.ByType.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["platform"] = $"{Environment.OSVersion.Platform}/{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}/.NET {Environment.Version}",
            ["note"] = "История симулятора воспроизводится по seed и параметрам на той же платформе. truth.jsonl не загружается в анализатор.",
            ["files"] = new JsonObject(new[] { SnapshotFile, EventsFile, TruthFile, StateFile }.Select(f =>
            {
                var path = Path.Combine(dir, f);
                return KeyValuePair.Create(f, (JsonNode?)new JsonObject { ["bytes"] = new FileInfo(path).Length, ["sha256"] = Sha256(path) });
            })),
            ["config"] = JsonNode.Parse(config.ToJson()),
        };
        File.WriteAllText(Path.Combine(dir, ManifestFile), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = Iso.Compact.Encoder }) + "\n");
        return manifest;
    }

    /// <summary>Тестовые данные на T₀ в формате, который принимает PUT /api/v1/source/snapshot</summary>
    public static JsonObject Snapshot(Simulation sim, DateTimeOffset asOf)
    {
        var w = sim.W;
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["sourceAsOf"] = Iso.Format(asOf),
            ["sourceRevision"] = $"sim:{w.RunId}",
            ["synthetic"] = true,
            ["accounts"] = new JsonArray(w.Accounts.Values.OrderBy(a => a.Id).Select(a => (JsonNode)Records.Account(a)).ToArray()),
            ["services"] = new JsonArray(w.Services.Values.OrderBy(s => s.Id).Select(s => (JsonNode)Records.Service(s)).ToArray()),
            ["transactions"] = new JsonArray(sim.InitialTransactions.Select(t => (JsonNode)t.DeepClone()).ToArray()),
        };
    }

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }
}

public sealed class TeeSink(params IEventSink[] sinks) : IEventSink
{
    public void Write(SimEvent e)
    {
        foreach (var s in sinks) s.Write(e);
    }
}
