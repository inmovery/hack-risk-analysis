using System.Text.Json;

using Aeza.Risk.Core;
using Aeza.Risk.Core.Source;

namespace Aeza.Risk.Ml;

/// <summary>Еженедельный срез клиента и его исходы. Метки: 1 — да, 0 — нет, −1 — исход ещё неизвестен (цензура)</summary>
/// <param name="AccountId">Клиент</param>
/// <param name="At">Момент среза (понедельник 00:00 UTC)</param>
/// <param name="LiveServices">Действующих услуг (active, suspended, prolong_wait)</param>
/// <param name="RevenueMonthMinor">Выручка действующих услуг в пересчёте на месяц, еврокопейки</param>
/// <param name="Churn30">Ушёл целиком в следующие 30 дней (и не вернулся за 30 дней после ухода)</param>
/// <param name="Churn90">То же за 90 дней</param>
/// <param name="Contraction30">Через 30 дней остался клиентом, но выручка ниже на 30 % и больше</param>
/// <param name="WeeksToChurn">Недель до ухода или до конца наблюдения</param>
/// <param name="ChurnObserved">Уход наблюдался (false — время до ухода цензурировано)</param>
/// <param name="ExitReason">Ответ в опросе при уходе — метка причины, не признак</param>
/// <param name="RulesScore">Балл правил клиента на тот же момент — базовая линия</param>
/// <param name="Features">Признаки клиента на момент среза (<see cref="MlCustomerFeatures"/>)</param>
public sealed record CustomerWeekRow(
    long AccountId, DateTimeOffset At, int LiveServices, long RevenueMonthMinor, int Churn30, int Churn90, int Contraction30, double WeeksToChurn,
    bool ChurnObserved, string? ExitReason, decimal? RulesScore = null, float[]? Features = null);

/// <summary>Хронология клиентов по мере переигрывания журнала: срезы по неделям и точки изменения действующих услуг</summary>
/// <param name="start">Начало данных: первый срез — ближайший понедельник</param>
/// <param name="snapshot">Признаки и балл правил клиента на момент среза (состояние журнала на этот момент)</param>
public sealed class CustomerTimeline(DateTimeOffset start, Func<long, DateTimeOffset, (float[] Features, decimal? Rules)?>? snapshot = null)
{
    /// <summary>Клиент, потерявший все услуги, считается ушедшим, если не вернулся за столько дней</summary>
    public const int ReturnDays = 30;

    /// <summary>Сокращение выручки — падение не меньше этой доли</summary>
    public const double ContractionShare = 0.3;

    private readonly Dictionary<long, List<(DateTimeOffset At, int Live, long Revenue)>> _changes = new();
    private readonly List<(long Account, DateTimeOffset At, int Live, long Revenue, float[]? Features, decimal? Rules)> _weeks = [];
    private readonly Dictionary<long, List<(DateTimeOffset At, string Reason)>> _surveys = new();
    private DateTimeOffset _nextWeek = NextMonday(start);

    private static DateTimeOffset NextMonday(DateTimeOffset t)
    {
        var day = new DateTimeOffset(t.UtcDateTime.Date, TimeSpan.Zero);
        var shift = ((int)DayOfWeek.Monday - (int)day.DayOfWeek + 7) % 7;
        return day.AddDays(shift == 0 && day < t ? 7 : shift);
    }

    /// <summary>Срезы всех недель до момента <paramref name="time"/> (вызывается перед применением события)</summary>
    public void SnapshotUntil(DateTimeOffset time)
    {
        while (_nextWeek < time)
        {
            foreach (var (account, list) in _changes)
                if (list[^1] is { Live: > 0 } last && last.At <= _nextWeek)
                {
                    var s = snapshot?.Invoke(account, _nextWeek);
                    _weeks.Add((account, _nextWeek, last.Live, last.Revenue, s?.Features, s?.Rules));
                }
            _nextWeek = _nextWeek.AddDays(7);
        }
    }

