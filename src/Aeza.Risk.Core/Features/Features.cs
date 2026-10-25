namespace Aeza.Risk.Core;

/// <summary>Услуга в prolong_wait: срок истёк, источник ждёт оплату</summary>
public sealed class WaitingPaymentFeature : IRiskFeature
{
    public string Key => "waiting_payment";
    public string Title => "Ожидает оплаты (prolong_wait)";
    public string Description => "1, если услуга в статусе prolong_wait";
    public decimal DefaultWeight => 40m;

    public FeatureValue Evaluate(ServiceContext ctx)
    {
        if (ctx.Status != ServiceStatus.ProlongWait) return FeatureValue.Of(0m, "Услуга не ожидает оплаты");
        var ev = new Dictionary<string, object?> { ["status"] = "prolong_wait", ["expiresAt"] = ctx.Service.ExpiresAt };
        var text = $"{ctx.Service.Name}: ожидает оплаты";
        if (ctx.Service.ExpiresAt is { } exp)
        {
            var graceEnd = exp + RenewalSchedule.ProlongWaitGrace;
            ev["graceEndsAt"] = graceEnd;
            text += graceEnd < ctx.AsOf
                ? $" с {Fmt.Date(exp)}; номинальные 5 дней ожидания закончились {Fmt.Relative(graceEnd, ctx.AsOf)}"
                : $" с {Fmt.Date(exp)}; до удаления по регламенту {Fmt.Relative(graceEnd, ctx.AsOf).Replace("через ", "")}";
        }
        return FeatureValue.Of(1m, text, ev);
    }
}

/// <summary>Средств (основной баланс + разрешённые бонусы после общего waterfall) не хватит на продление в горизонте</summary>
public sealed class FundsShortfallFeature : IRiskFeature
{
    public string Key => "funds_shortfall";
    public string Title => "Не хватит средств на продление";
    public string Description => "срочность первого продления услуги, не покрытого балансом и разрешёнными бонусами (общий баланс клиента тратится один раз)";
    public decimal DefaultWeight => 35m;

    public FeatureValue Evaluate(ServiceContext ctx)
    {
        if (!ctx.AccountFundingKnown) return FeatureValue.Missing("Неизвестен баланс клиента", "account.balance");
        if (!ctx.Plan.Schedulable || ctx.Funding is null)
            return FeatureValue.Missing("Нельзя построить расписание продлений услуги", ctx.Plan.MissingInputs.ToArray());
        var f = ctx.Funding;
        if (f.FirstUnfunded is not { } r)
        {
            var text = f.Renewals == 0
                ? $"{ctx.Service.Name}: продлений в горизонте нет"
                : $"{ctx.Service.Name}: все {f.Renewals} продл. в горизонте покрыты балансом";
            return FeatureValue.Of(0m, text, new Dictionary<string, object?> { ["renewals"] = f.Renewals, ["plannedMinor"] = f.PlannedMinor });
        }
        var u = ctx.Urgency(r.Due);
        var ev = new Dictionary<string, object?>
        {
            ["due"] = r.Due, ["amountMinor"] = r.Amount, ["coveredMinor"] = f.FirstUnfundedCoveredMinor,
            ["shortfallMinor"] = r.Amount - f.FirstUnfundedCoveredMinor, ["fundsMinor"] = ctx.AccountFunding.FundsMinor, ["urgency"] = u,
        };
        return FeatureValue.Of(u,
            $"{ctx.Service.Name}: не хватит средств на оплату {Fmt.Date(r.Due)} ({Fmt.Relative(r.Due, ctx.AsOf)}): нужно {Fmt.Eur(r.Amount)}, покрыто {Fmt.Eur(f.FirstUnfundedCoveredMinor)}",
            ev);
    }
}

/// <summary>Автопродление выключено, а срок близко: без действия клиента услуга не продлится</summary>
public sealed class NoAutoProlongFeature : IRiskFeature
{
    public string Key => "no_autoprolong";
    public string Title => "Автопродление выключено при близком сроке";
    public string Description => "срочность ближайшей оплаты × [autoProlong = false]";
    public decimal DefaultWeight => 35m;

