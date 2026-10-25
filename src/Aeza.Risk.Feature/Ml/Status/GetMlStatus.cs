using System.Text.Json;

using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Generations;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;
using Aeza.Risk.SharedKernel.Json;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Ml.Status;

/// <summary>Статус ML-модели</summary>
/// <param name="Status">not_trained — модели нет, shadow — модель включена в тени (правила остаются основным расчётом)</param>
/// <param name="Reason">Пояснение статуса</param>
/// <param name="Target">Цель модели</param>
/// <param name="ObservedSnapshots">Загрузок тестовых данных</param>
/// <param name="ObservedStatusChanges">Наблюдённых смен статуса между загрузками</param>
/// <param name="ObservedDeletions">Наблюдённых удалений между загрузками</param>
/// <param name="Collection">Как копятся данные для обучения</param>
/// <param name="NextStep">Путь обучения и включения</param>
/// <param name="Labels">Созревшие исходы</param>
/// <param name="Shadow">Теневая модель: карточка, офлайн-сравнение с правилами, прогнозы активного поколения, проверка на реальных исходах</param>
[PublicAPI]
public sealed record MlStatus(
    string Status, string Reason, string Target, int ObservedSnapshots, int ObservedStatusChanges, int ObservedDeletions, string Collection, string NextStep,
    MlLabels Labels, MlShadowStatus? Shadow = null);

/// <summary>Накопленные исходы для обучения (непродление за N дней)</summary>
/// <param name="Renewed">Продлено</param>
/// <param name="NotRenewed">Не продлено</param>
/// <param name="Pending">Ждут исхода</param>
/// <param name="RequiredPerClass">Нужно исходов каждого класса для честной проверки</param>
/// <param name="Ready">Достаточно для обучения и проверки</param>
/// <param name="Runtime">Путь инференса</param>
[PublicAPI]
public sealed record MlLabels(int Renewed, int NotRenewed, int Pending, int RequiredPerClass, bool Ready, string Runtime);

/// <summary>Теневая модель</summary>
/// <param name="Model">Загруженная модель</param>
/// <param name="Offline">Офлайн-отчёт из карточки (на чём обучена и как проверена)</param>
/// <param name="Generation">Прогнозы активного поколения (null — поколение ещё без этой модели)</param>
/// <param name="GenerationId">Активное поколение</param>
/// <param name="Online">Прогнозы в точках решения против созревших исходов этого анализатора</param>
/// <param name="Note">Как читать</param>
[PublicAPI]
public sealed record MlShadowStatus(MlModelView Model, MlOfflineReport Offline, MlShadowSummaryDto? Generation, long? GenerationId, MlOnlineEvaluation Online, string Note);

/// <summary>Метрики ранжирования на отложенном периоде</summary>
[PublicAPI]
public sealed record MlMetric(double PrAuc, double RocAuc, int K, double PrecisionAtK, double RecallAtK, double? Brier);

/// <summary>Вклад признака: падение PR-AUC при перемешивании</summary>
[PublicAPI]
public sealed record MlFeatureImportance(string Name, double PrAucDrop);

/// <summary>Офлайн-отчёт модели (из model.json)</summary>
/// <param name="Model">logistic | fasttree</param>
/// <param name="Baseline">rules | rulesWide — лучшая базовая линия правил на проверке</param>
/// <param name="Synthetic">Обучена и проверена на синтетике: метрики — проверка конвейера, не качество на реальных клиентах</param>
/// <param name="TestFrom">Начало отложенного периода</param>
/// <param name="TestRows">Решений в проверке</param>
/// <param name="TestPositives">Из них «не продлено»</param>
/// <param name="TrainRows">Решений в обучении</param>
/// <param name="ModelMetrics">Модель</param>
/// <param name="Rules">Правила по умолчанию (окно срочности 7 дн.)</param>
/// <param name="RulesWide">Правила с окном срочности 30 дн</param>
/// <param name="PrAucCi95">95% интервал PR-AUC модели (bootstrap по клиентам)</param>
/// <param name="PrAucMinusRulesCi95">95% интервал разницы PR-AUC с лучшими правилами</param>
/// <param name="PrecisionAtKMinusRulesCi95">95% интервал разницы P@K с лучшими правилами</param>
/// <param name="TopFeatures">Важнейшие признаки</param>
/// <param name="ShuffledLabelsRocAuc">ROC-AUC модели на перемешанных метках (≈ 0,5 — утечки через конвейер нет)</param>
[PublicAPI]
public sealed record MlOfflineReport(
    string Model, string Baseline, bool Synthetic, string? TestFrom, int TestRows, int TestPositives, int TrainRows, MlMetric? ModelMetrics, MlMetric? Rules,
    MlMetric? RulesWide, double[]? PrAucCi95, double[]? PrAucMinusRulesCi95, double[]? PrecisionAtKMinusRulesCi95, IReadOnlyList<MlFeatureImportance> TopFeatures,
    double? ShuffledLabelsRocAuc);

/// <summary>Честный статус ML: без модели основной score — правила</summary>
[UsedImplicitly]
public class GetMlStatus
{
    /// <summary>Исходов каждого класса для честной проверки на отложенном периоде</summary>
    public const int RequiredPerClass = 200;

