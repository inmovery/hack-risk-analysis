using System.Text.Json;

namespace Aeza.Risk.Core.Simulation;

/// <summary>Вариант значения поля</summary>
public sealed record ComposerOption(string Value, string Label);

/// <summary>Поле события в конструкторе</summary>
/// <param name="Name">Имя в data события</param>
/// <param name="Label">Подпись</param>
/// <param name="Kind">money — евро (в событие уходят еврокопейки), number, enum, bool</param>
/// <param name="Options">Варианты для enum</param>
/// <param name="Min">Минимум (money — евро)</param>
/// <param name="Max">Максимум (money — евро)</param>
/// <param name="Default">Значение по умолчанию</param>
public sealed record ComposerField(
    string Name, string Label, string Kind, IReadOnlyList<ComposerOption>? Options = null, double? Min = null, double? Max = null, JsonElement? Default = null);

/// <summary>Тип события, который можно собрать в панели</summary>
/// <param name="Type">Тип доменного события</param>
/// <param name="Title">Название</param>
/// <param name="Description">Что произойдёт в snapshot системы</param>
/// <param name="NeedsService">Событие относится к услуге клиента</param>
/// <param name="Fields">Поля data</param>
public sealed record ComposerEventType(string Type, string Title, string Description, bool NeedsService, IReadOnlyList<ComposerField> Fields);

/// <summary>Событие, собранное в панели: его применяет симулятор к snapshot системы и отправляет в общий поток со следующим номером аккаунта</summary>
/// <param name="Id">Номер</param>
/// <param name="CreatedAt">Создано</param>
/// <param name="CreatedBy">Кем</param>
/// <param name="Type">Тип события</param>
/// <param name="AccountId">Клиент</param>
/// <param name="ServiceId">Услуга</param>
/// <param name="Data">Поля события</param>
/// <param name="Status">pending — ждёт симулятор, applied — в потоке, rejected — не применено</param>
/// <param name="EventIds">Идентификаторы отправленных событий</param>
/// <param name="Message">Почему не применено</param>
/// <param name="UpdatedAt">Последнее изменение статуса</param>
public sealed record SimulationCommandView(
    long Id, DateTimeOffset CreatedAt, string CreatedBy, string Type, long AccountId, long? ServiceId, JsonElement Data, string Status,
    IReadOnlyList<string> EventIds, string? Message, DateTimeOffset? UpdatedAt);

/// <summary>Событие для симулятора</summary>
public sealed record SimulationAgentCommand(long Id, string Type, long AccountId, long? ServiceId, JsonElement Data);

/// <summary>Итог события у симулятора</summary>
/// <param name="Id">Номер</param>
/// <param name="Status">applied | rejected</param>
/// <param name="EventIds">Отправленные события</param>
/// <param name="Message">Почему не применено</param>
public sealed record SimulationCommandResult(long Id, string Status, IReadOnlyList<string>? EventIds, string? Message);

