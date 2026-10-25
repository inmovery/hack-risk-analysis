using System.Globalization;
using System.Text.Json;

using Aeza.Risk.Core.Source;

namespace Aeza.Risk.Core;

/// <summary>Дневная активность клиента — проекция журнала доменных событий v2: аккаунт × день по времени события</summary>
public sealed class ActivityDelta
{
    public int Logins { get; set; }
    public int LoginsFailed { get; set; }
    /// <summary>Сколько «услуго-дней» телеметрии пришло (usage.daily)</summary>
    public int UsageSamples { get; set; }
    /// <summary>Сумма средней загрузки CPU по этим услуго-дням, %</summary>
    public double CpuSum { get; set; }
    public double TrafficOutGb { get; set; }
    public int IncidentMinutes { get; set; }
    public int TicketsOpened { get; set; }
    public int TicketsUnsolved { get; set; }
    public int? CsatMin { get; set; }
    public int? NpsMin { get; set; }
    public int PaymentsCount { get; set; }
    public long PaymentsMinor { get; set; }
    public int PaymentsFailed { get; set; }
    public int TopUpsCount { get; set; }
    public long TopUpsMinor { get; set; }
    public int Downgrades { get; set; }
    public int Upgrades { get; set; }
    public int AutoProlongOff { get; set; }
    public int Suspends { get; set; }
    public long WithdrawalsMinor { get; set; }
    public int Backups { get; set; }
    /// <summary>Продлений услуг (service.renewed)</summary>
    public int Renewals { get; set; }
    /// <summary>Удалений услуг (service.deleted)</summary>
    public int Deletions { get; set; }
    /// <summary>Сгоревших бонусов (bonus.expired) — конец промо</summary>
    public int BonusExpired { get; set; }
    /// <summary>Выгрузок резервных копий (service.backup_downloaded) — данные забирают</summary>
    public int BackupDownloads { get; set; }
    /// <summary>Переводов DNS к другому провайдеру (dns.records_changed, target = external)</summary>
    public int DnsExternal { get; set; }
    /// <summary>Отправленных удерживающих предложений</summary>
    public int RetentionOffers { get; set; }
    /// <summary>Попаданий в контрольную группу удержания</summary>
    public int RetentionHoldouts { get; set; }
    /// <summary>Доменных событий аккаунта за день (любых типов, включая неизвестные)</summary>
    public int Events { get; set; }

    public void Add(ActivityDelta o)
    {
        Logins += o.Logins;
        LoginsFailed += o.LoginsFailed;
        UsageSamples += o.UsageSamples;
        CpuSum += o.CpuSum;
        TrafficOutGb += o.TrafficOutGb;
        IncidentMinutes += o.IncidentMinutes;
        TicketsOpened += o.TicketsOpened;
        TicketsUnsolved += o.TicketsUnsolved;
        CsatMin = Min(CsatMin, o.CsatMin);
        NpsMin = Min(NpsMin, o.NpsMin);
        PaymentsCount += o.PaymentsCount;
        PaymentsMinor = checked(PaymentsMinor + o.PaymentsMinor);
        PaymentsFailed += o.PaymentsFailed;
        TopUpsCount += o.TopUpsCount;
        TopUpsMinor = checked(TopUpsMinor + o.TopUpsMinor);
        Downgrades += o.Downgrades;
        Upgrades += o.Upgrades;
        AutoProlongOff += o.AutoProlongOff;
        Suspends += o.Suspends;
        WithdrawalsMinor = checked(WithdrawalsMinor + o.WithdrawalsMinor);
        Backups += o.Backups;
        Renewals += o.Renewals;
        Deletions += o.Deletions;
        BonusExpired += o.BonusExpired;
        BackupDownloads += o.BackupDownloads;
        DnsExternal += o.DnsExternal;
        RetentionOffers += o.RetentionOffers;
        RetentionHoldouts += o.RetentionHoldouts;
        Events += o.Events;
    }

