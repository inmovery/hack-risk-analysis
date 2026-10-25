namespace Aeza.Risk.Core;

/// <summary>Одна условная будущая оплата услуги</summary>
public readonly record struct Renewal(long ServiceId, int Ordinal, DateTimeOffset Due, long Amount);

/// <summary>План продлений услуги на момент оценки</summary>
public sealed record ServicePlan(
    long ServiceId,
    DateTimeOffset? NextDue,
    DateTimeOffset? Anchor,
    PaymentTerm Term,
    long? Price,
    bool Schedulable,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> MissingInputs)
{
    /// <summary>Состояние источника противоречит регламенту (prolong_wait дольше 5 дней или срок прошёл, а статус не обновлён)</summary>
    public bool PendingOnly => Flags.Contains(QualityFlags.ProlongWaitBeyondGrace) || Flags.Contains(QualityFlags.ExpiryPassedStatusNotUpdated);
}

public static class RenewalSchedule
{
    public static readonly TimeSpan ProlongWaitGrace = TimeSpan.FromDays(5);

    /// <summary>Срок k-го периода от якоря</summary>
    public static DateTimeOffset DueAt(DateTimeOffset anchor, PaymentTerm term, int k) => term switch
    {
        PaymentTerm.Hour => anchor.AddHours(k),
        PaymentTerm.Week => anchor.AddDays(7L * k),
        PaymentTerm.Month => anchor.AddMonths(k),
        PaymentTerm.QuarterYear => anchor.AddMonths(3 * k),
        PaymentTerm.HalfYear => anchor.AddMonths(6 * k),
        PaymentTerm.Year => anchor.AddMonths(12 * k),
        _ => throw new ArgumentOutOfRangeException(nameof(term), term, "Неизвестный период нельзя развернуть в расписание."),
    };

    /// <summary>Строит план продлений продлеваемой услуги</summary>
    public static ServicePlan Plan(ServiceInput service, ServiceStatus status, DateTimeOffset asOf)
    {
        var flags = new List<string>();
        var missing = new List<string>();
        var term = SourceEnums.ParseTerm(service.PaymentTerm);
        if (term == PaymentTerm.Unknown) { missing.Add("paymentTerm"); flags.Add(QualityFlags.UnknownPaymentTerm); }
        if (service.Price is null) { missing.Add("price"); flags.Add(QualityFlags.MissingPrice); }

        DateTimeOffset? nextDue;
        DateTimeOffset? anchor;
        if (status == ServiceStatus.ProlongWait)
        {
            // Ожидающая оплата нужна сейчас; следующий период — от момента оплаты (допущение).
            nextDue = asOf;
            anchor = asOf;
            if (service.ExpiresAt is { } exp && exp + ProlongWaitGrace < asOf) flags.Add(QualityFlags.ProlongWaitBeyondGrace);
        }
        else if (service.ExpiresAt is { } expires)
        {
            if (expires > asOf)
            {
                nextDue = expires;
                anchor = expires;
            }
            else
            {
                // Срок прошёл, а источник ещё не перевёл услугу в prolong_wait: одна оплата «сейчас».
                nextDue = asOf;
                anchor = asOf;
                flags.Add(QualityFlags.ExpiryPassedStatusNotUpdated);
            }
        }
        else
        {
            nextDue = null;
            anchor = null;
            missing.Add("expiresAt");
            flags.Add(QualityFlags.MissingExpiresAt);
        }

        var schedulable = term != PaymentTerm.Unknown && service.Price is not null && anchor is not null;
        return new ServicePlan(service.Id, nextDue, anchor, term, service.Price, schedulable, flags, missing);
    }

    /// <summary>Продления в интервале [asOf, horizonEnd)</summary>
    public static IEnumerable<Renewal> Renewals(ServicePlan plan, DateTimeOffset horizonEnd)
    {
        if (!plan.Schedulable) yield break;
        var anchor = plan.Anchor!.Value;
        for (var k = 0; ; k++)
        {
            var due = DueAt(anchor, plan.Term, k);
            if (due >= horizonEnd) yield break;
            yield return new Renewal(plan.ServiceId, k, due, plan.Price!.Value);
            if (plan.PendingOnly) yield break;
        }
    }
}
