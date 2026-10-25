using System.Globalization;
using System.Text.Json.Nodes;

namespace Aeza.Risk.SourceSimulator;

public sealed partial class Simulation
{
    private static readonly string[] Severities = ["minor", "major", "critical"];
    private static readonly string[] Priorities = ["normal", "high", "urgent"];

    private void Execute(AgendaItem it)
    {
        switch (it.Kind)
        {
            case Kinds.Day: DayStart(); break;
            case Kinds.Register: Register(); break;
            case Kinds.Order: Order(it.AccountId); break;
            case Kinds.Due: Due(it); break;
            case Kinds.Retry: Retry(it.ServiceId); break;
            case Kinds.Grace: Grace(it); break;
            case Kinds.Pay: Pay(it.ServiceId); break;
            case Kinds.TopUp: TopUp(it.AccountId, (long)it.Num, it.Arg ?? "card"); break;
            case Kinds.Login: Login(it.AccountId); break;
            case Kinds.Usage: Usage(it.AccountId); break;
            case Kinds.Incident: Incident(it.Arg!, (int)it.Num); break;
            case Kinds.Price: PriceChange(it.Arg!, it.Num); break;
            case Kinds.TicketOpen: TicketOpen(it.AccountId, it.Arg!, (int)it.Num); break;
            case Kinds.TicketReply: TicketReply(it.ServiceId); break;
            case Kinds.TicketClose: TicketClose(it.ServiceId); break;
            case Kinds.TicketCheck: TicketCheck(it.ServiceId); break;
            case Kinds.Nps: Nps(it.AccountId); break;
            case Kinds.AutoProlong: AutoProlongChange(it.ServiceId, it.Arg == "on", (int)it.Num); break;
            case Kinds.Suspend: Suspend(it.ServiceId); break;
            case Kinds.Resume: Resume(it.ServiceId); break;
            case Kinds.Plan: PlanChange(it.ServiceId, it.Arg == "up", (int)it.Num); break;
            case Kinds.Term: TermChange(it.ServiceId, it.Arg!); break;
            case Kinds.Backup: ServiceBehavior(it.ServiceId, "service.backup_created", (int)it.Num); break;
            case Kinds.Reinstall: ServiceBehavior(it.ServiceId, "service.reinstalled", 0); break;
            case Kinds.Withdraw: Withdraw(it.AccountId, (int)it.Num); break;
            case Kinds.BonusExpire: BonusExpire(it.AccountId); break;
            case Kinds.BonusGrant: BonusGrant(it.AccountId, (long)it.Num, it.Arg ?? "promo"); break;
            case Kinds.Reminder: Reminder(it.ServiceId, (int)it.Num); break;
            case Kinds.NotificationOpen: NotificationOpen(it.AccountId, it.Arg!); break;
            case Kinds.Block: Block(it.AccountId); break;
            case Kinds.AdminDelete: AdminDelete(it.ServiceId); break;
            case Kinds.UserDelete: UserDelete(it.ServiceId, it.Arg ?? "user_request", (int)it.Num); break;
            case Kinds.Decide: Decide(it.AccountId, (int)it.Num); break;
            case Kinds.Offer: Offer(it.AccountId); break;
            case Kinds.OfferRedeem: OfferRedeem(it.AccountId, it.Arg!); break;
            case Kinds.Verify: Verify(it.AccountId, it.Arg ?? "email"); break;
            case Kinds.Security: Security(it.AccountId); break;
            case Kinds.Close: Close(it.AccountId); break;
            case Kinds.Scenario: ScenarioStep(it.AccountId, it.ServiceId, (int)it.Num); break;
            default: throw new InvalidOperationException($"Неизвестное действие {it.Kind}");
        }
    }

    private static string Ticks(DateTimeOffset d) => d.UtcTicks.ToString(CultureInfo.InvariantCulture);

    private DateTimeOffset Daytime(DateTimeOffset day, double meanHour = 14, double sdHour = 4) =>
        day.AddHours(Math.Clamp(_r.Normal(meanHour, sdHour), 0.02, 23.98));

    // ---------- Шаг дня ----------

    private void DayStart()
    {
        var day = Iso.DayStart(W.Clock);
        var dayIndex = (int)Math.Floor((day - Iso.DayStart(W.StartedAt)).TotalDays);
        Schedule(day.AddDays(1), Kinds.Day);

        // 1. Глобальные события дня: аварии ДЦ (пуассоновский поток по локациям), изменения цен по расписанию
        foreach (var loc in C.Locations)
            for (var k = _r.Poisson(C.IncidentsPerLocationPerDay); k > 0; k--)
                Schedule(day.AddSeconds(_r.Uniform(60, 86340)), Kinds.Incident, arg: loc, num: _r.Weighted([0.7, 0.25, 0.05]));
        foreach (var pc in C.PriceChanges.Where(p => p.Day == dayIndex && _plans.ContainsKey(p.Plan)))
            Schedule(day.AddHours(9), Kinds.Price, arg: pc.Plan, num: pc.Percent);
        if (day >= Iso.DayStart(W.HistoryEnd))
            for (var k = _r.Poisson(C.LiveRegistrationsPerDay); k > 0; k--) Schedule(Daytime(day), Kinds.Register);

        // 2–5. Клиенты: дрейф скрытого состояния, намерение уйти, видимое поведение, пополнения
        foreach (var ag in W.Agents.Values.OrderBy(a => a.AccountId).ToList())
            if (!ag.Gone) PlanAgentDay(ag, day);
    }

