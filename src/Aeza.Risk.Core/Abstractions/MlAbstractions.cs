namespace Aeza.Risk.Core.Abstractions;

/// <summary>Загруженная ML-модель</summary>
/// <param name="Id">Номер</param>
/// <param name="Version">Версия из карточки</param>
/// <param name="Kind">logistic | fasttree</param>
/// <param name="FeatureSchema">Схема признаков</param>
/// <param name="Synthetic">Обучена на синтетике</param>
/// <param name="UploadedAt">Загружена</param>
/// <param name="UploadedBy">Кем</param>
/// <param name="OnnxSha256">SHA-256 ONNX</param>
/// <param name="OnnxBytes">Размер ONNX</param>
public sealed record MlModelView(
    long Id, string Version, string Kind, string FeatureSchema, bool Synthetic, DateTimeOffset UploadedAt, string UploadedBy, string OnnxSha256, int OnnxBytes);

/// <summary>Модель включена в тени: пересчёт поставлен в очередь</summary>
/// <param name="Model">Модель</param>
/// <param name="JobId">Пересчёт с теневой моделью</param>
public sealed record MlModelActivated(MlModelView Model, long JobId);

/// <summary>Включённая теневая модель: описание, карточка model.json как есть, отчёт</summary>
public sealed record MlActiveModel(MlModelView Model, string CardJson, string? ReportMarkdown);

/// <summary>Проверка теневой модели на реальных исходах сроков (прогноз в точке решения E − k против expiry_outcomes)</summary>
/// <param name="Predictions">Прогнозов в точках решения</param>
/// <param name="Matured">Из них с исходом renewed / not_renewed</param>
/// <param name="NotRenewed">Из них «не продлено»</param>
/// <param name="Pending">Ждут исхода</param>
/// <param name="Model">Метрики модели (null — мало исходов)</param>
/// <param name="Rules">Метрики правил на тех же решениях</param>
public sealed record MlOnlineEvaluation(int Predictions, int Matured, int NotRenewed, int Pending, RankMetrics? Model, RankMetrics? Rules);

/// <summary>Пакет модели не принят</summary>
/// <param name="code">Код (bundle_invalid, card_invalid, schema_mismatch, features_mismatch, onnx_hash_mismatch, onnx_invalid, onnx_output_invalid)</param>
/// <param name="message">Описание для пользователя</param>
public sealed class MlModelRejectedException(string code, string message) : Exception(message)
{
    /// <summary>Код ошибки</summary>
    public string Code { get; } = code;
}

/// <summary>Теневые ML-модели</summary>
public interface IMlModels
{
    /// <summary>Проверить пакет (zip: model.onnx + model.json), сохранить, включить в тени и поставить пересчёт</summary>
    Task<MlModelActivated> UploadAsync(Stream bundle, string actor, CancellationToken cancellationToken);

    /// <summary>Выключить теневую модель; номер пересчёта без неё, null — уже выключена</summary>
    Task<long?> DeactivateAsync(string actor, CancellationToken cancellationToken);

    /// <summary>Включённая модель</summary>
    Task<MlActiveModel?> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>Прогнозы модели против созревших исходов</summary>
    Task<MlOnlineEvaluation> EvaluateAsync(long modelId, CancellationToken cancellationToken);
}
