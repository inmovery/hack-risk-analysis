using System.Text.Json.Serialization;

namespace Aeza.Risk.SourceSimulator;

/// <summary>Аккаунт в формате тестовых данных (деньги в еврокопейках)</summary>
public sealed class AccountRec
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public long Balance { get; set; }
    public long WithdrawBalance { get; set; }
    public long TotalReplenished { get; set; }
    public long BonusBalance { get; set; }
    public string BonusState { get; set; } = "locked";

    /// <summary>Деньги, которыми источник оплачивает продление: баланс + бонусы, если они разблокированы</summary>
    [JsonIgnore]
    public long Funds => Balance + (BonusState == "unlocked" ? BonusBalance : 0);
}

/// <summary>Услуга: поля тестовых данных + то, что знает только источник (тариф, локация)</summary>
public sealed class ServiceRec
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long OwnerId { get; set; }
    public string PaymentTerm { get; set; } = "month";
    public bool AutoProlong { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Status { get; set; } = ServiceStatuses.Active;
    public long Price { get; set; }
    public string Plan { get; set; } = "";
    public string Location { get; set; } = "";
    public string Os { get; set; } = "";
    /// <summary>Сервер-эксперимент: создан на время (экспериментатор удаляет такие сам)</summary>
    public bool ShortLived { get; set; }
    public double DiskUsedPct { get; set; }
    /// <summary>Минуты простоя из-за аварий по дням (для uptime в телеметрии)</summary>
    public Dictionary<string, int> DowntimeByDay { get; set; } = new();
    public int PaymentAttempts { get; set; }

    [JsonIgnore]
    public bool Live => Status != ServiceStatuses.Deleted;
}

public static class ServiceStatuses
{
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string ProlongWait = "prolong_wait";
    public const string Blocked = "blocked";
    public const string Deleted = "deleted";
}

/// <summary>Намерение уйти: зреет от начала до решения</summary>
public sealed class Intent
{
    public string Cause { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    /// <summary>Номер намерения клиента: запланированные сигналы относятся только к своему намерению</summary>
    public int No { get; set; }
    /// <summary>Уход без предупреждения: сигналы перед решением не подаются</summary>
    public bool Silent { get; set; }
    public bool Decided { get; set; }
    /// <summary>Автопродление выключено из-за намерения (при отмене — включить обратно)</summary>
    public bool TurnedOffAutoProlong { get; set; }

    public double Progress(DateTimeOffset now) =>
        Decided ? 1 : Math.Clamp((now - StartedAt).TotalSeconds / Math.Max(1, (DueAt - StartedAt).TotalSeconds), 0, 1);
}

/// <summary>Клиент-агент: постоянный профиль и скрытое состояние (в анализатор не уходит)</summary>
public sealed class Agent
{
    public long AccountId { get; set; }
    public string Segment { get; set; } = "";
    public string Channel { get; set; } = "organic";
    public string Country { get; set; } = "DE";
    public string CustomerType { get; set; } = "individual";
    public string Habit { get; set; } = "manual";
    public double PriceSensitivity { get; set; }
    public double Tech { get; set; }
    public double LoginRate { get; set; }
    public double CpuBase { get; set; }
    public bool TwoFactor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Удовлетворённость s, потребность e, финансы f и их базовые уровни</summary>
    public double S { get; set; }
    public double E { get; set; }
    public double F { get; set; }
    public double S0 { get; set; }
    public double E0 { get; set; }
    public double F0 { get; set; }
    /// <summary>Проект затухает: e убывает с этой скоростью в день</summary>
    public double DecayRate { get; set; }

    public DateTimeOffset? PromoUntil { get; set; }
    public DateTimeOffset? PromoEndedAt { get; set; }
    public DateTimeOffset? PriceRiseAt { get; set; }
    public DateTimeOffset? VacationUntil { get; set; }
    public DateTimeOffset? LastNpsAt { get; set; }
    public Intent? Intent { get; set; }
    /// <summary>Ушёл: больше не входит, не платит, не заказывает</summary>
    public bool Gone { get; set; }
    public bool AbuseScheduled { get; set; }
    /// <summary>Сколько намерений уйти уже было</summary>
    public int IntentCount { get; set; }

    /// <summary>Номер последнего события аккаунта (accountSeq)</summary>
    public long Seq { get; set; }
    public List<long> ServiceIds { get; set; } = [];
}

public sealed class Ticket
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string Category { get; set; } = "";
    public string Priority { get; set; } = "normal";
    public DateTimeOffset OpenedAt { get; set; }
    public bool Closed { get; set; }
    public bool Penalized { get; set; }
    public bool Unsolved { get; set; }
}

/// <summary>Запланированное действие. Действия выполняются строго по времени: событие несёт состояние на свой момент</summary>
public sealed class AgendaItem
{
    public DateTimeOffset At { get; set; }
    public long Seq { get; set; }
    public string Kind { get; set; } = "";
    public long AccountId { get; set; }
    public long ServiceId { get; set; }
    public string? Arg { get; set; }
    public double Num { get; set; }
}

/// <summary>Весь мир источника: записи, агенты, очередь действий, счётчики и состояние генератора</summary>
public sealed class World
{
    public int FormatVersion { get; set; } = 1;
    public string RunId { get; set; } = "";
    public ulong Seed { get; set; }
    public ulong[] Rng { get; set; } = [];
    /// <summary>Начало истории (T₀) или момент инициализации</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>До какого момента мир смоделирован (все события до него сгенерированы)</summary>
    public DateTimeOffset Clock { get; set; }
    public long NextAccountId { get; set; } = 1;
    public long NextServiceId { get; set; } = 1;
    public long NextTransactionId { get; set; } = 1;
    public long NextEventNo { get; set; } = 1;
    public long NextTicketId { get; set; } = 1;
    public long NextIncidentId { get; set; } = 1;
    public long NextOfferId { get; set; } = 1;
    public long NextAgendaSeq { get; set; } = 1;
    public Dictionary<long, AccountRec> Accounts { get; set; } = new();
    public Dictionary<long, ServiceRec> Services { get; set; } = new();
    public Dictionary<long, Agent> Agents { get; set; } = new();
    public Dictionary<long, Ticket> Tickets { get; set; } = new();
    /// <summary>Действующие базовые цены месяца по тарифам и с какого момента</summary>
    public Dictionary<string, PlanPrice> PlanPrices { get; set; } = new();
    public List<AgendaItem> Agenda { get; set; } = [];
    /// <summary>Конец сгенерированной истории: дальше — live (регистрации идут потоком)</summary>
    public DateTimeOffset HistoryEnd { get; set; }
    /// <summary>Параметры модели, с которыми создан мир (продолжение — с ними же)</summary>
    public SimConfig? Config { get; set; }
    /// <summary>Длина outbox после последнего сохранения (live): события дальше будут сгенерированы заново теми же</summary>
    public long OutboxLength { get; set; }
}

public sealed class PlanPrice
{
    public long Current { get; set; }
    public long Next { get; set; }
    public DateTimeOffset? NextFrom { get; set; }

    public long At(DateTimeOffset now) => NextFrom is { } from && now >= from ? Next : Current;
}