    private void PlanAgentDay(Agent ag, DateTimeOffset day)
    {
        var seg = _segments[ag.Segment];
        var acc = W.Accounts[ag.AccountId];
        var live = LiveServices(ag).ToList();
        Drift(ag, seg, day);
        if (TruthDaily)
            Truth("day", ag.AccountId, new JsonObject
            {
                ["d"] = Iso.Date(day), ["s"] = Math.Round(ag.S, 3), ["e"] = Math.Round(ag.E, 3), ["f"] = Math.Round(ag.F, 3),
                ["intent"] = ag.Intent is { } it ? (it.Decided ? "decided:" : "maturing:") + it.Cause : null,
                ["vacation"] = ag.VacationUntil > day,
            });

        IntentStep(ag, seg, day, live);
        var intent = ag.Intent;
        var signals = intent is { Silent: false };

        // Входы ~ Пуассон(λ · e): отпуск и созревающее намерение их снижают
        var vacation = ag.VacationUntil > day ? 0.15 : 1;
        var intentFactor = signals ? (intent!.Decided ? 0.05 : 1 - 0.8 * intent.Progress(day)) : 1;
        for (var n = _r.Poisson(ag.LoginRate * Math.Min(1, ag.E * 1.2) * vacation * intentFactor); n > 0; n--)
            Schedule(Daytime(day), Kinds.Login, ag.AccountId);

        if (live.Count > 0) Schedule(day.AddMinutes(5).AddSeconds(ag.AccountId % 600), Kinds.Usage, ag.AccountId);

        PlanMoney(ag, seg, acc, day, live);
        if (seg.Attentive) PlanBehavior(ag, seg, acc, day, live);
        else if (seg.Abuser && !ag.AbuseScheduled && live.Count > 0)
        {
            ag.AbuseScheduled = true;
            Schedule(day.AddDays(_r.Uniform(3, 40)), Kinds.Block, ag.AccountId);
        }
        if (intent is null && _r.Chance(seg.NewServicePerDay * Math.Max(0.2, ag.E))) Schedule(Daytime(day), Kinds.Order, ag.AccountId);

        // Напоминания о продлении за 3 дня (для сроков от месяца)
        foreach (var s in live.Where(s => s.Status is ServiceStatuses.Active or ServiceStatuses.Suspended && s.PaymentTerm is not ("hour" or "week")))
            if (s.ExpiresAt >= day.AddDays(3) && s.ExpiresAt < day.AddDays(4)) Schedule(day.AddHours(10), Kinds.Reminder, ag.AccountId, s.Id, num: 3);
    }

    private void Drift(Agent ag, SegmentConfig seg, DateTimeOffset day)
    {
        ag.S = Math.Clamp(ag.S + (ag.S0 - ag.S) * 0.04 + _r.Normal(0, 0.01), 0, 1);
        if (ag.DecayRate > 0) ag.E = Math.Max(0.01, ag.E * (1 - ag.DecayRate));
        else
        {
            ag.E0 = Math.Min(1, ag.E0 + seg.NeedTrend);
            ag.E = Math.Clamp(ag.E + (ag.E0 - ag.E) * 0.02 + seg.NeedTrend + _r.Normal(0, 0.01), 0.01, 1);
            // Проект затухает: потребность плавно падает за 2–3 месяца («затухание проекта»)
            if (seg.Attentive && _r.Chance(seg.DecayPerDay)) ag.DecayRate = _r.Uniform(0.02, 0.04);
        }
        ag.F = Math.Clamp(ag.F + (ag.F0 - ag.F) * 0.01 + _r.Normal(0, 0.005), 0, 1);
        if (_r.Chance(1.0 / 500)) ag.F = Math.Max(0, ag.F - _r.Uniform(0.3, 0.6)); // финансовый шок
        if (seg.Attentive && !(ag.VacationUntil > day) && _r.Chance(C.Noise.VacationsPerYear / 365))
            ag.VacationUntil = day.AddDays(_r.Uniform(7, 15)); // ложная тревога: входы и нагрузка падают и возвращаются
    }

    /// <summary>Намерение уйти: h = σ(β₀ + β₁(1−s) + β₂(1−e) + β₃(1−f) + β₄·промо + β₅·цена); причина — что сильнее всего отклонилось от нормы клиента</summary>
    private void IntentStep(Agent ag, SegmentConfig seg, DateTimeOffset day, List<ServiceRec> live)
    {
        var h = C.Hazard;
        if (ag.Intent is { Decided: false } open)
        {
            if (open.Cause is Causes.Dissatisfaction or Causes.Price && ag.S > h.CancelSatisfaction && _r.Chance(0.3)) CancelIntent(ag, "satisfaction_recovered");
            return;
        }
        if (ag.Intent is not null || !seg.Attentive || live.Count == 0 || day - ag.CreatedAt < TimeSpan.FromDays(7)) return;

        var promo = ag.PromoEndedAt is { } pe && day - pe < TimeSpan.FromDays(h.ShockDays) ? 1 : 0;
        var price = ag.PriceRiseAt is { } pr && day - pr < TimeSpan.FromDays(h.ShockDays) ? 1 : 0;
        var t4 = h.B4 * promo * ag.PriceSensitivity;
        var t5 = h.B5 * price * ag.PriceSensitivity;
        var x = h.B0 + h.B1 * (1 - ag.S) + h.B2 * (1 - ag.E) + h.B3 * (1 - ag.F) + t4 + t5;
        if (!_r.Chance(Sigmoid(x))) return;

        var weights = new[]
        {
            Math.Max(0, h.B1 * (ag.S0 - ag.S)) + 0.05, Math.Max(0, h.B2 * (ag.E0 - ag.E)) + 0.05, Math.Max(0, h.B3 * (ag.F0 - ag.F)) + 0.05, t4 + t5 + 0.01,
        };
        var cause = new[] { Causes.Dissatisfaction, Causes.Decay, Causes.Finance, Causes.Price }[_r.Weighted(weights)];
        var span = cause switch
        {
            Causes.Decay => _r.Uniform(14, 45),
            Causes.Finance => _r.Uniform(3, 20),
            _ => _r.Uniform(h.MatureDaysMin, h.MatureDaysMax),
        };
        var start = Daytime(day);
        var intent = new Intent
        {
            No = ++ag.IntentCount, Cause = cause, StartedAt = Iso.Sec(start), DueAt = Iso.Sec(start.AddDays(span)), Silent = _r.Chance(C.Noise.SilentChurnShare),
        };
        ag.Intent = intent;
        Truth("intent_started", ag.AccountId, new JsonObject
        {
            ["no"] = intent.No, ["cause"] = cause, ["dueAt"] = Iso.Format(intent.DueAt), ["silent"] = intent.Silent,
            ["s"] = Math.Round(ag.S, 3), ["e"] = Math.Round(ag.E, 3), ["f"] = Math.Round(ag.F, 3),
        });
        Schedule(intent.DueAt, Kinds.Decide, ag.AccountId, num: intent.No);
        if (intent.Silent) return;

        // Видимые сигналы с задержками: модель может предсказывать заранее
        var at = (double share) => intent.StartedAt.AddSeconds((intent.DueAt - intent.StartedAt).TotalSeconds * share);
        // При финансовой причине клиент обычно не выключает автопродление — у него кончаются деньги, и списания не проходят
        if (_r.Chance(cause == Causes.Finance ? 0.25 : 0.75))
            foreach (var s in live.Where(s => s.AutoProlong)) Schedule(at(_r.Uniform(0.3, 0.9)), Kinds.AutoProlong, ag.AccountId, s.Id, "off", intent.No);
        if (_r.Chance(0.5)) Schedule(intent.DueAt.AddDays(-_r.Uniform(1, 5)), Kinds.Backup, ag.AccountId, live[0].Id, num: intent.No);
        if (W.Accounts[ag.AccountId].WithdrawBalance > 0 && _r.Chance(0.7))
            Schedule(intent.DueAt.AddDays(-_r.Uniform(0, 3)), Kinds.Withdraw, ag.AccountId, num: intent.No);
        if (cause is Causes.Finance or Causes.Price && _r.Chance(0.7))
            Schedule(at(_r.Uniform(0.2, 0.6)), Kinds.Plan, ag.AccountId, live.OrderByDescending(s => s.Price).First().Id, "down", intent.No);
        if (_r.Chance(0.2)) Schedule(intent.DueAt.AddDays(-_r.Uniform(0, 2)), Kinds.TicketOpen, ag.AccountId, arg: "cancellation", num: 0);
    }

