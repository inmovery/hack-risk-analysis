namespace Aeza.Risk.Core;

/// <summary>Состояние индекса «уход клиента целиком» (стабильные строки API)</summary>
public static class FullChurnStates
{
    public const string Scored = "scored";
    public const string Partial = "partial";
    public const string Churned = "churned";
    public const string Blocked = "blocked";
    public const string NoServices = "no_services";
    public const string InsufficientData = "insufficient_data";

    public static readonly string[] All = [Scored, Partial, Churned, Blocked, NoServices, InsufficientData];
}

/// <summary>Состояние тренда платежей (стабильные строки API)</summary>
public static class PaymentTrendStates
{
    public const string Decline = "decline";
    public const string Stable = "stable";
    public const string Growth = "growth";
    public const string NoPayments = "no_payments";
    public const string InsufficientHistory = "insufficient_history";

    public static readonly string[] All = [Decline, Stable, Growth, NoPayments, InsufficientHistory];
}

/// <summary>Индекс «уход клиента целиком»: минимум score по действующим услугам</summary>
/// <param name="Score">Индекс 0..100 или null</param>
/// <param name="Level">Уровень по тем же порогам, что и score</param>
/// <param name="State">См. <see cref="FullChurnStates"/></param>
/// <param name="WeakestServiceId">Самая устойчивая услуга (минимальный score) — она определяет индекс</param>
/// <param name="Text">Объяснение простыми словами</param>
public sealed record FullChurnIndicator(decimal? Score, RiskLevel Level, string State, long? WeakestServiceId, string Text);

/// <summary>Тренд выполненных платежей: последние W дней против предыдущих W</summary>
/// <param name="State">См. <see cref="PaymentTrendStates"/></param>
/// <param name="ChangePct">Изменение, % (1 знак) или null</param>
/// <param name="CurrentMinor">Сумма за последние W дней, еврокопейки</param>
/// <param name="PreviousMinor">Сумма за предыдущие W дней, еврокопейки</param>
/// <param name="WindowDays">W</param>
/// <param name="HistoryStart">Начало известной истории клиента</param>
/// <param name="Text">Объяснение простыми словами</param>
public sealed record PaymentTrendIndicator(
    string State, decimal? ChangePct, long CurrentMinor, long PreviousMinor, int WindowDays, DateTimeOffset? HistoryStart, string Text);

/// <summary>Индикаторы двух типов оттока помимо непродления</summary>
public static class ChurnIndicators
{
    /// <summary>Клиент уходит целиком, только если уходят все его действующие услуги: индекс = min score (конъюнкция индексов)</summary>
    public static FullChurnIndicator FullChurn(IReadOnlyList<ServiceAssessment> services, ModelSettings settings)
    {
        if (services.Count == 0)
            return new FullChurnIndicator(null, RiskLevel.Unknown, FullChurnStates.NoServices, null, "Услуг нет — уходить нечем");

        var statuses = services.Select(s => SourceEnums.ParseStatus(s.Status)).ToList();
        var renewable = services.Where(s => s.Renewable).ToList();
        var unknownStatus = statuses.Count(st => st == ServiceStatus.Unknown);
        if (renewable.Count == 0 && unknownStatus == 0)
        {
            if (statuses.All(st => st == ServiceStatus.Deleted))
                return new FullChurnIndicator(null, RiskLevel.Unknown, FullChurnStates.Churned, null,
                    $"Уже ушёл: все {services.Count} услуг удалены — кандидат на возврат");
            return new FullChurnIndicator(null, RiskLevel.Unknown, FullChurnStates.Blocked, null,
                "Действующих услуг нет: остались только заблокированные — продление невозможно");
        }

        var scored = renewable.Where(s => s.Scored && s.Score is not null).ToList();
        if (scored.Count == 0)
            return new FullChurnIndicator(null, RiskLevel.Unknown, FullChurnStates.InsufficientData, null,
                "Нет оценённых действующих услуг — индекс неизвестен");

        var weakest = scored.OrderBy(s => s.Score!.Value).ThenBy(s => s.Id).First();
        var min = weakest.Score!.Value;
        var partial = scored.Count < renewable.Count + unknownStatus;
        var n = renewable.Count;
        var text = n == 1
            ? $"Единственная действующая услуга {weakest.Name}: индекс равен её score ({Fmt.Num(min)})"
            : $"Уйдёт целиком, только если уйдут все {n} действующие услуги; самая устойчивая — {weakest.Name} ({Fmt.Num(min)})";
        if (partial) text += $"; оценены {scored.Count} из {renewable.Count + unknownStatus} — индекс не выше {Fmt.Num(min)}";
        return new FullChurnIndicator(min, settings.LevelOf(min), partial ? FullChurnStates.Partial : FullChurnStates.Scored, weakest.Id, text);
    }

    /// <summary>Выполненные платежи клиента: окно (asOf − W, asOf] против (asOf − 2W, asOf − W]; короткая история — «мало истории», не 0</summary>
    public static PaymentTrendIndicator PaymentTrend(
        IReadOnlyList<ServiceInput> services, IReadOnlyList<TransactionInput> transactions, DateTimeOffset asOf, ChurnSettings churn)
    {
        var w = churn.PaymentWindowDays;
        var window = TimeSpan.FromDays(w);
        var currentFrom = asOf - window;
        var previousFrom = asOf - window - window;

        DateTimeOffset? start = null;
        foreach (var d in services.Select(s => s.CreatedAt).Concat(transactions.Select(t => t.CreatedAt)))
            if (d is { } v && (start is null || v < start)) start = v;

        long current = 0, previous = 0;
        foreach (var t in transactions)
        {
            if (SourceEnums.ParseTransactionStatus(t.Status) != TransactionStatus.Performed || t.Amount is not { } amount) continue;
            if ((t.PerformedAt ?? t.CreatedAt) is not { } at) continue;
            if (at > currentFrom && at <= asOf) current = checked(current + amount);
            else if (at > previousFrom && at <= currentFrom) previous = checked(previous + amount);
        }

        if (start is null || start > previousFrom)
        {
            var since = start is { } s0 ? $"клиент с {Fmt.Date(s0)}" : "дат активности нет";
            return new PaymentTrendIndicator(PaymentTrendStates.InsufficientHistory, null, current, previous, w, start,
                $"Мало истории для сравнения двух окон по {w} дн.: {since}");
        }
        if (previous == 0 && current == 0)
            return new PaymentTrendIndicator(PaymentTrendStates.NoPayments, null, 0, 0, w, start,
                $"Выполненных платежей нет ни за последние {w} дн., ни за предыдущие {w} дн.");
        if (previous == 0)
            return new PaymentTrendIndicator(PaymentTrendStates.Growth, null, current, 0, w, start,
                $"Новые платежи: {Fmt.Eur(current)} за {w} дн. при нуле в предыдущие {w} дн.");

        var change = Math.Round((current - previous) * 100m / previous, 1, MidpointRounding.ToEven);
        var state = change <= -churn.PaymentDeclinePct ? PaymentTrendStates.Decline
            : change >= churn.PaymentDeclinePct ? PaymentTrendStates.Growth
            : PaymentTrendStates.Stable;
        var sign = change > 0 ? "+" : "";
        return new PaymentTrendIndicator(state, change, current, previous, w, start,
            $"Платежи за {w} дн.: {Fmt.Eur(current)} против {Fmt.Eur(previous)} в предыдущие {w} дн. ({sign}{Fmt.Num(change)} %)");
    }
}
