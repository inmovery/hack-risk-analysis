using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aeza.Risk.SourceSimulator;

/// <summary>Агентная модель источника</summary>
public sealed partial class Simulation
{
    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = false };
    private static readonly string[] Oses = ["ubuntu-24.04", "debian-12", "almalinux-9", "windows-2022", "rocky-9"];
    private static readonly Dictionary<string, double> Countries = new()
    {
        ["DE"] = 0.22, ["RU"] = 0.2, ["NL"] = 0.1, ["US"] = 0.1, ["PL"] = 0.08, ["FI"] = 0.05, ["KZ"] = 0.08, ["FR"] = 0.07, ["GB"] = 0.1,
    };

    private readonly Rng _r;
    private readonly PriorityQueue<AgendaItem, (long, long)> _q = new();
    private readonly Dictionary<string, SegmentConfig> _segments;
    private readonly Dictionary<string, PlanConfig> _plans;
    private IEventSink _sink = new ListSink();
    private ITruthSink? _truth;
    /// <summary>«Истина» до первого хода (профили клиентов при создании мира) — уходит в первый переданный приёмник</summary>
    private readonly List<JsonObject> _pendingTruth = [];

    private Simulation(World world, SimConfig config, Rng rng)
    {
        W = world;
        C = config;
        _r = rng;
        _segments = config.Segments.ToDictionary(s => s.Name);
        _plans = config.Plans.ToDictionary(p => p.Name);
        foreach (var item in world.Agenda) _q.Enqueue(item, (item.At.UtcTicks, item.Seq));
        world.Agenda = [];
    }

    public World W { get; }

    public SimConfig C { get; }

    /// <summary>Писать скрытое состояние каждого клиента по дням (truth.jsonl)</summary>
    public bool TruthDaily { get; set; } = true;

    /// <summary>Подписи для лога (сценарий, заметные события)</summary>
    public Action<string>? Narrate { get; set; }

    /// <summary>Тестовые данные на T₀: транзакции существующих клиентов до начала истории</summary>
    public List<JsonObject> InitialTransactions { get; } = [];

    /// <summary>Запланированных действий</summary>
    public int Pending => _q.Count;

    // ---------- Создание мира ----------

    /// <summary>Мир на начало истории T₀: часть клиентов уже есть (стаж до 3 лет), остальные зарегистрируются за период</summary>
    public static Simulation CreateHistory(SimConfig config, ulong seed, DateTimeOffset t0)
    {
        config.Validate();
        t0 = Iso.Sec(t0);
        var world = new World
        {
            Seed = seed, StartedAt = t0, Clock = t0, HistoryEnd = t0.AddDays(config.Days), Config = config,
            RunId = RunIdOf($"{seed}|{Iso.Format(t0)}|{config.ToJson()}"),
        };
        var sim = new Simulation(world, config, new Rng(seed));
        foreach (var p in config.Plans) world.PlanPrices[p.Name] = new PlanPrice { Current = p.MonthPrice, Next = p.MonthPrice };
        var existing = (int)Math.Round(config.Customers * Math.Clamp(config.ExistingShare, 0, 1));
        for (var i = 0; i < existing; i++) sim.CreateExistingCustomer(t0);
        var arrivals = Enumerable.Range(0, config.Customers - existing)
            .Select(_ => Iso.Sec(t0.AddSeconds(sim._r.Uniform(3600, config.Days * 86400.0 - 3600)))).Order().ToList();
        foreach (var at in arrivals) sim.Schedule(at, Kinds.Register);
        sim.Schedule(t0, Kinds.Day);
        return sim;
    }

    /// <summary>Мир из тестовых данных в формате организаторов (accounts / services / transactions) на момент <paramref name="now"/></summary>
    public static Simulation FromSourceData(JsonObject data, SimConfig config, ulong seed, DateTimeOffset now, string sourceHash)
    {
        config.Validate();
        now = Iso.Sec(now);
        var world = new World
        {
            Seed = seed, StartedAt = now, Clock = now, HistoryEnd = now, Config = config,
            RunId = RunIdOf($"{seed}|{Iso.Format(now)}|{sourceHash}"),
        };
        var sim = new Simulation(world, config, new Rng(seed));
        foreach (var p in config.Plans) world.PlanPrices[p.Name] = new PlanPrice { Current = p.MonthPrice, Next = p.MonthPrice };
        sim.Import(data, now);
        sim.Schedule(now, Kinds.Day);
        return sim;
    }

    /// <summary>Мир из файла состояния (переживает перезапуск: та же очередь, те же счётчики и генератор)</summary>
    public static Simulation Load(string json)
    {
        var world = JsonSerializer.Deserialize<World>(json, StateJson) ?? throw new InvalidOperationException("Файл состояния симулятора пуст");
        if (world.FormatVersion != 1) throw new InvalidOperationException($"Неизвестная версия состояния симулятора: {world.FormatVersion}");
        return new Simulation(world, world.Config ?? new SimConfig(), Rng.FromState(world.Rng));
    }

    /// <summary>Состояние целиком: мир, очередь, генератор</summary>
    public string Save()
    {
        W.Rng = _r.State;
        W.Agenda = _q.UnorderedItems.Select(x => x.Element).OrderBy(i => i.At).ThenBy(i => i.Seq).ToList();
        try
        {
            return JsonSerializer.Serialize(W, StateJson);
        }
        finally
        {
            W.Agenda = [];
        }
    }

    private static string RunIdOf(string material) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..8];

    // ---------- Ход времени ----------

    /// <summary>Выполнить все действия до <paramref name="target"/> включительно; события — в <paramref name="sink"/></summary>
    public void AdvanceTo(DateTimeOffset target, IEventSink sink, ITruthSink? truth = null)
    {
        _sink = sink;
        _truth = truth;
        if (truth is not null && _pendingTruth.Count > 0)
        {
            foreach (var line in _pendingTruth) truth.Write(line);
            _pendingTruth.Clear();
        }
        while (_q.TryPeek(out var item, out _) && item.At <= target)
        {
            _q.Dequeue();
            if (item.At > W.Clock) W.Clock = item.At;
            Execute(item);
        }
        if (target > W.Clock) W.Clock = Iso.Sec(target);
    }

    /// <summary>Запланировать действие. Не раньше следующей секунды после текущего момента</summary>
    private AgendaItem Schedule(DateTimeOffset at, string kind, long account = 0, long service = 0, string? arg = null, double num = 0)
    {
        at = Iso.Sec(at);
        if (at <= W.Clock) at = W.Clock.AddSeconds(1);
        var item = new AgendaItem { At = at, Seq = W.NextAgendaSeq++, Kind = kind, AccountId = account, ServiceId = service, Arg = arg, Num = num };
        _q.Enqueue(item, (at.UtcTicks, item.Seq));
        return item;
    }

    private SimEvent Emit(string type, long? accountId, long? serviceId, JsonObject data, JsonObject? state = null)
    {
        long? seq = accountId is { } a ? ++W.Agents[a].Seq : null;
        var e = new SimEvent($"{W.RunId}-{W.NextEventNo++:D9}", type, W.Clock, accountId, seq, serviceId, data, state);
        _sink.Write(e);
        return e;
    }

    private void Truth(string kind, long accountId, JsonObject fields)
    {
        var line = new JsonObject { ["k"] = kind, ["at"] = Iso.Format(W.Clock), ["a"] = accountId };
        foreach (var (k, v) in fields.ToList())
        {
            fields.Remove(k);
            line[k] = v;
        }
        if (_truth is null) _pendingTruth.Add(line);
        else _truth.Write(line);
    }

    // ---------- Клиенты ----------

    private SegmentConfig PickSegment()
    {
        var names = C.Segments.Select(s => s.Name).ToList();
        return _segments[names[_r.Weighted(C.Segments.Select(s => s.Share).ToList())]];
    }

    private AccountRec NewAccount()
    {
        var id = W.NextAccountId++;
        var a = new AccountRec { Id = id, Email = $"user-{id:00000}@example.test" };
        W.Accounts[id] = a;
        return a;
    }

    private Agent NewAgent(long accountId, SegmentConfig seg, DateTimeOffset createdAt, bool existing)
    {
        var ag = new Agent
        {
            AccountId = accountId,
            Segment = seg.Name,
            Channel = _r.Chance(seg.PromoShare) ? "promo" : _r.Weighted(new Dictionary<string, double> { ["organic"] = 0.5, ["ads"] = 0.3, ["referral"] = 0.2 }),
            Country = _r.Weighted(Countries),
            CustomerType = _r.Chance(seg.Business) ? "business" : "individual",
            Habit = seg.Habits.Count == 0 ? "manual" : _r.Weighted(seg.Habits),
            PriceSensitivity = Math.Clamp(seg.PriceSensitivity + _r.Normal(0, 0.1), 0, 1),
            Tech = _r.Uniform(0.2, 1),
            LoginRate = seg.LoginsPerDay * _r.LogNormal(1, 0.4),
            CpuBase = seg.CpuBase * _r.LogNormal(1, 0.35),
            TwoFactor = _r.Chance(seg.TwoFactor),
            CreatedAt = createdAt,
            S0 = Math.Clamp(seg.Satisfaction + _r.Normal(0, 0.07), 0.05, 1),
            E0 = Math.Clamp(seg.Need + _r.Normal(0, 0.08), 0.01, 1),
            F0 = Math.Clamp(seg.Finance + _r.Normal(0, 0.1), 0.05, 1),
        };
        ag.S = ag.S0;
        ag.E = ag.E0;
        ag.F = ag.F0;
        W.Agents[accountId] = ag;
        Truth("profile", accountId, new JsonObject
        {
            ["segment"] = ag.Segment, ["channel"] = ag.Channel, ["habit"] = ag.Habit, ["customerType"] = ag.CustomerType,
            ["existing"] = existing, ["createdAt"] = Iso.Format(createdAt),
        });
        return ag;
    }

    private void CreateExistingCustomer(DateTimeOffset t0)
    {
        var seg = PickSegment();
        var acc = NewAccount();
        var createdAt = Iso.Sec(t0.AddDays(-_r.Uniform(7, 1000)));
        var ag = NewAgent(acc.Id, seg, createdAt, existing: true);
        var count = 1 + _r.Poisson(Math.Max(0, seg.ServicesMean - 1));
        long spent = 0;
        for (var i = 0; i < count; i++)
        {
            var plan = _r.Weighted(seg.Plans);
            var term = _r.Weighted(seg.Terms);
            var created = Iso.Sec(createdAt.AddSeconds(_r.Uniform(0, Math.Max(1, (t0 - createdAt).TotalSeconds - 86400))));
            var price = TermPrice(plan, term, t0);
            var cost = Cost(term, price);
            DateTimeOffset expires;
            if (term == "hour") expires = Iso.Sec(t0.AddSeconds(_r.Uniform(3600, 86400)));
            else
            {
                expires = created;
                while (expires <= t0) expires = AddTerm(expires, term);
            }
            var s = NewService(ag, plan, term, price, created, expires, shortLived: seg.Name == SegmentNames.Developer && i > 0 && _r.Chance(0.6));
            if (seg.Attentive && _r.Chance(0.05)) s.Status = ServiceStatuses.Suspended;
            InitialTransactions.Add(Records.Transaction(W.NextTransactionId++, s.Id, cost, true, created, "order"));
            spent += cost;
            var last = term == "hour" ? expires.AddDays(-1) : SubtractTerm(expires, term);
            if (last > created)
            {
                InitialTransactions.Add(Records.Transaction(W.NextTransactionId++, s.Id, cost, true, last, "prolong"));
                spent += cost;
            }
            Schedule(expires, Kinds.Due, ag.AccountId, s.Id, s.ExpiresAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var monthly = MonthlyCost(ag, t0);
        // «Забытый сервер» живёт на предоплате, пока она не кончится; остальные держат запас по привычке
        acc.Balance = RoundEur(monthly * (!seg.Attentive && !seg.Abuser ? _r.Uniform(2, 18) : ag.Habit == "last_moment" ? _r.Uniform(0, 0.7) : _r.Uniform(0.3, 2.2)));
        acc.TotalReplenished = acc.Balance + spent + RoundEur(_r.Uniform(0, 50) * 100);
        if (ag.Channel == "promo" && createdAt > t0.AddDays(-60))
        {
            acc.BonusBalance = RoundEur(_r.Uniform(3, 10) * 100);
            acc.BonusState = "unlocked";
            ag.PromoUntil = Iso.Sec(createdAt.AddDays(_r.Uniform(30, 90)));
            if (ag.PromoUntil > t0) Schedule(ag.PromoUntil.Value, Kinds.BonusExpire, ag.AccountId);
            else
            {
                acc.BonusBalance = 0;
                acc.BonusState = "locked";
            }
        }
        if (seg.Name is SegmentNames.Stable or SegmentNames.Growing && _r.Chance(0.1)) acc.WithdrawBalance = RoundEur(_r.Uniform(5, 50) * 100);
        if (seg.Abuser)
        {
            Schedule(t0.AddDays(_r.Uniform(1, 30)), Kinds.Block, ag.AccountId, ag.ServiceIds[0]);
            ag.AbuseScheduled = true;
        }
    }

    private ServiceRec NewService(Agent ag, string plan, string term, long price, DateTimeOffset createdAt, DateTimeOffset expiresAt, bool shortLived)
    {
        var seg = _segments[ag.Segment];
        var id = W.NextServiceId++;
        var s = new ServiceRec
        {
            // Почасовые тарифицируются автоматически с баланса — без автопродления их не бывает
            Id = id, Name = $"service-{id:00000}", OwnerId = ag.AccountId, PaymentTerm = term, AutoProlong = term == "hour" || _r.Chance(seg.AutoProlong),
            CreatedAt = createdAt, ExpiresAt = expiresAt, Status = ServiceStatuses.Active, Price = price, Plan = plan,
            Location = _r.Pick(C.Locations), Os = _r.Pick(Oses), ShortLived = shortLived, DiskUsedPct = _r.Uniform(5, 40),
        };
        W.Services[id] = s;
        ag.ServiceIds.Add(id);
        return s;
    }

    private void Import(JsonObject data, DateTimeOffset now)
    {
        static long L(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
        static string Str(JsonNode? n, string fallback) => n is JsonValue v && v.TryGetValue<string>(out var x) ? x : fallback;
        static DateTimeOffset? D(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var x) ? Iso.Sec(Iso.Parse(x)) : null;

        foreach (var a in (data["accounts"]?.AsArray() ?? []).OfType<JsonObject>().OrderBy(a => L(a["id"])))
        {
            var id = L(a["id"]);
            W.Accounts[id] = new AccountRec
            {
                Id = id, Email = Str(a["email"], $"user-{id:00000}@example.test"), Balance = L(a["balance"]), WithdrawBalance = L(a["withdrawBalance"]),
                TotalReplenished = L(a["totalReplenished"]), BonusBalance = L(a["bonusBalance"]), BonusState = Str(a["bonusState"], "locked"),
            };
        }
        var services = (data["services"]?.AsArray() ?? []).OfType<JsonObject>().OrderBy(s => L(s["id"])).ToList();
        foreach (var acc in W.Accounts.Values.OrderBy(a => a.Id))
        {
            var own = services.Where(s => L(s["ownerId"]) == acc.Id).ToList();
            var terms = own.Select(s => Str(s["paymentTerm"], "month")).ToList();
            var seg = terms.Any(t => t is "hour" or "week") && _r.Chance(0.7) ? _segments.GetValueOrDefault(SegmentNames.Developer) : null;
            seg ??= PickSegment();
            if (seg.Abuser) seg = _segments.GetValueOrDefault(SegmentNames.PriceSensitive) ?? seg; // набор не даёт оснований считать клиента нарушителем
            var created = own.Select(s => D(s["createdAt"])).OfType<DateTimeOffset>().DefaultIfEmpty(now.AddDays(-30)).Min();
            NewAgent(acc.Id, seg, created, existing: true);
        }
        foreach (var s in services)
        {
            var owner = L(s["ownerId"]);
            if (!W.Agents.TryGetValue(owner, out var ag)) continue; // без аккаунта: источник такой услуги не обслуживает
            var id = L(s["id"]);
            var term = Str(s["paymentTerm"], "month");
            var price = L(s["price"]);
            var rec = new ServiceRec
            {
                Id = id, Name = Str(s["name"], $"service-{id:00000}"), OwnerId = owner, PaymentTerm = term,
                AutoProlong = s["autoProlong"] is JsonValue ap && ap.TryGetValue<bool>(out var b) && b,
                CreatedAt = D(s["createdAt"]) ?? now, ExpiresAt = D(s["expiresAt"]) ?? now, Status = Str(s["status"], ServiceStatuses.Active),
                Price = price, Plan = NearestPlan(term, price), Location = _r.Pick(C.Locations), Os = _r.Pick(Oses), DiskUsedPct = _r.Uniform(5, 40),
            };
            W.Services[id] = rec;
            ag.ServiceIds.Add(id);
            var ticks = rec.ExpiresAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (rec.Status is ServiceStatuses.Active or ServiceStatuses.Suspended) Schedule(Max(rec.ExpiresAt, now), Kinds.Due, owner, id, ticks);
            else if (rec.Status == ServiceStatuses.ProlongWait) Schedule(Max(rec.ExpiresAt.AddDays(C.GraceDays), now), Kinds.Grace, owner, id, ticks);
        }
        foreach (var ag in W.Agents.Values.Where(a => a.Segment != SegmentNames.Developer && a.ServiceIds.All(id => !W.Services[id].Live))) ag.Gone = true;
        W.NextAccountId = W.Accounts.Keys.DefaultIfEmpty(0).Max() + 1;
        W.NextServiceId = Math.Max(services.Select(s => L(s["id"])).DefaultIfEmpty(0).Max(), W.Services.Keys.DefaultIfEmpty(0).Max()) + 1;
        W.NextTransactionId = (data["transactions"]?.AsArray() ?? []).OfType<JsonObject>().Select(t => L(t["id"])).DefaultIfEmpty(0).Max() + 1;
    }

    private string NearestPlan(string term, long price)
    {
        var monthly = term switch
        {
            "hour" => price * 720 / 1.15,
            "week" => price / 0.27,
            "quarter_year" => price / (3 * 0.95),
            "half_year" => price / (6 * 0.9),
            "year" => price / (12 * 0.85),
            _ => price,
        };
        return C.Plans.OrderBy(p => Math.Abs(p.MonthPrice - monthly)).First().Name;
    }

    // ---------- Деньги и сроки ----------

    /// <summary>Цена периода по базовой цене месяца тарифа (действующей на момент)</summary>
    private long TermPrice(string plan, string term, DateTimeOffset now)
    {
        var month = W.PlanPrices.TryGetValue(plan, out var p) ? p.At(now) : _plans[plan].MonthPrice;
        return term switch
        {
            "hour" => Math.Max(1, (long)Math.Ceiling(month * 1.15 / 720)),
            "week" => (long)Math.Round(month * 0.27),
            "quarter_year" => (long)Math.Round(month * 3 * 0.95),
            "half_year" => (long)Math.Round(month * 6 * 0.9),
            "year" => (long)Math.Round(month * 12 * 0.85),
            _ => month,
        };
    }

    /// <summary>Списание за период; почасовые тарифицируются агрегированно — за сутки вперёд</summary>
    private static long Cost(string term, long price) => term == "hour" ? price * 24 : price;

    private static DateTimeOffset AddTerm(DateTimeOffset d, string term) => term switch
    {
        "hour" => d.AddDays(1),
        "week" => d.AddDays(7),
        "quarter_year" => d.AddMonths(3),
        "half_year" => d.AddMonths(6),
        "year" => d.AddYears(1),
        _ => d.AddMonths(1),
    };

    private static DateTimeOffset SubtractTerm(DateTimeOffset d, string term) => term switch
    {
        "hour" => d.AddDays(-1),
        "week" => d.AddDays(-7),
        "quarter_year" => d.AddMonths(-3),
        "half_year" => d.AddMonths(-6),
        "year" => d.AddYears(-1),
        _ => d.AddMonths(-1),
    };

    private long MonthlyCost(Agent ag, DateTimeOffset now) => ag.ServiceIds.Select(id => W.Services[id]).Where(s => s.Live)
        .Sum(s => TermPrice(s.Plan, "month", now));

    private static long RoundEur(double minor) => Math.Max(0, (long)Math.Round(minor / 100) * 100);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>Списать: сначала разблокированные бонусы, затем баланс</summary>
    private static (long FromBalance, long FromBonus) Charge(AccountRec acc, long cost)
    {
        var fromBonus = acc.BonusState == "unlocked" ? Math.Min(acc.BonusBalance, cost) : 0;
        acc.BonusBalance -= fromBonus;
        acc.Balance -= cost - fromBonus;
        return (cost - fromBonus, fromBonus);
    }

    private IEnumerable<ServiceRec> LiveServices(Agent ag) => ag.ServiceIds.Select(id => W.Services[id]).Where(s => s.Live);

    private double Sigmoid(double x) => 1 / (1 + Math.Exp(-x));
}

/// <summary>Виды запланированных действий</summary>
public static class Kinds
{
    public const string Day = "day";
    public const string Register = "register";
    public const string Order = "order";
    public const string Due = "due";
    public const string Retry = "retry";
    public const string Grace = "grace";
    public const string Pay = "pay";
    public const string TopUp = "topup";
    public const string Login = "login";
    public const string Usage = "usage";
    public const string Incident = "incident";
    public const string Price = "price";
    public const string TicketOpen = "ticket_open";
    public const string TicketReply = "ticket_reply";
    public const string TicketClose = "ticket_close";
    public const string TicketCheck = "ticket_check";
    public const string Nps = "nps";
    public const string AutoProlong = "autoprolong";
    public const string Suspend = "suspend";
    public const string Resume = "resume";
    public const string Plan = "plan";
    public const string Term = "term";
    public const string Backup = "backup";
    public const string Reinstall = "reinstall";
    public const string Withdraw = "withdraw";
    public const string BonusExpire = "bonus_expire";
    public const string BonusGrant = "bonus_grant";
    public const string Reminder = "reminder";
    public const string NotificationOpen = "notification_open";
    public const string Block = "block";
    public const string AdminDelete = "admin_delete";
    public const string UserDelete = "user_delete";
    public const string Decide = "decide";
    public const string Offer = "offer";
    public const string OfferRedeem = "offer_redeem";
    public const string Verify = "verify";
    public const string Security = "security";
    public const string Close = "close";
    public const string Scenario = "scenario";
}