    private void CancelIntent(Agent ag, string reason)
    {
        if (ag.Intent is not { Decided: false } intent) return;
        Truth("intent_cancelled", ag.AccountId, new JsonObject { ["no"] = intent.No, ["cause"] = intent.Cause, ["reason"] = reason });
        ag.Intent = null;
        if (intent.TurnedOffAutoProlong)
            foreach (var s in LiveServices(ag).Where(s => !s.AutoProlong && _r.Chance(0.7)))
                Schedule(W.Clock.AddHours(_r.Uniform(1, 48)), Kinds.AutoProlong, ag.AccountId, s.Id, "on");
    }

    private bool IntentActive(Agent ag, int no) => no == 0 || ag.Intent is { } i && i.No == no;

    private void PlanMoney(Agent ag, SegmentConfig seg, AccountRec acc, DateTimeOffset day, List<ServiceRec> live)
    {
        var decided = ag.Intent is { Decided: true };
        if (decided) return; // решил уйти: больше не платит
        var horizon = day.AddDays(3);
        var due = live.Where(s => s.Status is ServiceStatuses.Active or ServiceStatuses.Suspended && s.AutoProlong && s.ExpiresAt < horizon).ToList();
        var waiting = live.Where(s => s.Status == ServiceStatuses.ProlongWait).ToList();
        var need = due.Sum(s => Cost(s.PaymentTerm, TermPrice(s.Plan, s.PaymentTerm, s.ExpiresAt))) + waiting.Sum(s => Cost(s.PaymentTerm, TermPrice(s.Plan, s.PaymentTerm, day)));
        var shortfall = need - acc.Funds;
        var finance = (0.3 + 0.7 * ag.F) * (ag.Intent?.Cause == Causes.Finance ? 0.2 : 1) * seg.TopUpAttention;
        if (shortfall > 0)
        {
            var monthly = MonthlyCost(ag, day);
            switch (ag.Habit)
            {
                case "auto" when _r.Chance(0.95 * finance):
                    Schedule(day.AddHours(_r.Uniform(0.5, 6)), Kinds.TopUp, ag.AccountId, arg: "card:auto", num: RoundEur(Math.Max(shortfall, monthly) * _r.Uniform(1, 2.5) + 100));
                    break;
                case "manual" when _r.Chance(0.65 * finance):
                    Schedule(Daytime(day), Kinds.TopUp, ag.AccountId, arg: _r.Weighted(new Dictionary<string, double> { ["card"] = 0.7, ["crypto"] = 0.2, ["bank"] = 0.1 }),
                        num: RoundEur(shortfall * _r.Uniform(1.2, 3) + 100));
                    break;
                case "last_moment":
                    var first = due.Count > 0 ? due.Min(s => s.ExpiresAt) : (DateTimeOffset?)null;
                    if (first is { } f && f < day.AddDays(1) && _r.Chance(0.75 * finance))
                        Schedule(Max(day.AddMinutes(1), f.AddHours(-_r.Uniform(0.3, 6))), Kinds.TopUp, ag.AccountId, arg: "card", num: RoundEur(shortfall * _r.Uniform(1, 1.5) + 100));
                    else if (waiting.Count > 0 && _r.Chance(0.6 * finance))
                        Schedule(Daytime(day), Kinds.TopUp, ag.AccountId, arg: "card", num: RoundEur(shortfall * _r.Uniform(1, 1.5) + 100));
                    break;
            }
        }
        // Оплата вручную услуг в ожидании, если деньги уже есть
        if (shortfall <= 0)
            foreach (var s in waiting.Where(_ => _r.Chance(0.75 * finance)))
                Schedule(Daytime(day), Kinds.Pay, ag.AccountId, s.Id);
    }

    private void PlanBehavior(Agent ag, SegmentConfig seg, AccountRec acc, DateTimeOffset day, List<ServiceRec> live)
    {
        var running = live.Where(s => s.Status == ServiceStatuses.Active).ToList();
        var intent = ag.Intent;
        if (seg.ShortLivedDeletePerDay > 0)
            foreach (var s in live.Where(s => s.ShortLived && s.CreatedAt < day.AddDays(-1) && s.Status != ServiceStatuses.Blocked))
                if (_r.Chance(seg.ShortLivedDeletePerDay)) Schedule(Daytime(day), Kinds.UserDelete, ag.AccountId, s.Id, "user_request");
        foreach (var s in running)
        {
            if (_r.Chance(1.0 / 150)) Schedule(Daytime(day), Kinds.Suspend, ag.AccountId, s.Id);
            if (s.AutoProlong && intent is null && _r.Chance(1.0 / 500))
            {
                // Ложная тревога: выключил автопродление и потом включил обратно
                var off = Daytime(day);
                Schedule(off, Kinds.AutoProlong, ag.AccountId, s.Id, "off");
                if (_r.Chance(0.7)) Schedule(off.AddDays(_r.Uniform(1, 20)), Kinds.AutoProlong, ag.AccountId, s.Id, "on");
            }
        }
        if (running.Count > 0)
        {
            if (seg.UpgradePerDay > 0 && _r.Chance(seg.UpgradePerDay * (ag.E > 0.8 ? 1.5 : 0.5))) Schedule(Daytime(day), Kinds.Plan, ag.AccountId, _r.Pick(running).Id, "up");
            if (ag.F < 0.35 && _r.Chance(1.0 / 30)) Schedule(Daytime(day), Kinds.Plan, ag.AccountId, running.OrderByDescending(s => s.Price).First().Id, "down");
            if (seg.Name == SegmentNames.Stable && _r.Chance(1.0 / 365) && running.FirstOrDefault(s => s.PaymentTerm == "month") is { } m)
                Schedule(Daytime(day), Kinds.Term, ag.AccountId, m.Id, "year");
            if (_r.Chance(ag.Tech / 90)) Schedule(Daytime(day), Kinds.Reinstall, ag.AccountId, _r.Pick(running).Id);
            if (_r.Chance(seg.Name == SegmentNames.Stable ? 1.0 / 30 : 1.0 / 120)) Schedule(Daytime(day), Kinds.Backup, ag.AccountId, _r.Pick(running).Id);
        }
        if ((ag.LastNpsAt is null || day - ag.LastNpsAt > TimeSpan.FromDays(90)) && _r.Chance(1.0 / 120)) Schedule(Daytime(day), Kinds.Nps, ag.AccountId);
        if (!ag.TwoFactor && _r.Chance((seg.TwoFactor + 0.1) / 250)) Schedule(Daytime(day), Kinds.Security, ag.AccountId);
        if (seg.Name == SegmentNames.Growing && _r.Chance(1.0 / 120)) Schedule(Daytime(day), Kinds.TicketOpen, ag.AccountId, arg: "sales", num: 0);
        // Реферальные начисления копятся на выводимом балансе (видны в следующем состоянии аккаунта)
        if (seg.Name is SegmentNames.Stable or SegmentNames.Growing && _r.Chance(1.0 / 200)) acc.WithdrawBalance += RoundEur(_r.Uniform(1, 20) * 100);
    }

