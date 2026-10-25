namespace Aeza.Risk.Core.Source;

/// <summary>Исходы сроков: стабильные строки API и БД</summary>
public static class ExpiryOutcomes
{
    public const string Renewed = "renewed";
    public const string NotRenewed = "not_renewed";
    public const string Excluded = "excluded";

    public const string ResolutionExtended = "extended";
    public const string ResolutionRenewedLate = "renewed_late";
    public const string ResolutionDeleted = "deleted";
    public const string ResolutionBlocked = "blocked";
    public const string ResolutionExpired = "expired_fresh_source";
}

/// <summary>Действие над таблицей исходов сроков</summary>
public abstract record ExpiryAction(long ServiceId);

/// <summary>Начать наблюдение срока E услуги (если ещё не наблюдается)</summary>
/// <param name="ServiceId">Услуга</param>
/// <param name="OwnerId">Клиент</param>
/// <param name="ExpiresAt">Срок E</param>
/// <param name="ObservedAt">Момент, когда срок впервые увиден</param>
public sealed record RegisterExpiry(long ServiceId, long OwnerId, DateTimeOffset ExpiresAt, DateTimeOffset ObservedAt) : ExpiryAction(ServiceId);

/// <summary>Закрыть открытые сроки услуги (все или только раньше <paramref name="ExpiresBefore"/>)</summary>
/// <param name="ServiceId">Услуга</param>
/// <param name="ExpiresBefore">null — все открытые; иначе только сроки раньше этого момента (срок продлён до него)</param>
/// <param name="Outcome">Исход</param>
/// <param name="Resolution">Как установлен</param>
/// <param name="At">Момент наблюдения</param>
/// <param name="LateOutcome">Исход для сроков, у которых E + N уже прошло к моменту <paramref name="At"/> (продление пришло поздно)</param>
public sealed record ResolveExpiry(long ServiceId, DateTimeOffset? ExpiresBefore, string Outcome, string Resolution, DateTimeOffset At, string? LateOutcome = null)
    : ExpiryAction(ServiceId);

/// <summary>Факт «не продлено в течение N дней после срока»</summary>
public static class ExpiryOutcomeRules
{
    /// <summary>Действия при наблюдении состояния услуги <paramref name="after"/> (прежнее — <paramref name="before"/>)</summary>
    public static IReadOnlyList<ExpiryAction> OnServiceObserved(ServiceInput? before, ServiceInput after, DateTimeOffset observedAt, int graceDays)
    {
        var actions = new List<ExpiryAction>();
        var grace = TimeSpan.FromDays(graceDays);
        var status = SourceEnums.ParseStatus(after.Status);
        switch (status)
        {
            case ServiceStatus.Deleted:
                actions.Add(new ResolveExpiry(after.Id, null, ExpiryOutcomes.NotRenewed, ExpiryOutcomes.ResolutionDeleted, observedAt));
                return actions;
            case ServiceStatus.Blocked:
                actions.Add(new ResolveExpiry(after.Id, null, ExpiryOutcomes.Excluded, ExpiryOutcomes.ResolutionBlocked, observedAt));
                return actions;
        }
        if (after.ExpiresAt is not { } e) return actions;
        if (before?.ExpiresAt is { } prev && e > prev)
            actions.Add(new ResolveExpiry(after.Id, e, ExpiryOutcomes.Renewed, ExpiryOutcomes.ResolutionExtended, observedAt, ExpiryOutcomes.NotRenewed));
        if (SourceEnums.IsRenewable(status) && observedAt <= e + grace)
            actions.Add(new RegisterExpiry(after.Id, after.OwnerId, e, observedAt));
        return actions;
    }

    /// <summary>Созревший срок без продления: «не продлено», только если источник обновлялся после E + N (иначе отсутствие продления неотличимо от отсутствия данных)</summary>
    public static string? ResolveDue(DateTimeOffset expiresAt, ServiceInput? current, DateTimeOffset? sourceUpdatedAt, DateTimeOffset now, int graceDays)
    {
        var due = expiresAt + TimeSpan.FromDays(graceDays);
        if (now < due || sourceUpdatedAt is not { } fresh || fresh < due) return null;
        if (current?.ExpiresAt is { } cur && cur > expiresAt) return ExpiryOutcomes.Renewed;
        return ExpiryOutcomes.NotRenewed;
    }
}