/// <summary>Каталог событий конструктора и проверка полей</summary>
public static class EventComposer
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Rejected = "rejected";

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    public static readonly IReadOnlyList<ComposerEventType> Types =
    [
        new("account.login", "Вход в панель", "Клиент входит в панель управления.", false, []),
        new("balance.topped_up", "Пополнение баланса", "Баланс клиента растёт на сумму пополнения.", false,
            [new("amount", "Сумма, €", "money", Min: 1, Max: 10_000, Default: J(10))]),
        new("service.autoprolong_changed", "Автопродление", "Клиент включает или выключает автопродление услуги.", true,
            [new("enabled", "Автопродление включено", "bool", Default: J(false))]),
        new("service.suspended", "Выключить сервер", "Услуга переходит в suspended и продолжает тарифицироваться.", true, []),
        new("service.resumed", "Включить сервер", "Выключенная услуга снова работает.", true, []),
        new("service.plan_changed", "Смена тарифа", "Тариф услуги на ступень выше или ниже.", true,
            [new("direction", "Направление", "enum", [new("down", "Понизить"), new("up", "Повысить")], Default: J("down"))]),
        new("service.deleted", "Удалить услугу", "Клиент удаляет услугу; без услуг клиент уходит.", true, []),
        new("ticket.opened", "Обращение в поддержку", "Клиент открывает обращение.", false,
            [new("category", "Категория", "enum", [new("technical", "Техническая проблема"), new("billing", "Оплата")], Default: J("technical"))]),
        new("nps.responded", "Оценка NPS", "Клиент отвечает на опрос NPS.", false,
            [new("score", "Оценка 0–10", "number", Min: 0, Max: 10, Default: J(3))]),
        new("service.backup_downloaded", "Выгрузка бэкапа", "Клиент забирает данные сервера — сигнал возможного переезда.", true, []),
        new("dns.records_changed", "Изменение DNS", "Клиент меняет DNS-записи; «к другому провайдеру» — сигнал переезда.", false,
            [new("target", "Куда", "enum", [new("internal", "Внутри Aéza"), new("external", "К другому провайдеру")], Default: J("external"))]),
        new("retention.offer_sent", "Удерживающее предложение", "Клиенту отправляется предложение остаться.", false,
            [new("kind", "Предложение", "enum",
                [new("discount", "Скидка 20 %"), new("bonus", "Бонус на баланс"), new("term_upgrade", "Длиннее срок по цене короткого"), new("support_call", "Звонок поддержки")],
                Default: J("discount"))]),
    ];

    private static readonly Dictionary<string, ComposerEventType> ByType = Types.ToDictionary(t => t.Type, StringComparer.Ordinal);

    public static ComposerEventType? Find(string? type) => type is null ? null : ByType.GetValueOrDefault(type);

    /// <summary>Ошибки по путям полей; пусто — событие собрано верно. Сумма в евро переводится в еврокопейки в <paramref name="normalized"/></summary>
    public static IReadOnlyDictionary<string, string[]> Validate(string? type, long accountId, long? serviceId, JsonElement data, out Dictionary<string, object> normalized)
    {
        normalized = new Dictionary<string, object>();
        var errors = new Dictionary<string, string[]>();
        var t = Find(type);
        if (t is null)
        {
            errors["type"] = ["Выберите тип события из списка."];
            return errors;
        }
        if (accountId <= 0) errors["accountId"] = ["Выберите клиента."];
        if (t.NeedsService && serviceId is not > 0) errors["serviceId"] = ["Выберите услугу клиента."];
        foreach (var f in t.Fields)
        {
            var path = $"data.{f.Name}";
            JsonElement v = default;
            var present = data.ValueKind == JsonValueKind.Object && data.TryGetProperty(f.Name, out v) && v.ValueKind != JsonValueKind.Null;
            if (!present)
            {
                errors[path] = [$"Заполните поле «{f.Label}»."];
                continue;
            }
            switch (f.Kind)
            {
                case "money" or "number":
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var x) || x < (f.Min ?? double.MinValue) || x > (f.Max ?? double.MaxValue))
                        errors[path] = [$"«{f.Label}»: число от {f.Min} до {f.Max}."];
                    else if (f.Kind == "money") normalized[f.Name] = (long)Math.Round((decimal)x * 100, MidpointRounding.AwayFromZero);
                    else if (Math.Abs(x - Math.Round(x)) > 1e-9) errors[path] = [$"«{f.Label}»: целое число."];
                    else normalized[f.Name] = (long)x;
                    break;
                case "bool":
                    if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) errors[path] = [$"«{f.Label}»: да или нет."];
                    else normalized[f.Name] = v.GetBoolean();
                    break;
                case "enum":
                    if (v.ValueKind != JsonValueKind.String || f.Options!.All(o => o.Value != v.GetString()))
                        errors[path] = [$"«{f.Label}»: выберите вариант из списка."];
                    else normalized[f.Name] = v.GetString()!;
                    break;
            }
        }
        return errors;
    }
}