    // ---------- Клиенты и услуги ----------

    private void Register()
    {
        var seg = PickSegment();
        var acc = NewAccount();
        var ag = NewAgent(acc.Id, seg, W.Clock, existing: false);
        Emit("account.registered", acc.Id, null,
            new JsonObject { ["channel"] = ag.Channel, ["country"] = ag.Country, ["customerType"] = ag.CustomerType },
            Records.State(account: Records.Account(acc)));
        var start = RoundEur((seg.Name == SegmentNames.Stable ? _r.Uniform(20, 80) : _r.Uniform(5, 30)) * 100);
        Schedule(W.Clock.AddMinutes(_r.Uniform(2, 10)), Kinds.TopUp, acc.Id, arg: "card", num: start);
        if (ag.Channel == "promo")
        {
            Schedule(W.Clock.AddMinutes(1), Kinds.BonusGrant, acc.Id, arg: "promo", num: RoundEur(_r.Uniform(5, 15) * 100));
            ag.PromoUntil = Iso.Sec(W.Clock.AddDays(_r.Uniform(30, 90)));
            Schedule(ag.PromoUntil.Value, Kinds.BonusExpire, acc.Id);
        }
        if (_r.Chance(0.9)) Schedule(W.Clock.AddMinutes(_r.Uniform(1, 60)), Kinds.Verify, acc.Id, arg: "email");
        Schedule(W.Clock.AddMinutes(_r.Uniform(15, 40)), Kinds.Order, acc.Id);
    }

    private void Order(long accountId)
    {
        var ag = W.Agents[accountId];
        if (ag.Gone || ag.Intent is { Decided: true }) return;
        var seg = _segments[ag.Segment];
        var acc = W.Accounts[accountId];
        var plan = _r.Weighted(seg.Plans);
        var term = _r.Weighted(seg.Terms);
        var price = TermPrice(plan, term, W.Clock);
        var cost = Cost(term, price);
        if (acc.Funds < cost)
        {
            if (!_r.Chance(0.9 * (0.5 + ag.F))) return;
            EmitTopUp(acc, RoundEur(cost * _r.Uniform(1, 2.5) + 100), "card", auto: false);
        }
        var shortLived = seg.Name == SegmentNames.Developer && LiveServices(ag).Any(s => !s.ShortLived) && _r.Chance(0.75);
        var s = NewService(ag, plan, term, price, W.Clock, AddTerm(W.Clock, term), shortLived);
        var p = _plans[plan];
        Emit("service.ordered", accountId, s.Id, new JsonObject
        {
            ["plan"] = plan, ["cpu"] = p.Cpu, ["ramGb"] = p.RamGb, ["diskGb"] = p.DiskGb, ["location"] = s.Location, ["os"] = s.Os, ["term"] = term,
            ["price"] = price, ["promo"] = ag.PromoUntil > W.Clock,
        }, Records.State(service: Records.Service(s)));
        EmitCharge(acc, s, cost, "order");
        Schedule(s.ExpiresAt, Kinds.Due, accountId, s.Id, Ticks(s.ExpiresAt));
        if (seg.Abuser && !ag.AbuseScheduled)
        {
            ag.AbuseScheduled = true;
            Schedule(W.Clock.AddDays(_r.Uniform(3, 40)), Kinds.Block, accountId);
        }
    }

    private void EmitCharge(AccountRec acc, ServiceRec s, long cost, string kind)
    {
        var (fromBalance, fromBonus) = Charge(acc, cost);
        Emit("payment.charged", acc.Id, s.Id,
            new JsonObject { ["serviceId"] = s.Id, ["amount"] = cost, ["kind"] = kind, ["fromBalance"] = fromBalance, ["fromBonus"] = fromBonus },
            Records.State(account: Records.Account(acc), transaction: Records.Transaction(W.NextTransactionId++, s.Id, cost, true, W.Clock, kind == "order" ? "order" : "prolong")));
    }

    private void EmitTopUp(AccountRec acc, long amount, string method, bool auto)
    {
        acc.Balance += amount;
        acc.TotalReplenished += amount;
        Emit("balance.topped_up", acc.Id, null, new JsonObject { ["amount"] = amount, ["method"] = method, ["auto"] = auto },
            Records.State(account: Records.Account(acc)));
    }

    /// <summary>Срок наступил: автопродление при наличии средств, иначе prolong_wait; blocked не продлевается</summary>
    private void Due(AgendaItem it)
    {
        var s = W.Services[it.ServiceId];
        if (!s.Live || Ticks(s.ExpiresAt) != it.Arg || s.Status is ServiceStatuses.Blocked or ServiceStatuses.ProlongWait) return;
        var acc = W.Accounts[s.OwnerId];
        var cost = Cost(s.PaymentTerm, TermPrice(s.Plan, s.PaymentTerm, W.Clock));
        if (s.AutoProlong && acc.Funds >= cost) Renew(s, acc, "auto", cost);
        else if (s.AutoProlong)
        {
            s.PaymentAttempts = 1;
            Emit("payment.failed", acc.Id, s.Id,
                new JsonObject { ["serviceId"] = s.Id, ["amount"] = cost, ["reason"] = "insufficient_funds", ["attempt"] = 1 },
                Records.State(transaction: Records.Transaction(W.NextTransactionId++, s.Id, cost, false, W.Clock, "prolong")));
            Expire(s, "no_funds");
            Schedule(W.Clock.AddDays(1), Kinds.Retry, s.OwnerId, s.Id);
            if (_segments[W.Agents[s.OwnerId].Segment].Attentive && _r.Chance(0.2))
                Schedule(W.Clock.AddHours(_r.Uniform(1, 24)), Kinds.TicketOpen, s.OwnerId, arg: "billing", num: 0);
        }
        else Expire(s, "autoprolong_off");
    }

