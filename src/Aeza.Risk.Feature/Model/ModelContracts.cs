using Aeza.Risk.Core;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Settings;
using Aeza.Risk.SharedKernel.Json;

using JetBrains.Annotations;

namespace Aeza.Risk.Feature.Model;

/// <summary>Признак каталога</summary>
/// <param name="Key">Ключ</param>
/// <param name="Title">Название</param>
/// <param name="Description">Формула простыми словами</param>
/// <param name="DefaultWeight">Вес по умолчанию</param>
/// <param name="Behavioral">Признак поведения по событиям v2: без данных не снимает оценку</param>
[PublicAPI]
public sealed record FeatureMeta(string Key, string Title, string Description, decimal DefaultWeight, bool Behavioral);

/// <summary>Ревизия настроек</summary>
[PublicAPI]
public sealed record RevisionDto(long Revision, ModelSettingsDto Settings, DateTimeOffset CreatedAt, string CreatedBy, string? Comment, long? BasedOn);

/// <summary>Ревизия, по которой посчитано опубликованное поколение</summary>
[PublicAPI]
public sealed record ActiveModelDto(long Revision, long GenerationId, DateTimeOffset AsOf);

/// <summary>Границы значений настроек</summary>
[PublicAPI]
public sealed record SettingsLimits(
    int MaxHorizonDays, int MaxUrgencyWindowDays, decimal MinWeight, decimal MaxWeight, int MinTickMinutes, int MaxTickMinutes,
    int MaxNonRenewalDays, int MinPaymentWindowDays, int MaxPaymentWindowDays);

/// <summary>Действующие настройки</summary>
[PublicAPI]
public sealed record ModelSettingsResponse(RevisionDto Latest, ActiveModelDto? Active, IReadOnlyList<FeatureMeta> Catalog, SettingsLimits Limits, bool Pending);

/// <summary>Изменение порогов</summary>
[PublicAPI]
public sealed record ThresholdsPatch(decimal? Medium, decimal? High);

/// <summary>Изменение часов оценки</summary>
[PublicAPI]
public sealed record ClockPatch(string? Mode, int? TickMinutes);

/// <summary>Изменение определений оттока</summary>
/// <param name="NonRenewalDays">N: «не продлено» через N дней после срока (1–90)</param>
/// <param name="PaymentWindowDays">Окно сравнения платежей, дни (7–180)</param>
/// <param name="PaymentDeclinePct">Порог снижения платежей, % (1–100)</param>
[PublicAPI]
public sealed record ChurnPatch(int? NonRenewalDays, int? PaymentWindowDays, decimal? PaymentDeclinePct);

/// <summary>Частичное изменение настроек: передаются только меняемые поля; baseRevision защищает от потерянного обновления</summary>
[PublicAPI]
public sealed record SettingsPatch(
    long BaseRevision, int? HorizonDays, int? UrgencyWindowDays, ThresholdsPatch? Thresholds, ClockPatch? Clock, Dictionary<string, decimal>? Weights, string? Comment,
    ChurnPatch? Churn = null);

/// <summary>Результат изменения настроек</summary>
[PublicAPI]
public sealed record SettingsAccepted(long ModelRevision, long? JobId, string Status);

/// <summary>Изменение оценки клиента в предпросмотре</summary>
[PublicAPI]
public sealed record LevelShift(
    long CustomerId, string Email, decimal? ScoreBefore, decimal? ScoreAfter, string LevelBefore, string LevelAfter, long AtRiskBefore, long AtRiskAfter);

/// <summary>Предпросмотр черновика настроек</summary>
[PublicAPI]
public sealed record PreviewResponse(
    DateTimeOffset AsOf, long BasedOnGeneration, IReadOnlyDictionary<string, int> ByLevelBefore, IReadOnlyDictionary<string, int> ByLevelAfter,
    long AtRiskBefore, long AtRiskAfter, int ChangedLevelCount, IReadOnlyList<LevelShift> Changes);

/// <summary>Правила изменения настроек: слияние частичного изменения с текущей ревизией и проверка результата</summary>
internal static class ModelSettingsRules
{
    public static RevisionDto ToDto(this ModelRevisionEntity r) =>
        // Через доменную модель: ревизии схемы 1 отдаются в текущей схеме (с определениями оттока по умолчанию)
        new(r.Id, ModelSettingsDto.From(AppJson.Deserialize<ModelSettingsDto>(r.SettingsJson).ToModel()), r.CreatedAt, r.CreatedBy, r.Comment, r.BasedOn);

    public static ModelSettings Merge(ModelSettings current, SettingsPatch p)
    {
        // Ключи удалённых из каталога признаков отбрасываются, иначе любой PATCH навсегда получал бы 422
        var known = FeatureCatalog.All.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var weights = current.Weights.Where(k => known.Contains(k.Key)).ToDictionary(k => k.Key, k => k.Value);
        if (p.Weights is not null)
            foreach (var (k, v) in p.Weights) weights[k] = v;
        var clockMode = p.Clock?.Mode switch
        {
            null => current.Clock.Mode,
            "live" => ClockMode.Live,
            "source" => ClockMode.Source,
            _ => (ClockMode)(-1),
        };
        return current with
        {
            HorizonDays = p.HorizonDays ?? current.HorizonDays,
            UrgencyWindowDays = p.UrgencyWindowDays ?? current.UrgencyWindowDays,
            Thresholds = new Thresholds(p.Thresholds?.Medium ?? current.Thresholds.Medium, p.Thresholds?.High ?? current.Thresholds.High),
            Clock = new ClockSettings(clockMode, p.Clock?.TickMinutes ?? current.Clock.TickMinutes),
            Weights = weights,
            Churn = new ChurnSettings(
                p.Churn?.NonRenewalDays ?? current.Churn.NonRenewalDays,
                p.Churn?.PaymentWindowDays ?? current.Churn.PaymentWindowDays,
                p.Churn?.PaymentDeclinePct ?? current.Churn.PaymentDeclinePct),
        };
    }

    public static Dictionary<string, string[]> Validate(ModelSettings merged)
    {
        var errors = ModelSettingsValidator.Validate(merged, FeatureCatalog.All.ToList()).ToDictionary(k => k.Key, k => k.Value);
        if (!Enum.IsDefined(merged.Clock.Mode)) errors["clock.mode"] = ["Режим часов: live или source."];
        return errors;
    }
}
