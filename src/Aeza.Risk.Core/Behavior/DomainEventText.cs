using System.Globalization;
using System.Text.Json;

namespace Aeza.Risk.Core;

/// <summary>Подпись доменного события для ленты карточки клиента: домен и короткий текст из известных полей data (без PII)</summary>
public static class DomainEventText
{
    public const string Account = "account";
    public const string Money = "money";
    public const string Service = "service";
    public const string Usage = "usage";
    public const string Support = "support";
    public const string Retention = "retention";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> Domains = [Account, Money, Service, Usage, Support, Retention, Other];

    public static string DomainOf(string type) => type switch
    {
        _ when type.StartsWith("account.", StringComparison.Ordinal) => Account,
        "usage.daily" or "service.incident" => Usage,
        _ when type.StartsWith("service.", StringComparison.Ordinal) => Service,
        _ when type.StartsWith("balance.", StringComparison.Ordinal) || type.StartsWith("payment.", StringComparison.Ordinal)
               || type.StartsWith("bonus.", StringComparison.Ordinal) || type.StartsWith("withdrawal.", StringComparison.Ordinal)
               || type.StartsWith("renewal.", StringComparison.Ordinal) || type.StartsWith("notification.", StringComparison.Ordinal) => Money,
        _ when type.StartsWith("ticket.", StringComparison.Ordinal) || type.StartsWith("nps.", StringComparison.Ordinal) => Support,
        _ when type.StartsWith("retention.", StringComparison.Ordinal) => Retention,
        _ => Other,
    };

    /// <summary>Типы домена (для фильтра ленты в SQL)</summary>
    public static bool InDomain(string type, string domain) => DomainOf(type) == domain;

    public static string Describe(string type, string dataJson)
    {
        JsonElement d;
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            d = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return type;
        }
        string? S(string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? N(string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) ? x : null;
        bool? B(string n) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        string Eur(string n) => N(n) is { } m ? Fmt.Eur((long)m) : "—";
        string Num(string n, string format = "0.#") => N(n) is { } x ? x.ToString(format, CultureInfo.InvariantCulture) : "—";

        return type switch
        {
            "account.registered" => $"Регистрация · канал {S("channel") ?? "—"}",
            "account.verified" => $"Подтверждение: {S("level") ?? "—"}",
            "account.security_changed" => B("twoFactor") == true ? "Включил двухфакторный вход" : "Изменил настройки безопасности",
            "account.login" => B("success") == false ? $"Неудачный вход ({S("channel") ?? "—"})" : $"Вход ({S("channel") ?? "—"})",
            "account.closed" => $"Аккаунт закрыт: {S("reason") ?? "—"}",
            "balance.topped_up" => $"Пополнение {Eur("amount")}{(B("auto") == true ? " (авто)" : "")}",
            "payment.charged" => $"Списание {Eur("amount")} ({(S("kind") == "order" ? "заказ" : "продление")})",
            "payment.failed" => $"Списание не прошло {Eur("amount")}: {S("reason") ?? "—"}",
            "bonus.granted" => $"Бонус {Eur("amount")}: {S("reason") ?? "—"}",
            "bonus.expired" => $"Бонусы сгорели {Eur("amount")}",
            "withdrawal.requested" => $"Вывод средств {Eur("amount")}",
            "renewal.reminder_sent" => $"Напоминание о продлении за {Num("daysBefore", "0")} дн.",
            "notification.opened" => "Открыл уведомление",
            "service.ordered" => $"Заказ услуги: {S("plan") ?? "—"}, {S("location") ?? "—"}, {S("term") ?? "—"}",
            "service.renewed" => $"Продление {Eur("amount")} ({(S("mode") == "manual" ? "вручную" : "авто")})",
            "service.expired" => $"Срок истёк → prolong_wait ({(S("reason") == "no_funds" ? "не хватило денег" : S("reason") == "autoprolong_off" ? "автопродление выключено" : S("reason") ?? "—")})",
            "service.suspended" => "Сервер выключен",
            "service.resumed" => "Сервер включён",
            "service.blocked" => $"Услуга заблокирована: {S("reason") ?? "—"}",
            "service.unblocked" => "Услуга разблокирована",
            "service.deleted" => $"Услуга удалена: {S("reason") ?? "—"}",
            "service.plan_changed" => $"{(S("direction") == "downgrade" ? "Понижение" : "Повышение")} тарифа: {S("fromPlan") ?? "—"} → {S("toPlan") ?? "—"}",
            "service.term_changed" => $"Срок оплаты: {S("fromTerm") ?? "—"} → {S("toTerm") ?? "—"}",
            "service.autoprolong_changed" => B("enabled") == true ? "Автопродление включено" : "Автопродление выключено",
            "service.reinstalled" => $"Переустановка ОС: {S("os") ?? "—"}",
            "service.backup_created" => $"Резервная копия ({S("kind") ?? "—"})",
            "service.migrated" => $"Перенос: {S("fromLocation") ?? "—"} → {S("toLocation") ?? "—"}",
            "usage.daily" => $"Нагрузка за {S("date") ?? "—"}: CPU {Num("cpuAvgPct")}%, uptime {Num("uptimePct")}%",
            "service.incident" => $"Авария ({S("severity") ?? "—"}): {Num("durationMin", "0")} мин",
            "ticket.opened" => $"Обращение: {S("category") ?? "—"} ({S("priority") ?? "—"})",
            "ticket.first_replied" => $"Ответ поддержки через {Num("minutes", "0")} мин",
            "ticket.closed" => $"Обращение закрыто: {(S("resolution") == "unsolved" ? "не решено" : "решено")}{(N("csat") is { } c ? $", оценка {c:0}" : "")}",
            "nps.responded" => $"NPS: {Num("score", "0")} из 10",
            "retention.offer_sent" => $"Предложение удержания: {S("kind") ?? "—"}",
            "retention.offer_redeemed" => "Принял предложение удержания",
            _ => type,
        };
    }
}