    /// <summary>Действующие услуги клиента после изменения</summary>
    public void Observe(long account, DateTimeOffset at, IEnumerable<ServiceInput> services)
    {
        var live = services.Where(s => SourceEnums.ParseStatus(s.Status) is ServiceStatus.Active or ServiceStatus.Suspended or ServiceStatus.ProlongWait).ToList();
        var revenue = (long)Math.Round(live.Sum(s => MlFeatures.MonthlyEquivalent(Math.Max(0, s.Price ?? 0), SourceEnums.ParseTerm(s.PaymentTerm))));
        if (!_changes.TryGetValue(account, out var list)) _changes[account] = list = [];
        if (list.Count > 0 && list[^1].Live == live.Count && list[^1].Revenue == revenue) return;
        list.Add((at, live.Count, revenue));
    }

    /// <summary>Доменное событие: из него нужен только ответ опроса при уходе</summary>
    public void Domain(DomainEventInput e)
    {
        if (e.Type != "exit.survey_answered" || e.AccountId is not { } account) return;
        using var doc = JsonDocument.Parse(e.DataJson);
        if (!doc.RootElement.TryGetProperty("reason", out var r) || r.GetString() is not { } reason) return;
        if (!_surveys.TryGetValue(account, out var list)) _surveys[account] = list = [];
        list.Add((e.OccurredAt, reason));
    }

    /// <summary>Метки срезов по наблюдениям до <paramref name="dataEnd"/></summary>
    public List<CustomerWeekRow> Label(DateTimeOffset dataEnd)
    {
        // Уходы клиента: момент потери всех услуг без возврата за ReturnDays; null у неподтверждённых (наблюдение кончилось раньше)
        var churns = _changes.ToDictionary(kv => kv.Key, kv => Churns(kv.Value, dataEnd));
        var rows = new List<CustomerWeekRow>(_weeks.Count);
        foreach (var (account, at, live, revenue, features, rules) in _weeks)
        {
            var churn = churns[account].Where(c => c > at).Cast<DateTimeOffset?>().FirstOrDefault();
            int Label(int days) =>
                churn is { } c && c <= at.AddDays(days) ? 1
                : at.AddDays(days + ReturnDays) <= dataEnd ? 0
                : -1;
            var contraction = -1;
            if (at.AddDays(30) <= dataEnd)
            {
                var later = _changes[account].Where(x => x.At <= at.AddDays(30)).Select(x => x.Revenue).Last();
                contraction = later > 0 && later <= revenue * (1 - ContractionShare) ? 1 : 0;
            }
            var observedUntil = dataEnd.AddDays(-ReturnDays);
            var weeks = churn is { } cc ? (cc - at).TotalDays / 7 : Math.Max(0, (observedUntil - at).TotalDays / 7);
            var reason = churn is { } ch && _surveys.TryGetValue(account, out var s)
                ? s.Where(x => x.At >= ch.AddDays(-1) && x.At <= ch.AddDays(14)).Select(x => x.Reason).FirstOrDefault()
                : null;
            rows.Add(new CustomerWeekRow(account, at, live, revenue, Label(30), Label(90), contraction, Math.Round(weeks, 2), churn is not null, reason, rules,
                features));
        }
        return rows;
    }

    private static List<DateTimeOffset> Churns(List<(DateTimeOffset At, int Live, long Revenue)> changes, DateTimeOffset dataEnd)
    {
        var result = new List<DateTimeOffset>();
        for (var i = 1; i < changes.Count; i++)
        {
            if (changes[i].Live != 0 || changes[i - 1].Live == 0) continue;
            var lost = changes[i].At;
            var back = changes.Skip(i + 1).FirstOrDefault(x => x.Live > 0);
            var returned = back.Live > 0 && back.At <= lost.AddDays(ReturnDays);
            if (!returned && lost.AddDays(ReturnDays) <= dataEnd) result.Add(lost);
        }
        return result;
    }
}
