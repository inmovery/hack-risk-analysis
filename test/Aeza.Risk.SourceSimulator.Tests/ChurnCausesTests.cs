using System.Text.Json;

using Xunit;

namespace Aeza.Risk.SourceSimulator.Tests;

/// <summary>Отток возникает из причин: в истории есть уходы по каждой причине, у предупреждённых уходов заложенная причина видна в событиях раньше ухода, ~25% уходов — без предупреждения</summary>
public sealed class ChurnCausesTests
{
    private sealed record IntentInfo(DateTimeOffset StartedAt, string Cause, bool Silent);

    [Fact]
    public void EveryCause_LeadsToChurn_WithItsSignalsBeforehand()
    {
        var dir = GenerationTests.TempDir();
        Dataset.Generate(GenerationTests.Small(600, 240), 2026, GenerationTests.End, dir);

        var intents = new Dictionary<(long, int), IntentInfo>();
        var lost = new List<(long Account, DateTimeOffset At, string Cause, int? No)>();
        int started = 0, silent = 0;
        foreach (var line in File.ReadLines(Path.Combine(dir, Dataset.TruthFile)))
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            switch (r.GetProperty("k").GetString())
            {
                case "intent_started":
                    started++;
                    if (r.GetProperty("silent").GetBoolean()) silent++;
                    intents[(r.GetProperty("a").GetInt64(), r.GetProperty("no").GetInt32())] =
                        new IntentInfo(Iso.Parse(r.GetProperty("at").GetString()!), r.GetProperty("cause").GetString()!, r.GetProperty("silent").GetBoolean());
                    break;
                case "lost_all_services":
                    lost.Add((r.GetProperty("a").GetInt64(), Iso.Parse(r.GetProperty("at").GetString()!), r.GetProperty("cause").GetString()!,
                        r.TryGetProperty("intentNo", out var no) && no.ValueKind == JsonValueKind.Number ? no.GetInt32() : null));
                    break;
            }
        }
        foreach (var cause in new[] { Causes.Dissatisfaction, Causes.Decay, Causes.Finance, Causes.Price, Causes.Neglect, Causes.Abuse })
            Assert.True(lost.Count(l => l.Cause == cause) >= 8, $"уходов по причине {cause}: {lost.Count(l => l.Cause == cause)}");
        Assert.InRange(silent / (double)started, 0.15, 0.35);

        // Сигналы по аккаунтам: нагрузка по дням, опыт (аварии, нерешённые обращения, низкий NPS), деньги, ценовые шоки
        var cpu = new Dictionary<long, Dictionary<DateTimeOffset, double>>();
        var experience = new Dictionary<long, List<DateTimeOffset>>();
        var money = new Dictionary<long, List<DateTimeOffset>>();
        var priceShocks = new Dictionary<long, List<DateTimeOffset>>();
        var globalPrice = new List<DateTimeOffset>();
        static void Add(Dictionary<long, List<DateTimeOffset>> d, long a, DateTimeOffset t)
        {
            if (!d.TryGetValue(a, out var l)) d[a] = l = [];
            l.Add(t);
        }
        foreach (var line in File.ReadLines(Path.Combine(dir, Dataset.EventsFile)))
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            var type = e.GetProperty("type").GetString();
            var at = Iso.Parse(e.GetProperty("occurredAt").GetString()!);
            var data = e.GetProperty("data");
            if (type == "tariff.price_changed")
            {
                globalPrice.Add(at);
                continue;
            }
            if (!e.TryGetProperty("accountId", out var accEl)) continue;
            var a = accEl.GetInt64();
            switch (type)
            {
                case "usage.daily":
                    if (!cpu.TryGetValue(a, out var days)) cpu[a] = days = [];
                    var day = Iso.Parse(data.GetProperty("date").GetString() + "T00:00:00Z");
                    days[day] = days.GetValueOrDefault(day) + data.GetProperty("cpuAvgPct").GetDouble();
                    break;
                case "service.incident":
                case "ticket.closed" when data.GetProperty("resolution").GetString() == "unsolved":
                case "nps.responded" when data.GetProperty("score").GetInt32() <= 6:
                    Add(experience, a, at);
                    break;
                case "payment.failed":
                case "service.plan_changed" when data.GetProperty("direction").GetString() == "downgrade":
                    Add(money, a, at);
                    break;
                case "bonus.expired":
                    Add(priceShocks, a, at);
                    break;
            }
        }

        double? MeanCpu(long a, DateTimeOffset from, DateTimeOffset to)
        {
            if (!cpu.TryGetValue(a, out var d)) return null;
            var v = d.Where(kv => kv.Key >= from && kv.Key < to).Select(kv => kv.Value).ToList();
            return v.Count == 0 ? null : v.Average();
        }
        bool Any(Dictionary<long, List<DateTimeOffset>> d, long a, DateTimeOffset from, DateTimeOffset to) =>
            d.TryGetValue(a, out var l) && l.Any(t => t >= from && t <= to);

        var warned = lost.Where(l => l.No is { } no && intents.TryGetValue((l.Account, no), out var i) && !i.Silent)
            .Select(l => (l.Account, l.At, Intent: intents[(l.Account, l.No!.Value)])).ToList();
        void Share(string cause, double atLeast, Func<(long Account, DateTimeOffset At, IntentInfo Intent), bool> signal)
        {
            var group = warned.Where(w => w.Intent.Cause == cause).ToList();
            Assert.True(group.Count >= 5, $"предупреждённых уходов {cause}: {group.Count}");
            var share = group.Count(signal) / (double)group.Count;
            Assert.True(share >= atLeast, $"{cause}: сигнал у {share:P0} предупреждённых уходов (нужно ≥ {atLeast:P0}, всего {group.Count})");
        }

        // Затухание проекта: нагрузка за 2 недели до ухода ниже 60% от уровня за 1–3 месяца до намерения
        Share(Causes.Decay, 0.6, w => MeanCpu(w.Account, w.Intent.StartedAt.AddDays(-90), w.Intent.StartedAt.AddDays(-30)) is { } before
                                             && MeanCpu(w.Account, w.At.AddDays(-14), w.At) is { } late && late < 0.6 * before);
        // Недовольство: авария, нерешённое обращение или низкий NPS за 45 дней до намерения
        Share(Causes.Dissatisfaction, 0.6, w => Any(experience, w.Account, w.Intent.StartedAt.AddDays(-45), w.Intent.StartedAt.AddDays(1)));
        // Финансы: неудачные платежи или даунгрейд между намерением и уходом
        Share(Causes.Finance, 0.5, w => Any(money, w.Account, w.Intent.StartedAt.AddDays(-30), w.At));
        // Цена: конец промо или рост цены за 60 дней до намерения
        Share(Causes.Price, 0.8, w => Any(priceShocks, w.Account, w.Intent.StartedAt.AddDays(-60), w.Intent.StartedAt)
                                             || globalPrice.Any(t => t >= w.Intent.StartedAt.AddDays(-60) && t <= w.Intent.StartedAt));
    }
}
