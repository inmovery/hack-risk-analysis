using Aeza.Risk.Core.Entities;

namespace Aeza.Risk.Core.Generations;

/// <summary>Поколение, из которого строится ответ, и состояние очереди пересчёта</summary>
/// <param name="Generation">Запись поколения</param>
/// <param name="Info">Идентификаторы поколения для ответа API</param>
/// <param name="Pending">Ожидающий/выполняющийся пересчёт</param>
/// <param name="LastFailure">Последний окончательно неудачный пересчёт (если он новее последнего успешного)</param>
public sealed record GenerationContext(GenerationEntity Generation, GenerationInfo Info, PendingInfo? Pending, LastFailureInfo? LastFailure);

/// <summary>Причина, по которой поколение не найдено</summary>
public enum GenerationLookupError
{
    /// <summary>Поколение найдено</summary>
    None,

    /// <summary>Расчёт ещё не выполнен</summary>
    NoGeneration,

    /// <summary>Запрошенное поколение удалено по retention</summary>
    Gone,

    /// <summary>Активное поколение недоступно</summary>
    ActiveMissing,
}

/// <summary>Результат поиска поколения</summary>
public sealed record GenerationLookup(GenerationContext? Context, GenerationLookupError Error);

/// <summary>Правила публикации поколения</summary>
public static class GenerationPublishing
{
    /// <summary>Старый job не может перезаписать поколение с более новыми входами</summary>
    public static bool ShouldPublish(GenerationEntity? active, GenerationEntity candidate)
    {
        if (active is null) return true;
        if (candidate.SourceRevision < active.SourceRevision || candidate.SourceSeq < active.SourceSeq || candidate.ModelRevisionId < active.ModelRevisionId
            || candidate.ActivitySeq < active.ActivitySeq || candidate.MlRevision < active.MlRevision) return false;
        if (candidate.SourceRevision > active.SourceRevision || candidate.SourceSeq > active.SourceSeq || candidate.ModelRevisionId > active.ModelRevisionId
            || candidate.ActivitySeq > active.ActivitySeq || candidate.MlRevision > active.MlRevision) return true;
        return candidate.AsOf >= active.AsOf;
    }

    /// <summary>Неизменяемая сводка поколения из результата движка (её читают и API, и /metrics)</summary>
    public static GenerationSummary BuildSummary(EngineResult r)
    {
        var s = r.Summary;
        var top = r.Customers.Where(c => c.Score is not null && c.Money.AtRiskMinor > 0)
            .OrderByDescending(c => c.Money.AtRiskMinor).ThenByDescending(c => c.Score).ThenBy(c => c.Account.Id).Take(10)
            .Select(c => new TopCustomerDto(c.Account.Id, c.Account.Email, c.Score, c.Level.ToApi(), c.Money.AtRiskMinor, c.PrimaryReason.Text)).ToList();
        return new GenerationSummary(
            new SummaryCustomersDto(s.CustomersTotal, s.ByLevel, s.ByState, s.ScoredCustomers, s.ScoreSum),
            new SummaryServicesDto(s.ServicesTotal, s.ServicesRenewable, s.ServicesScored, s.ServicesUnscored, s.ServicesUnschedulable, s.ServicesByStatus),
            new SummaryMoneyDto(s.HorizonDays, s.PlannedRenewalsMinor, s.UnfundedMinor, s.AtRiskMinor, s.UnscoredPlannedMinor, s.UnfundedUnknownPlannedMinor,
                s.MoneyByLevel.ToDictionary(k => k.Key, k => new LevelMoneyDto(k.Value.Customers, k.Value.PlannedRenewalsMinor, k.Value.UnfundedMinor, k.Value.AtRiskMinor)),
                s.AtRiskInterval),
            s.QualityFlags, top,
            new ChurnSummaryDto(
                new FullChurnSummaryDto(s.FullChurnByLevel, s.FullChurnByState),
                new PaymentTrendSummaryDto(s.PaymentTrendByState, r.Customers.FirstOrDefault()?.PaymentTrend.WindowDays ?? 0)));
    }
}