    private void Expire(ServiceRec s, string reason)
    {
        s.Status = ServiceStatuses.ProlongWait;
        Emit("service.expired", s.OwnerId, s.Id, new JsonObject { ["reason"] = reason }, Records.State(service: Records.Service(s)));
        Schedule(s.ExpiresAt.AddDays(C.GraceDays), Kinds.Grace, s.OwnerId, s.Id, Ticks(s.ExpiresAt));
    }

    private void Renew(ServiceRec s, AccountRec acc, string mode, long cost)
    {
        var from = s.ExpiresAt;
        s.Price = TermPrice(s.Plan, s.PaymentTerm, W.Clock); // новая цена тарифа — с первого продления после вступления в силу
        EmitCharge(acc, s, cost, "prolong");
        var next = AddTerm(from, s.PaymentTerm);
        if (next <= W.Clock) next = AddTerm(W.Clock, s.PaymentTerm);
        s.ExpiresAt = Iso.Sec(next);
        if (s.Status == ServiceStatuses.ProlongWait) s.Status = ServiceStatuses.Active; // suspended продлевается и остаётся suspended
        s.PaymentAttempts = 0;
        Emit("service.renewed", s.OwnerId, s.Id, new JsonObject { ["mode"] = mode, ["amount"] = cost, ["fromExpiresAt"] = Iso.Format(from) },
            Records.State(service: Records.Service(s)));
        Schedule(s.ExpiresAt, Kinds.Due, s.OwnerId, s.Id, Ticks(s.ExpiresAt));
    }

    /// <summary>Повтор списания в ожидании оплаты (до 3 попыток)</summary>
    private void Retry(long serviceId)
    {
        var s = W.Services[serviceId];
        if (s.Status != ServiceStatuses.ProlongWait || !s.AutoProlong) return;
        var acc = W.Accounts[s.OwnerId];
        var cost = Cost(s.PaymentTerm, TermPrice(s.Plan, s.PaymentTerm, W.Clock));
        if (acc.Funds >= cost)
        {
            Renew(s, acc, "auto", cost);
            return;
        }
        if (s.PaymentAttempts >= 3) return;
        s.PaymentAttempts++;
        Emit("payment.failed", acc.Id, s.Id,
            new JsonObject { ["serviceId"] = s.Id, ["amount"] = cost, ["reason"] = "insufficient_funds", ["attempt"] = s.PaymentAttempts },
            Records.State(transaction: Records.Transaction(W.NextTransactionId++, s.Id, cost, false, W.Clock, "prolong")));
        Schedule(W.Clock.AddDays(1), Kinds.Retry, s.OwnerId, s.Id);
    }

    /// <summary>prolong_wait дольше N дней → deleted (non_payment)</summary>
    private void Grace(AgendaItem it)
    {
        var s = W.Services[it.ServiceId];
        if (s.Status == ServiceStatuses.ProlongWait && Ticks(s.ExpiresAt) == it.Arg) Delete(s, "non_payment");
    }

    private void Pay(long serviceId)
    {
        var s = W.Services[serviceId];
        var ag = W.Agents[s.OwnerId];
        if (s.Status != ServiceStatuses.ProlongWait || ag.Gone || ag.Intent is { Decided: true }) return;
        var acc = W.Accounts[s.OwnerId];
        var cost = Cost(s.PaymentTerm, TermPrice(s.Plan, s.PaymentTerm, W.Clock));
        if (acc.Funds < cost) EmitTopUp(acc, RoundEur(cost * _r.Uniform(1, 2) + 100), "card", auto: false);
        Renew(s, acc, "manual", cost);
    }

    private void TopUp(long accountId, long amount, string method)
    {
        var ag = W.Agents[accountId];
        if (ag.Gone || ag.Intent is { Decided: true } || amount <= 0) return;
        var parts = method.Split(':');
        var acc = W.Accounts[accountId];
        EmitTopUp(acc, amount, parts[0], auto: parts.Length > 1 && parts[1] == "auto");
        if (!_segments[ag.Segment].Attentive && !_r.Chance(0.5)) return;
        foreach (var s in LiveServices(ag).Where(s => s.Status == ServiceStatuses.ProlongWait))
            Schedule(W.Clock.AddMinutes(_r.Uniform(1, 30)), Kinds.Pay, accountId, s.Id);
    }

    private void Delete(ServiceRec s, string reason)
    {
        s.Status = ServiceStatuses.Deleted;
        Emit("service.deleted", s.OwnerId, s.Id, new JsonObject { ["reason"] = reason }, Records.State(service: Records.Service(s)));
        var ag = W.Agents[s.OwnerId];
        if (LiveServices(ag).Any()) return;
        var seg = _segments[ag.Segment];
        var cause = ag.Intent is { Decided: true } i ? i.Cause
            : seg.Abuser ? Causes.Abuse
            : !seg.Attentive ? Causes.Neglect
            : seg.Name == SegmentNames.Developer && reason == "user_request" ? Causes.Experiment
            : Causes.NonPayment;
        Truth("lost_all_services", ag.AccountId, new JsonObject
        {
            ["cause"] = cause, ["reason"] = reason, ["intentNo"] = ag.Intent?.No, ["silent"] = ag.Intent?.Silent,
        });
        ag.Gone = cause switch
        {
            Causes.Experiment => false,
            Causes.NonPayment => _r.Chance(0.7),
            _ => true,
        };
        if (ag.Gone && _r.Chance(0.1)) Schedule(W.Clock.AddDays(_r.Uniform(1, 10)), Kinds.Close, ag.AccountId);
    }

    private void Login(long accountId)
    {
        var ag = W.Agents[accountId];
        if (ag.Gone) return;
        Emit("account.login", accountId, null, new JsonObject
        {
            ["channel"] = _r.Weighted(new Dictionary<string, double> { ["panel"] = 0.7, ["api"] = 0.2, ["mobile"] = 0.1 }),
            ["success"] = !_r.Chance(0.03),
            ["mfa"] = ag.TwoFactor,
        });
    }