    public FeatureValue Evaluate(ServiceContext ctx)
    {
        if (ctx.Service.AutoProlong is null) return FeatureValue.Missing("Неизвестно, включено ли автопродление", "autoProlong");
        if (ctx.Plan.NextDue is not { } due) return FeatureValue.Missing("Неизвестен срок услуги", "expiresAt");
        if (ctx.Service.AutoProlong.Value) return FeatureValue.Of(0m, $"{ctx.Service.Name}: автопродление включено");
        var u = ctx.Urgency(due);
        return FeatureValue.Of(u,
            $"{ctx.Service.Name}: автопродление выключено, оплата {(due <= ctx.AsOf ? "нужна сейчас" : Fmt.Relative(due, ctx.AsOf))}",
            new Dictionary<string, object?> { ["autoProlong"] = false, ["nextDue"] = due, ["urgency"] = u });
    }
}

/// <summary>Сервер выключен клиентом, но тарифицируется — частый предвестник отказа</summary>
public sealed class SuspendedBillingFeature : IRiskFeature
{
    public string Key => "suspended_billing";
    public string Title => "Сервер выключен, но тарифицируется";
    public string Description => "1, если услуга в статусе suspended (выключена клиентом, оплата продолжается)";
    public decimal DefaultWeight => 20m;

    public FeatureValue Evaluate(ServiceContext ctx)
    {
        if (ctx.Status != ServiceStatus.Suspended) return FeatureValue.Of(0m, "Услуга не выключена");
        var price = ctx.Service.Price is { } p ? $" ({Fmt.Eur(p)} за {Fmt.Term(ctx.Plan.Term)})" : "";
        return FeatureValue.Of(1m, $"{ctx.Service.Name}: выключена клиентом, но продолжает тарифицироваться{price}",
            new Dictionary<string, object?> { ["status"] = "suspended", ["priceMinor"] = ctx.Service.Price });
    }
}

/// <summary>Последняя транзакция услуги не проведена (not_performed): заказ/продление создан, но не оплачен</summary>
public sealed class UnpaidTransactionFeature : IRiskFeature
{
    public string Key => "unpaid_transaction";
    public string Title => "Последняя транзакция не проведена";
    public string Description => "1, если последняя транзакция услуги в статусе not_performed (создана, но не оплачена — не «отказ»)";
    public decimal DefaultWeight => 10m;

    public FeatureValue Evaluate(ServiceContext ctx)
    {
        var t = ctx.LastTransaction;
        if (t is null) return FeatureValue.Of(0m, $"{ctx.Service.Name}: транзакций нет");
        if (SourceEnums.ParseTransactionStatus(t.Status) != TransactionStatus.NotPerformed)
            return FeatureValue.Of(0m, $"{ctx.Service.Name}: последняя транзакция проведена");
        var kind = SourceEnums.ParseTransactionType(t.Type) switch { TransactionType.Prolong => "продление", TransactionType.Order => "заказ", _ => "транзакция" };
        var created = t.CreatedAt is { } c ? $" от {Fmt.Date(c)}" : "";
        return FeatureValue.Of(1m,
            $"{ctx.Service.Name}: последняя транзакция ({kind}{(t.Amount is { } a ? ", " + Fmt.Eur(a) : "")}{created}) не проведена",
            new Dictionary<string, object?> { ["transactionId"] = t.Id, ["type"] = t.Type, ["status"] = t.Status, ["amountMinor"] = t.Amount, ["createdAt"] = t.CreatedAt });
    }
}

/// <summary>Реестр признаков. Порядок задаёт порядок в UI и детерминированный tie-break причин</summary>
public static class FeatureCatalog
{
    public static readonly IReadOnlyList<IRiskFeature> All = new IRiskFeature[]
    {
        new WaitingPaymentFeature(),
        new FundsShortfallFeature(),
        new NoAutoProlongFeature(),
        new SuspendedBillingFeature(),
        new UnpaidTransactionFeature(),
        // Поведение клиента по событиям v2: вес по умолчанию 0
        new UsageDropFeature(),
        new InactiveLoginsFeature(),
        new PaymentFailuresFeature(),
        new SupportIssuesFeature(),
        new IncidentsFeature(),
        new DowngradeFeature(),
    };
}
