using System.Text.Json.Nodes;

namespace Aeza.Risk.SourceSimulator;

/// <summary>Сценарий защиты поверх live: причинно-следственная цепочка одного клиента за 2–3 минуты</summary>
public sealed partial class Simulation
{
    private const int ScenarioSteps = 7;

    /// <summary>Запустить сценарий: клиент с ближайшим сроком (≥ 10 мин), шаги через <paramref name="step"/></summary>
    public string? InjectScenario(TimeSpan step)
    {
        var now = W.Clock;
        var candidate = W.Services.Values
            .Where(s => s.Status == ServiceStatuses.Active && s.AutoProlong && s.PaymentTerm != "hour" && s.ExpiresAt >= now.AddMinutes(10)
                        && W.Agents.TryGetValue(s.OwnerId, out var a) && !a.Gone && _segments[a.Segment].Attentive)
            .OrderBy(s => s.ExpiresAt).ThenBy(s => s.Id).FirstOrDefault()
            ?? W.Services.Values.Where(s => s.Status == ServiceStatuses.Active && W.Agents.TryGetValue(s.OwnerId, out var a) && !a.Gone)
                .OrderBy(s => s.ExpiresAt).ThenBy(s => s.Id).FirstOrDefault();
        if (candidate is null) return null;
        for (var i = 0; i < ScenarioSteps; i++) Schedule(now + step * (i + 1), Kinds.Scenario, candidate.OwnerId, candidate.Id, num: i);
        return $"Сценарий: клиент {candidate.OwnerId}, услуга {candidate.Name} ({candidate.Location}), срок {candidate.ExpiresAt:dd.MM HH:mm} UTC";
    }

    private void ScenarioStep(long accountId, long serviceId, int step)
    {
        var ag = W.Agents[accountId];
        var s = W.Services[serviceId];
        var acc = W.Accounts[accountId];
        if (ag.Gone || !s.Live) return;
        switch (step)
        {
            case 0:
            {
                var id = W.NextIncidentId++;
                Emit("datacenter.incident", null, null, new JsonObject { ["location"] = s.Location, ["incidentId"] = id, ["severity"] = "critical", ["durationMin"] = 180 });
                Emit("service.incident", accountId, s.Id, new JsonObject { ["serviceId"] = s.Id, ["incidentId"] = id, ["severity"] = "critical", ["durationMin"] = 180 });
                var key = Iso.Date(W.Clock);
                s.DowntimeByDay[key] = Math.Min(1440, s.DowntimeByDay.GetValueOrDefault(key) + 180);
                ag.S = Math.Clamp(ag.S - 0.3, 0, 1);
                Narrate?.Invoke($"1/7 Авария ДЦ {s.Location} (critical, 3 ч): {s.Name} клиента {accountId} недоступна");
                break;
            }
            case 1:
            {
                var t = new Ticket { Id = W.NextTicketId++, AccountId = accountId, Category = "technical", Priority = "urgent", OpenedAt = W.Clock, Unsolved = true };
                W.Tickets[t.Id] = t;
                Emit("ticket.opened", accountId, null, new JsonObject { ["ticketId"] = t.Id, ["category"] = "technical", ["priority"] = "urgent" });
                Narrate?.Invoke($"2/7 Клиент {accountId} открыл обращение #{t.Id} (technical, urgent)");
                break;
            }
            case 2:
            {
                var t = W.Tickets.Values.Where(x => x.AccountId == accountId && !x.Closed).OrderBy(x => x.Id).LastOrDefault();
                if (t is null) break;
                t.Closed = true;
                t.Penalized = true;
                Emit("ticket.closed", accountId, null, new JsonObject
                {
                    ["ticketId"] = t.Id, ["resolution"] = "unsolved", ["csat"] = 1, ["hours"] = Math.Round((W.Clock - t.OpenedAt).TotalHours, 2),
                });
                W.Tickets.Remove(t.Id);
                ag.S = Math.Clamp(ag.S - 0.15, 0, 1);
                Narrate?.Invoke($"3/7 Обращение #{t.Id} закрыто без решения, оценка 1");
                break;
            }
            case 3:
                Emit("nps.responded", accountId, null, new JsonObject { ["score"] = 2 });
                ag.LastNpsAt = W.Clock;
                Narrate?.Invoke($"4/7 NPS клиента {accountId}: 2 (критик)");
                break;
            case 4:
            {
                ag.Intent = new Intent
                {
                    No = ++ag.IntentCount, Cause = Causes.Dissatisfaction, StartedAt = W.Clock, DueAt = s.ExpiresAt, TurnedOffAutoProlong = true,
                };
                Truth("intent_started", accountId, new JsonObject { ["no"] = ag.Intent.No, ["cause"] = Causes.Dissatisfaction, ["dueAt"] = Iso.Format(s.ExpiresAt), ["silent"] = false, ["scenario"] = true });
                foreach (var x in LiveServices(ag).Where(x => x.AutoProlong).ToList()) AutoProlongChange(x.Id, false, ag.Intent.No);
                Narrate?.Invoke($"5/7 Клиент {accountId} выключил автопродление");
                break;
            }
            case 5:
                Emit("service.backup_created", accountId, s.Id, new JsonObject { ["kind"] = "backup" });
                Narrate?.Invoke($"6/7 Бэкап {s.Name} перед уходом");
                break;
            case 6:
            {
                var withdrawn = acc.WithdrawBalance;
                Withdraw(accountId, 0);
                if (ag.Intent is { } intent)
                {
                    intent.Decided = true;
                    Truth("intent_decided", accountId, new JsonObject { ["no"] = intent.No, ["cause"] = intent.Cause, ["silent"] = false, ["scenario"] = true });
                }
                Narrate?.Invoke($"7/7 {(withdrawn > 0 ? $"Вывод {withdrawn / 100m:0.00} €" : "Выводить нечего")}, клиент решил уйти. Срок {s.Name} — {s.ExpiresAt:dd.MM HH:mm} UTC: тогда prolong_wait, через {C.GraceDays} дн. — удаление");
                break;
            }
        }
    }
}
