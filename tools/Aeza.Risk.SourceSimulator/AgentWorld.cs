using System.Globalization;
using System.Text.Json.Nodes;

namespace Aeza.Risk.SourceSimulator;

/// <summary>Snapshot системы, заданный анализатором. Строится только из этих параметров и момента старта</summary>
public sealed record WorldSpec(long Revision, DateTimeOffset StartedAt, string Source, int Seed, int ExtraCustomers, int Customers, int HistoryDays)
{
    public const string FromOrganizer = "organizer";
    public const string FromGenerator = "generated";

    public bool Generated => Source == FromGenerator;

    public ulong SeedValue => unchecked((ulong)(long)Seed);

    /// <summary>Идентичность snapshot системы: одинаковые параметры дают одинаковые eventId</summary>
    public string RunId(string? organizerHash) => Simulation.RunIdOf(Generated
        ? $"agent|{FromGenerator}|{Seed}|{Customers}|{HistoryDays}|{Iso.Format(Iso.Sec(StartedAt))}"
        : $"agent|{FromOrganizer}|{Seed}|{ExtraCustomers}|{Iso.Format(Iso.Sec(StartedAt))}|{organizerHash}");
}

/// <summary>Построение snapshot системы агента и его начального состояния</summary>
public static class AgentWorld
{
    /// <summary>Snapshot системы на начальный момент и записи этого момента строками v1 (аккаунты, услуги, транзакции)</summary>
    public static (Simulation Sim, List<string> Bootstrap) Build(WorldSpec spec, SimConfig config, JsonObject? organizer, string? organizerHash)
    {
        var startedAt = Iso.Sec(spec.StartedAt);
        Simulation sim;
        JsonObject records;
        if (spec.Generated)
        {
            config.Customers = Math.Max(1, spec.Customers);
            config.Days = Math.Max(1, spec.HistoryDays);
            var t0 = startedAt.AddDays(-Math.Max(0, spec.HistoryDays));
            sim = Simulation.CreateHistory(config, spec.SeedValue, t0);
            records = Dataset.Snapshot(sim, t0);
        }
        else
        {
            if (organizer is null) throw new InvalidOperationException("Нет тестовых данных организаторов (SIM_INIT)");
            sim = Simulation.FromSourceData(organizer, config, spec.SeedValue, startedAt, organizerHash ?? "");
            sim.AddExistingCustomers(Math.Max(0, spec.ExtraCustomers), startedAt);
            records = WithExtra(organizer, sim);
        }
        sim.W.RunId = spec.RunId(organizerHash);
        sim.TruthDaily = false;
        return (sim, Bootstrap(sim.W.RunId, records));
    }

    /// <summary>Записи организаторов как есть плюс добавленные клиенты с их транзакциями</summary>
    private static JsonObject WithExtra(JsonObject organizer, Simulation sim)
    {
        var accounts = Array(organizer, "accounts");
        var services = Array(organizer, "services");
        var known = accounts.Select(Id).OfType<long>().ToHashSet();
        var knownServices = services.Select(Id).OfType<long>().ToHashSet();
        return new JsonObject
        {
            ["accounts"] = new JsonArray([
                .. accounts.Select(a => a.DeepClone()),
                .. sim.W.Accounts.Values.Where(a => !known.Contains(a.Id)).OrderBy(a => a.Id).Select(a => (JsonNode)Records.Account(a))]),
            ["services"] = new JsonArray([
                .. services.Select(s => s.DeepClone()),
                .. sim.W.Services.Values.Where(s => !knownServices.Contains(s.Id)).OrderBy(s => s.Id).Select(s => (JsonNode)Records.Service(s))]),
            ["transactions"] = new JsonArray([
                .. Array(organizer, "transactions").Select(t => t.DeepClone()),
                .. sim.InitialTransactions.Select(t => t.DeepClone())]),
        };
    }

    /// <summary>Строки v1: eventId «{runId}-boot-{a|s|t}-{id}»</summary>
    public static List<string> Bootstrap(string runId, JsonObject records)
    {
        var lines = new List<string>();
        foreach (var (field, type, letter) in new[] { ("accounts", "account", "a"), ("services", "service", "s"), ("transactions", "transaction", "t") })
        {
            var n = 0;
            foreach (var record in Array(records, field))
            {
                n++;
                var id = Id(record) is { } x ? x.ToString(CultureInfo.InvariantCulture) : $"n{n}";
                lines.Add(new JsonObject
                {
                    ["schemaVersion"] = 1, ["eventId"] = $"{runId}-boot-{letter}-{id}", ["type"] = type, ["data"] = record.DeepClone(),
                }.ToJsonString(Iso.Compact));
            }
        }
        return lines;
    }

    private static List<JsonObject> Array(JsonObject o, string field) => (o[field] as JsonArray ?? []).OfType<JsonObject>().ToList();

    private static long? Id(JsonObject o) => o["id"] is JsonValue v && v.TryGetValue<long>(out var id) ? id : null;
}