    /// <summary>Телеметрия за прошедшие сутки (00:05): нагрузка = база · e · ритм недели · шум; 2% дней без данных</summary>
    private void Usage(long accountId)
    {
        var ag = W.Agents[accountId];
        if (ag.Gone) return;
        var day = Iso.DayStart(W.Clock).AddDays(-1);
        var key = Iso.Date(day);
        var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var rhythm = weekend ? (ag.Segment == SegmentNames.Stable ? 0.7 : 0.9) : 1.0;
        if (day.Month == 12 && day.Day >= 24 || day.Month == 1 && day.Day <= 2) rhythm *= 0.75;
        var vacation = ag.VacationUntil > day ? 0.4 : 1;
        var intent = ag.Intent;
        var intentFactor = intent is { Silent: false } && intent.Cause != Causes.Finance ? (intent.Decided ? 0.2 : 1 - 0.7 * intent.Progress(day)) : 1;
        foreach (var s in LiveServices(ag).Where(s => s.Status != ServiceStatuses.Blocked && s.CreatedAt < day.AddHours(12)).ToList())
        {
            var down = s.DowntimeByDay.Remove(key, out var d) ? d : 0;
            if (_r.Chance(C.Noise.TelemetryGap)) continue;
            double cpu = 0, ram = 0, tin = 0, tout = 0, up = 0;
            if (s.Status == ServiceStatuses.Active)
            {
                cpu = Math.Clamp(ag.CpuBase * Math.Clamp(ag.E, 0.02, 1) * rhythm * vacation * intentFactor * _r.LogNormal(1, 0.25), 0.1, 100);
                ram = Math.Clamp(15 + cpu * 0.6 + _r.Normal(0, 3), 1, 100);
                tout = cpu * _plans[s.Plan].Cpu * 0.08 * _r.LogNormal(1, 0.4);
                tin = tout * _r.Uniform(0.2, 0.5);
                up = 100.0 * (1 - Math.Min(1440, down) / 1440.0);
                s.DiskUsedPct = Math.Clamp(s.DiskUsedPct + _r.Normal(0.05, 0.1) * ag.E, 3, 98);
            }
            Emit("usage.daily", accountId, s.Id, new JsonObject
            {
                ["serviceId"] = s.Id, ["date"] = key, ["cpuAvgPct"] = Math.Round(cpu, 1), ["ramAvgPct"] = Math.Round(ram, 1),
                ["trafficInGb"] = Math.Round(tin, 2), ["trafficOutGb"] = Math.Round(tout, 2), ["diskUsedPct"] = Math.Round(s.DiskUsedPct, 1),
                ["uptimePct"] = Math.Round(up, 1),
            });
        }
    }

    // ---------- Глобальные события ----------

    /// <summary>Авария ДЦ → аварии услуг → недовольство → обращения; после критической — иногда компенсация</summary>
    private void Incident(string location, int severity)
    {
        var id = W.NextIncidentId++;
        var duration = (int)Math.Round(_r.LogNormal(new[] { 20.0, 90.0, 240.0 }[severity], 0.5));
        var sev = Severities[severity];
        Emit("datacenter.incident", null, null, new JsonObject { ["location"] = location, ["incidentId"] = id, ["severity"] = sev, ["durationMin"] = duration });
        var fraction = new[] { 0.2, 0.5, 0.9 }[severity];
        var affected = W.Services.Values.Where(s => s.Status == ServiceStatuses.Active && s.Location == location).OrderBy(s => s.Id)
            .Where(_ => _r.Chance(fraction)).ToList();
        var key = Iso.Date(W.Clock);
        foreach (var s in affected)
        {
            Emit("service.incident", s.OwnerId, s.Id, new JsonObject { ["serviceId"] = s.Id, ["incidentId"] = id, ["severity"] = sev, ["durationMin"] = duration });
            s.DowntimeByDay[key] = Math.Min(1440, s.DowntimeByDay.GetValueOrDefault(key) + duration);
        }
        foreach (var owner in affected.Select(s => s.OwnerId).Distinct())
        {
            var ag = W.Agents[owner];
            if (ag.Gone) continue;
            ag.S = Math.Clamp(ag.S - new[] { 0.05, 0.15, 0.3 }[severity] * _r.Uniform(0.7, 1.3), 0, 1);
            if (_segments[ag.Segment].Attentive && _r.Chance(new[] { 0.04, 0.25, 0.55 }[severity] * (0.6 + 0.4 * ag.Tech)))
                Schedule(W.Clock.AddMinutes(_r.Uniform(10, 180)), Kinds.TicketOpen, owner, arg: "technical", num: severity);
            if (severity == 2 && _r.Chance(0.3))
                Schedule(W.Clock.AddDays(_r.Uniform(1, 3)), Kinds.BonusGrant, owner, arg: "compensation", num: Math.Max(100, RoundEur(MonthlyCost(ag, W.Clock) * 0.15)));
        }
        if (severity > 0) Narrate?.Invoke($"Авария в {location} ({sev}, {duration} мин): затронуто услуг {affected.Count}");
    }

    private void PriceChange(string plan, double percent)
    {
        var pp = W.PlanPrices[plan];
        var from = pp.At(W.Clock);
        var to = (long)Math.Round(from * (1 + percent / 100));
        var effective = W.Clock.AddDays(14);
        pp.Current = from;
        pp.Next = to;
        pp.NextFrom = effective;
        Emit("tariff.price_changed", null, null, new JsonObject { ["plan"] = plan, ["priceFrom"] = from, ["priceTo"] = to, ["effectiveAt"] = Iso.Format(effective) });
        foreach (var ag in W.Agents.Values.Where(a => !a.Gone && LiveServices(a).Any(s => s.Plan == plan)).OrderBy(a => a.AccountId))
        {
            ag.S = Math.Clamp(ag.S - 0.25 * ag.PriceSensitivity, 0, 1);
            ag.PriceRiseAt = W.Clock;
        }
        Narrate?.Invoke($"Цена тарифа {plan}: {from} → {to} с {effective:dd.MM}");
    }

    // ---------- Поддержка и опыт ----------

    private void TicketOpen(long accountId, string category, int severity)
    {
        var ag = W.Agents[accountId];
        if (ag.Gone && category != "abuse") return;
        if (category == "cancellation" && ag.Intent is null) return; // передумал
        var t = new Ticket { Id = W.NextTicketId++, AccountId = accountId, Category = category, Priority = Priorities[Math.Clamp(severity, 0, 2)], OpenedAt = W.Clock };
        t.Unsolved = _r.Chance(category switch
        {
            "technical" => 0.2 + 0.15 * severity,
            "billing" => 0.1,
            "cancellation" => 0.5,
            "abuse" => 0.3,
            _ => 0.05,
        });
        W.Tickets[t.Id] = t;
        Emit("ticket.opened", accountId, null, new JsonObject { ["ticketId"] = t.Id, ["category"] = category, ["priority"] = t.Priority });
        var reply = Math.Max(1, _r.LogNormal(category == "technical" && severity > 0 ? 240 : 40, 1.0));
        var close = Math.Max(reply / 60 + 0.1, _r.LogNormal(category == "cancellation" ? 24 : 8, 0.9));
        Schedule(W.Clock.AddMinutes(reply), Kinds.TicketReply, accountId, t.Id);
        Schedule(W.Clock.AddHours(close), Kinds.TicketClose, accountId, t.Id);
        Schedule(W.Clock.AddHours(72), Kinds.TicketCheck, accountId, t.Id);
    }

