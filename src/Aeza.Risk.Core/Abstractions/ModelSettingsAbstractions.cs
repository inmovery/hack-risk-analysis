using Aeza.Risk.Core.Entities;

namespace Aeza.Risk.Core.Abstractions;

/// <summary>Состояние настроек: последняя ревизия, активное поколение, идёт ли пересчёт</summary>
public sealed record ModelStateView(ModelRevisionEntity Latest, GenerationEntity? Active, bool Pending);

/// <summary>Решение по изменению настроек, принятое бизнес-логикой над текущей ревизией</summary>
public abstract record ModelChangeDecision
{
    /// <summary>Изменения не прошли проверку</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : ModelChangeDecision;

    /// <summary>Итоговые настройки совпадают с текущими</summary>
    public sealed record Unchanged : ModelChangeDecision;

    /// <summary>Новая ревизия</summary>
    public sealed record Change(ModelSettings Merged, string? Comment) : ModelChangeDecision;
}

/// <summary>Итог добавления ревизии</summary>
public enum ModelAppendOutcome
{
    /// <summary>baseRevision устарел</summary>
    Conflict,

    /// <summary>Настройки не прошли проверку</summary>
    Invalid,

    /// <summary>Нечего сохранять</summary>
    Unchanged,

    /// <summary>Ревизия и задание пересчёта сохранены</summary>
    Accepted,
}

/// <summary>Результат добавления ревизии</summary>
public sealed record ModelAppendResult(
    ModelAppendOutcome Outcome, ModelRevisionEntity Latest, long? NewRevision, long? JobId, IReadOnlyDictionary<string, string[]>? Errors);

/// <summary>Ревизии настроек модели</summary>
public interface IModelSettingsRepository
{
    /// <summary>Последняя ревизия, активное поколение и признак пересчёта</summary>
    Task<ModelStateView> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Последние ревизии (аудит)</summary>
    Task<IReadOnlyList<ModelRevisionEntity>> ListRevisionsAsync(int take, CancellationToken cancellationToken);

    /// <summary>Атомарно: блокировка состояния, проверка baseRevision, решение бизнес-логики над текущей ревизией, новая неизменяемая ревизия + durable job + аудит в одной транзакции</summary>
    Task<ModelAppendResult> AppendAsync(long baseRevision, Func<ModelSettings, ModelChangeDecision> decide, string actor, CancellationToken cancellationToken);
}
