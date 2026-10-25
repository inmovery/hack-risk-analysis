using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Settings;
using Aeza.Risk.SharedKernel.Abstraction;
using Aeza.Risk.SharedKernel.Json;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Analytics.History;

/// <summary>Факты из журнала событий</summary>
/// <param name="Coverage">Покрытие журнала (до первого события фактов нет)</param>
/// <param name="Days">Дни внутри покрытия (нули внутри покрытия — факт, до покрытия дней нет)</param>
/// <param name="Outcomes">Исходы сроков: непродление за N дней</param>
/// <param name="Note">Пояснение для подписи графика</param>
[PublicAPI]
public sealed record HistoryResponse(StreamCoverage Coverage, IReadOnlyList<HistoryDay> Days, ExpiryOutcomeStats Outcomes, string Note);

/// <summary>Факт (не прогноз): продления, переходы в ожидание оплаты, удаления, уход клиентов, платежи и пополнения по дням получения</summary>
[UsedImplicitly]
public class GetHistory
{
    /// <param name="days">Глубина, дни (1–400, по умолчанию 30)</param>
    /// <param name="journal">Журнал событий</param>
    /// <param name="models">Настройки модели (N для исходов сроков)</param>
    /// <param name="time">Часы</param>
    /// <param name="cancellationToken">Отмена</param>
    private static async Task<Results<Ok<HistoryResponse>, ProblemHttpResult>> Handle(
        int? days, ISourceStreamRepository journal, IModelSettingsRepository models, TimeProvider time, CancellationToken cancellationToken)
    {
        if (days is < 1 or > 400) return AppProblems.Validation(new Dictionary<string, string[]> { ["days"] = ["days от 1 до 400."] });
        var ct = cancellationToken;
        var now = time.GetUtcNow();
        var coverage = await journal.GetCoverageAsync(ct);
        var state = await models.GetStateAsync(ct);
        var grace = AppJson.Deserialize<ModelSettingsDto>(state.Latest.SettingsJson).ToModel().Churn.NonRenewalDays;
        var outcomes = await journal.GetOutcomeStatsAsync(grace, ct);
        if (coverage.StreamStartedAt is not { } started)
            return TypedResults.Ok(new HistoryResponse(coverage, [], outcomes,
                "Факта пока нет: журнал заполняется с первого события источника; историю до подключения потока не восстанавливаем."));

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var from = today.AddDays(1 - (days ?? 30));
        var first = DateOnly.FromDateTime(started.UtcDateTime);
        if (first > from) from = first;
        var actual = (await journal.GetDailyAsync(new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), ct)).ToDictionary(d => d.Date);
        var series = new List<HistoryDay>();
        for (var d = from; d <= today; d = d.AddDays(1))
            series.Add(actual.GetValueOrDefault(d) ?? new HistoryDay(d, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        var note = $"Факт по дню получения события; журнал с {started.UtcDateTime:dd.MM.yyyy HH:mm} UTC. Дни до подключения потока не заполняются нулями."
                   + (coverage.Synthetic ? " Среди событий есть синтетика (симулятор)." : "");
        return TypedResults.Ok(new HistoryResponse(coverage, series, outcomes, note));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetHistory);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("analytics/history", Handle)
                .RequireAuthorization(Policies.Reader)
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .WithTags("Analytics")
                .WithSummary("Факты оттока из журнала событий (не прогноз): по дням получения и исходы сроков")
                .WithDescription("Только применённые события после подключения потока: продления, переходы в prolong_wait, удаления, уход клиента целиком, платежи, пополнения." +
                                 "outcomes — непродление в течение N дней после срока; фиксируется только при свежем источнике.");
    }
}