    private void TicketReply(long ticketId)
    {
        if (!W.Tickets.TryGetValue(ticketId, out var t)) return;
        Emit("ticket.first_replied", t.AccountId, null, new JsonObject { ["ticketId"] = t.Id, ["minutes"] = (int)Math.Round((W.Clock - t.OpenedAt).TotalMinutes) });
    }

    private void TicketClose(long ticketId)
    {
        if (!W.Tickets.TryGetValue(ticketId, out var t)) return;
        var ag = W.Agents[t.AccountId];
        t.Closed = true;
        var data = new JsonObject
        {
            ["ticketId"] = t.Id, ["resolution"] = t.Unsolved ? "unsolved" : "solved", ["hours"] = Math.Round((W.Clock - t.OpenedAt).TotalHours, 1),
        };
        if (_r.Chance(0.6)) data["csat"] = t.Unsolved ? _r.Next(1, 3) : Math.Clamp((int)Math.Round(3 + 2 * ag.S + _r.Normal(0, 0.6)), 1, 5);
        Emit("ticket.closed", t.AccountId, null, data);
        if (!t.Unsolved) ag.S = Math.Clamp(ag.S + 0.05, 0, 1);
        else if (!t.Penalized)
        {
            ag.S = Math.Clamp(ag.S - 0.15, 0, 1);
            t.Penalized = true;
        }
        W.Tickets.Remove(t.Id);
    }

    /// <summary>Обращение висит 72 ч — удовлетворённость падает</summary>
    private void TicketCheck(long ticketId)
    {
        if (!W.Tickets.TryGetValue(ticketId, out var t) || t.Closed || t.Penalized) return;
        var ag = W.Agents[t.AccountId];
        ag.S = Math.Clamp(ag.S - 0.15, 0, 1);
        t.Penalized = true;
    }

    private void Nps(long accountId)
    {
        var ag = W.Agents[accountId];
        if (ag.Gone) return;
        ag.LastNpsAt = W.Clock;
        if (!_r.Chance(0.35)) return;
        Emit("nps.responded", accountId, null, new JsonObject { ["score"] = Math.Clamp((int)Math.Round(10 * ag.S - 1 + _r.Normal(0, 1.3)), 0, 10) });
    }

    // ---------- Действия с услугами ----------

    private void AutoProlongChange(long serviceId, bool enabled, int intentNo)
    {
        var s = W.Services[serviceId];
        var ag = W.Agents[s.OwnerId];
        if (!s.Live || s.Status == ServiceStatuses.Blocked || s.AutoProlong == enabled || !IntentActive(ag, intentNo) || ag.Gone && enabled) return;
        s.AutoProlong = enabled;
        Emit("service.autoprolong_changed", s.OwnerId, s.Id, new JsonObject { ["enabled"] = enabled, ["by"] = "user" }, Records.State(service: Records.Service(s)));
        if (intentNo > 0 && !enabled && ag.Intent is { } i)
        {
            var first = !i.TurnedOffAutoProlong;
            i.TurnedOffAutoProlong = true;
            if (first && _r.Chance(0.3)) Schedule(W.Clock.AddHours(_r.Uniform(1, 48)), Kinds.Offer, ag.AccountId); // удержание — воздействие, не признак
        }
    }

    private void Suspend(long serviceId)
    {
        var s = W.Services[serviceId];
        if (s.Status != ServiceStatuses.Active || W.Agents[s.OwnerId].Gone) return;
        s.Status = ServiceStatuses.Suspended;
        Emit("service.suspended", s.OwnerId, s.Id, new JsonObject { ["by"] = "user" }, Records.State(service: Records.Service(s)));
        Schedule(W.Clock.AddDays(_r.Uniform(1, 10)), Kinds.Resume, s.OwnerId, s.Id);
    }

    private void Resume(long serviceId)
    {
        var s = W.Services[serviceId];
        if (s.Status != ServiceStatuses.Suspended) return;
        s.Status = ServiceStatuses.Active;
        Emit("service.resumed", s.OwnerId, s.Id, new JsonObject { ["by"] = "user" }, Records.State(service: Records.Service(s)));
    }

    private void PlanChange(long serviceId, bool up, int intentNo)
    {
        var s = W.Services[serviceId];
        var ag = W.Agents[s.OwnerId];
        if (s.Status is not (ServiceStatuses.Active or ServiceStatuses.Suspended) || ag.Gone || !IntentActive(ag, intentNo)) return;
        var idx = C.Plans.FindIndex(p => p.Name == s.Plan);
        var next = Math.Clamp(idx + (up ? 1 : -1), 0, C.Plans.Count - 1);
        if (next == idx) return;
        var fromPlan = s.Plan;
        var priceFrom = s.Price;
        s.Plan = C.Plans[next].Name;
        s.Price = TermPrice(s.Plan, s.PaymentTerm, W.Clock);
        Emit("service.plan_changed", s.OwnerId, s.Id, new JsonObject
        {
            ["fromPlan"] = fromPlan, ["toPlan"] = s.Plan, ["direction"] = up ? "upgrade" : "downgrade", ["priceFrom"] = priceFrom, ["priceTo"] = s.Price,
        }, Records.State(service: Records.Service(s)));
    }

    private void TermChange(long serviceId, string term)
    {
        var s = W.Services[serviceId];
        if (s.Status is not (ServiceStatuses.Active or ServiceStatuses.Suspended) || s.PaymentTerm is "hour" or "week" || s.PaymentTerm == term) return;
        var from = s.PaymentTerm;
        s.PaymentTerm = term;
        s.Price = TermPrice(s.Plan, term, W.Clock);
        Emit("service.term_changed", s.OwnerId, s.Id, new JsonObject { ["fromTerm"] = from, ["toTerm"] = term }, Records.State(service: Records.Service(s)));
    }

    private void ServiceBehavior(long serviceId, string type, int intentNo)
    {
        var s = W.Services[serviceId];
        var ag = W.Agents[s.OwnerId];
        if (s.Status != ServiceStatuses.Active || ag.Gone || !IntentActive(ag, intentNo)) return;
        var data = type == "service.reinstalled"
            ? new JsonObject { ["os"] = s.Os = _r.Pick(Oses) }
            : new JsonObject { ["kind"] = _r.Chance(0.6) ? "snapshot" : "backup" };
        Emit(type, s.OwnerId, s.Id, data);
    }