    private static async Task<Ok<MlStatus>> Handle(
        IAnalyticsRepository analytics, ISourceStreamRepository journal, IMlModels models, IGenerationReader generations, CancellationToken cancellationToken)
    {
        var counts = await analytics.GetObservationCountsAsync(cancellationToken);
        var outcomes = await journal.GetOutcomeStatsAsync(0, cancellationToken);
        var ready = outcomes.Renewed >= RequiredPerClass && outcomes.NotRenewed >= RequiredPerClass;
        var active = await models.GetActiveAsync(cancellationToken);
        MlShadowStatus? shadow = null;
        if (active is not null)
        {
            var lookup = await generations.ResolveAsync(null, cancellationToken);
            var gen = lookup.Context?.Generation;
            var summary = gen is null ? null : AppJson.Deserialize<GenerationSummary>(gen.SummaryJson).MlShadow;
            var forThisModel = summary is not null && summary.ModelId == active.Model.Id;
            shadow = new MlShadowStatus(active.Model, Offline(active.CardJson), forThisModel ? summary : null, forThisModel ? gen!.Id : null,
                await models.EvaluateAsync(active.Model.Id, cancellationToken),
                active.Model.Synthetic
                    ? "Модель обучена на синтетике симулятора: офлайн-метрики проверяют конвейер, а не качество на реальных клиентах. Прогнозы в тени не меняют баллы, уровни, сортировки и деньги."
                    : "Прогнозы в тени не меняют баллы, уровни, сортировки и деньги. Продвижение — после проверки на реальных исходах и согласования бизнес-цели.");
        }
        return TypedResults.Ok(new MlStatus(
            shadow is null ? "not_trained" : "shadow",
            shadow is not null
                ? $"Включена теневая модель {shadow.Model.Version}: прогноз «не продлено» считается рядом с правилами и сверяется с исходами сроков; основной score — правила."
                : ready
                    ? "Исходов достаточно для офлайн-обучения и проверки; модель ещё не обучена и не подключена — основной score остаётся правилами."
                    : $"Мало наблюдённых исходов «продлил/не продлил»: {outcomes.Renewed} / {outcomes.NotRenewed}, нужно ≥ {RequiredPerClass} каждого. Исходные тестовые данные без истории; исходы копятся из потока событий.",
            "Непродление услуги в течение N дней после expiresAt (churn.nonRenewalDays), уровень услуги; решение за 14 и 7 дней до срока (месяц и дольше), за 3 дня (неделя).",
            counts.Snapshots, counts.StatusChanges, counts.Deletions,
            "expiry_outcomes: исходы сроков из журнала событий; набор point-in-time строится переигрыванием журнала (tools/Aeza.Risk.Ml dataset --db); ml_shadow_predictions — прогнозы теневой модели в точках решения.",
            "Офлайн: tools/Aeza.Risk.Ml dataset → train (логистическая регрессия и FastTree против правил, ONNX) → PUT /api/v1/ml/model; модель работает в тени, продвижение — только при выигрыше на реальных исходах.",
            new MlLabels(outcomes.Renewed, outcomes.NotRenewed, outcomes.Pending, RequiredPerClass, ready, shadow is null ? "onnx (модель не загружена)" : "onnx runtime, в тени"),
            shadow));
    }

    /// <summary>Сводка карточки model.json (поля, которых нет, — пустые)</summary>
    public static MlOfflineReport Offline(string cardJson)
    {
        using var doc = JsonDocument.Parse(cardJson);
        var card = doc.RootElement;
        static JsonElement? Get(JsonElement e, params string[] path)
        {
            foreach (var p in path)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out var next)) return null;
                e = next;
            }
            return e;
        }
        static double? Num(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : null;
        static int Int(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var i) ? i : 0;
        static string? Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
        static double[]? Pair(JsonElement? e) => e is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() == 2 && a.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number)
            ? a.EnumerateArray().Select(x => x.GetDouble()).ToArray()
            : null;
        static MlMetric? Metric(JsonElement? m) => m is { ValueKind: JsonValueKind.Object }
            ? new MlMetric(Num(m.Value.TryGetProperty("prAuc", out var a) ? a : null) ?? 0, Num(m.Value.TryGetProperty("rocAuc", out var r) ? r : null) ?? 0,
                Int(m.Value.TryGetProperty("k", out var k) ? k : null), Num(m.Value.TryGetProperty("precisionAtK", out var p) ? p : null) ?? 0,
                Num(m.Value.TryGetProperty("recallAtK", out var rc) ? rc : null) ?? 0, Num(m.Value.TryGetProperty("brier", out var b) ? b : null))
            : null;
        var model = Str(Get(card, "model")) ?? "unknown";
        var top = Get(card, "topFeatures") is { ValueKind: JsonValueKind.Array } t
            ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Take(10)
                .Select(x => new MlFeatureImportance(Str(Get(x, "name")) ?? "", Num(Get(x, "prAucDrop")) ?? 0)).ToList()
            : [];
        return new MlOfflineReport(model, Str(Get(card, "baseline")) ?? "rules", Get(card, "synthetic") is not { ValueKind: JsonValueKind.False },
            Str(Get(card, "split", "testFrom")), Int(Get(card, "split", "testRows")), Int(Get(card, "split", "testPositives")), Int(Get(card, "split", "trainRows")),
            Metric(Get(card, "metrics", model, "time")), Metric(Get(card, "metrics", "rules")), Metric(Get(card, "metrics", "rulesWide")),
            Pair(Get(card, "metrics", model, "prAucCi95")), Pair(Get(card, "metrics", model, "prAucMinusRulesCi95")),
            Pair(Get(card, "metrics", model, "precisionAtKMinusRulesCi95")), top, Num(Get(card, "leakage", "shuffledLabelsRocAuc")));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetMlStatus);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("ml/status", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("ML")
                .WithSummary("Статус ML-модели: not_trained или shadow (модель в тени — карточка, сравнение с правилами, проверка на реальных исходах)");
    }
}