    private static int? Min(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}

/// <summary>Активность одного дня (из проекции)</summary>
public sealed record ActivityDay(DateOnly Day, ActivityDelta Totals);

/// <summary>Свёртка доменного события в дневную активность</summary>
public static class ActivityProjection
{
    /// <summary>Вклад события в активность аккаунта: (аккаунт, день, дельта) или null для глобальных событий</summary>
    public static (long AccountId, DateOnly Day, ActivityDelta Delta)? Fold(DomainEventInput e)
    {
        if (e.AccountId is not { } account) return null;
        var day = DateOnly.FromDateTime(e.OccurredAt.UtcDateTime);
        var d = new ActivityDelta { Events = 1 };
        using var doc = JsonDocument.Parse(e.DataJson);
        var data = doc.RootElement;
        switch (e.Type)
        {
            case "account.login":
                if (Bool(data, "success") == false) d.LoginsFailed = 1;
                else d.Logins = 1;
                break;
            case "usage.daily":
                if (data.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String
                    && DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var usageDay))
                    day = usageDay;
                if (Num(data, "cpuAvgPct") is { } cpu)
                {
                    d.UsageSamples = 1;
                    d.CpuSum = Math.Clamp(cpu, 0, 100);
                }
                d.TrafficOutGb = Math.Max(0, Num(data, "trafficOutGb") ?? 0);
                break;
            case "service.incident":
                d.IncidentMinutes = (int)Math.Clamp(Num(data, "durationMin") ?? 0, 0, 1440);
                break;
            case "ticket.opened":
                d.TicketsOpened = 1;
                break;
            case "ticket.closed":
                if (Str(data, "resolution") == "unsolved") d.TicketsUnsolved = 1;
                if (Num(data, "csat") is { } csat) d.CsatMin = (int)Math.Clamp(csat, 1, 5);
                break;
            case "nps.responded":
                if (Num(data, "score") is { } nps) d.NpsMin = (int)Math.Clamp(nps, 0, 10);
                break;
            case "payment.charged":
                d.PaymentsCount = 1;
                d.PaymentsMinor = Math.Max(0, Long(data, "amount") ?? 0);
                break;
            case "payment.failed":
                d.PaymentsFailed = 1;
                break;
            case "balance.topped_up":
                d.TopUpsCount = 1;
                d.TopUpsMinor = Math.Max(0, Long(data, "amount") ?? 0);
                break;
            case "service.plan_changed":
                if (Str(data, "direction") == "downgrade") d.Downgrades = 1;
                else if (Str(data, "direction") == "upgrade") d.Upgrades = 1;
                break;
            case "service.autoprolong_changed":
                if (Bool(data, "enabled") == false) d.AutoProlongOff = 1;
                break;
            case "service.suspended":
                d.Suspends = 1;
                break;
            case "withdrawal.requested":
                d.WithdrawalsMinor = Math.Max(0, Long(data, "amount") ?? 0);
                break;
            case "service.backup_created":
                d.Backups = 1;
                break;
            case "service.renewed":
                d.Renewals = 1;
                break;
            case "service.deleted":
                d.Deletions = 1;
                break;
            case "bonus.expired":
                d.BonusExpired = 1;
                break;
            case "service.backup_downloaded":
                d.BackupDownloads = 1;
                break;
            case "dns.records_changed":
                if (Str(data, "target") == "external") d.DnsExternal = 1;
                break;
            case "retention.offer_sent":
                d.RetentionOffers = 1;
                break;
            case "retention.holdout":
                d.RetentionHoldouts = 1;
                break;
        }
        return (account, day, d);
    }

    private static double? Num(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) && double.IsFinite(x) ? x : null;

    private static long? Long(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x) ? x : null;

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}

/// <summary>Поведение клиента на момент оценки</summary>
/// <param name="KnownSince">Первый день, за который есть доменные события аккаунта</param>
/// <param name="LastLoginDay">Последний день с успешным входом (за всё время данных)</param>
/// <param name="Days">Дни активности в окне признаков (до момента оценки включительно)</param>
public sealed record AccountBehavior(DateOnly KnownSince, DateOnly? LastLoginDay, IReadOnlyList<ActivityDay> Days)
{
    /// <summary>Сколько дней активности нужно загрузить для признаков (правила — до 60 дней, ML — до 90)</summary>
    public const int WindowDays = 90;

    /// <summary>Сумма активности за дни [from, to] включительно</summary>
    public ActivityDelta Sum(DateOnly from, DateOnly to)
    {
        var total = new ActivityDelta();
        foreach (var d in Days)
            if (d.Day >= from && d.Day <= to) total.Add(d.Totals);
        return total;
    }
}