    private void Block(long accountId)
    {
        var ag = W.Agents[accountId];
        foreach (var s in LiveServices(ag).Where(s => s.Status != ServiceStatuses.Blocked).ToList())
        {
            s.Status = ServiceStatuses.Blocked;
            Emit("service.blocked", accountId, s.Id, new JsonObject { ["reason"] = "abuse" }, Records.State(service: Records.Service(s)));
            Schedule(W.Clock.AddDays(_r.Uniform(5, 15)), Kinds.AdminDelete, accountId, s.Id);
        }
        if (_r.Chance(0.5)) Schedule(W.Clock.AddHours(_r.Uniform(1, 24)), Kinds.TicketOpen, accountId, arg: "abuse", num: 1);
    }

    private void AdminDelete(long serviceId)
    {
        var s = W.Services[serviceId];
        if (s.Status == ServiceStatuses.Blocked) Delete(s, "abuse");
    }

    private void UserDelete(long serviceId, string reason, int intentNo)
    {
        var s = W.Services[serviceId];
        if (!s.Live || s.Status == ServiceStatuses.Blocked || !IntentActive(W.Agents[s.OwnerId], intentNo)) return;
        Delete(s, reason);
    }

    // ---------- Деньги аккаунта ----------

    private void Withdraw(long accountId, int intentNo)
    {
        var acc = W.Accounts[accountId];
        if (acc.WithdrawBalance <= 0 || !IntentActive(W.Agents[accountId], intentNo)) return;
        var amount = acc.WithdrawBalance;
        acc.WithdrawBalance = 0;
        Emit("withdrawal.requested", accountId, null, new JsonObject { ["amount"] = amount }, Records.State(account: Records.Account(acc)));
    }

    private void BonusExpire(long accountId)
    {
        var acc = W.Accounts[accountId];
        var ag = W.Agents[accountId];
        if (acc.BonusState != "unlocked" && acc.BonusBalance == 0) return;
        var amount = acc.BonusBalance;
        acc.BonusBalance = 0;
        acc.BonusState = "locked";
        Emit("bonus.expired", accountId, null, new JsonObject { ["amount"] = amount }, Records.State(account: Records.Account(acc)));
        ag.PromoEndedAt = W.Clock;
        ag.S = Math.Clamp(ag.S - 0.15 * ag.PriceSensitivity, 0, 1);
    }

    private void BonusGrant(long accountId, long amount, string reason)
    {
        var acc = W.Accounts[accountId];
        var ag = W.Agents[accountId];
        if (ag.Gone || amount <= 0) return;
        acc.BonusBalance += amount;
        acc.BonusState = "unlocked";
        Emit("bonus.granted", accountId, null, new JsonObject { ["amount"] = amount, ["reason"] = reason }, Records.State(account: Records.Account(acc)));
        if (reason is "compensation" or "retention") ag.S = Math.Clamp(ag.S + 0.1, 0, 1);
    }

    private void Reminder(long serviceId, int daysBefore)
    {
        var s = W.Services[serviceId];
        var ag = W.Agents[s.OwnerId];
        if (s.Status is not (ServiceStatuses.Active or ServiceStatuses.Suspended) || ag.Gone) return;
        var e = Emit("renewal.reminder_sent", s.OwnerId, s.Id, new JsonObject
        {
            ["serviceId"] = s.Id, ["daysBefore"] = daysBefore, ["channel"] = _r.Chance(0.7) ? "email" : "telegram",
        });
        if (_r.Chance(0.55 * Math.Min(1, ag.E + 0.3) * (ag.Intent is { Silent: false } ? 0.3 : 1)))
            Schedule(W.Clock.AddHours(_r.Uniform(0.5, 30)), Kinds.NotificationOpen, s.OwnerId, arg: e.EventId);
    }

    private void NotificationOpen(long accountId, string reference)
    {
        if (W.Agents[accountId].Gone) return;
        Emit("notification.opened", accountId, null, new JsonObject { ["ref"] = reference });
    }

    // ---------- Решение уйти и удержание ----------

    /// <summary>Намерение созрело: клиент больше не платит; часть услуг удаляет сам, остальные истекают</summary>
    private void Decide(long accountId, int intentNo)
    {
        var ag = W.Agents[accountId];
        if (ag.Gone || ag.Intent is not { Decided: false } intent || intent.No != intentNo) return;
        intent.Decided = true;
        Truth("intent_decided", accountId, new JsonObject { ["no"] = intent.No, ["cause"] = intent.Cause, ["silent"] = intent.Silent });
        foreach (var s in LiveServices(ag).Where(s => s.Status != ServiceStatuses.Blocked).ToList())
        {
            if (_r.Chance(0.35)) Schedule(W.Clock.AddHours(_r.Uniform(0, 72)), Kinds.UserDelete, accountId, s.Id, "user_request", intent.No);
            else if (s.AutoProlong) AutoProlongChange(s.Id, false, intent.No);
        }
    }

    private void Offer(long accountId)
    {
        var ag = W.Agents[accountId];
        if (ag.Gone) return;
        var id = W.NextOfferId++;
        var kind = _r.Pick(new[] { "discount", "bonus", "term_upgrade" });
        var value = kind == "bonus" ? RoundEur(_r.Uniform(3, 10) * 100) : kind == "discount" ? 20 : 1;
        Emit("retention.offer_sent", accountId, null, new JsonObject { ["offerId"] = id, ["kind"] = kind, ["value"] = value });
        if (_r.Chance(0.35)) Schedule(W.Clock.AddHours(_r.Uniform(1, 72)), Kinds.OfferRedeem, accountId, arg: $"{id}|{kind}|{value}");
    }

    private void OfferRedeem(long accountId, string arg)
    {
        var ag = W.Agents[accountId];
        if (ag.Gone) return;
        var parts = arg.Split('|');
        Emit("retention.offer_redeemed", accountId, null, new JsonObject { ["offerId"] = long.Parse(parts[0], CultureInfo.InvariantCulture) });
        if (parts[1] == "bonus") BonusGrant(accountId, long.Parse(parts[2], CultureInfo.InvariantCulture), "retention");
        ag.S = Math.Clamp(ag.S + 0.25, 0, 1);
        if (ag.Intent is { Decided: false } && _r.Chance(0.6)) CancelIntent(ag, "retention");
    }

    private void Verify(long accountId, string level)
    {
        if (W.Agents[accountId].Gone) return;
        Emit("account.verified", accountId, null, new JsonObject { ["level"] = level }, Records.State(account: Records.Account(W.Accounts[accountId])));
    }

    private void Security(long accountId)
    {
        var ag = W.Agents[accountId];
        if (ag.Gone || ag.TwoFactor) return;
        ag.TwoFactor = true;
        Emit("account.security_changed", accountId, null, new JsonObject { ["twoFactor"] = true });
    }

    private void Close(long accountId)
    {
        if (LiveServices(W.Agents[accountId]).Any()) return;
        Emit("account.closed", accountId, null, new JsonObject { ["reason"] = "user_request" }, Records.State(account: Records.Account(W.Accounts[accountId])));
    }
}
