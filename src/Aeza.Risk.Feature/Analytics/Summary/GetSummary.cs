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

namespace Aeza.Risk.Feature.Analytics.Summary;

/// <summary>Сводка поколения: KPI, деньги, прогноз и наблюдённая история</summary>
[PublicAPI]
public sealed record SummaryResponse(
    GenerationInfo Generation, PendingInfo? Pending, LastFailureInfo? LastFailure, SummaryCustomersDto Customers, SummaryServicesDto Services,
    SummaryMoneyDto Money, IReadOnlyDictionary<string, int> QualityFlags, IReadOnlyList<TopCustomerDto> TopAtRisk,
    IReadOnlyList<ForecastPointDto> Forecast, HistoryDto History, DefinitionsDto Definitions, ChurnSummaryDto? Churn);

/// <summary>Получение сводных показателей риска и денежной оценки потенциальных потерь</summary>
[UsedImplicitly]
public class GetSummary
{
    /// <param name="generationId">Явное поколение (по умолчанию — активное)</param>
    /// <param name="generations">Выбор поколения</param>
    /// <param name="analytics">Наблюдённая история</param>
    /// <param name="cancellationToken">Отмена</param>
    private static async Task<Results<Ok<SummaryResponse>, ProblemHttpResult>> Handle(long? generationId, IGenerationReader generations,
        IAnalyticsRepository analytics, CancellationToken cancellationToken)
    {
        var lookup = await generations.ResolveAsync(generationId, cancellationToken);
        if (lookup.Context is not { } ctx) return AppProblems.Generation(lookup.Error);
        var gen = ctx.Generation;
        var summary = AppJson.Deserialize<GenerationSummary>(gen.SummaryJson);
        var forecast = AppJson.Deserialize<List<ForecastPointDto>>(gen.ForecastJson);

        var observed = await analytics.GetHistoryAsync(gen.SourceRevision, cancellationToken);
        var note = observed.Snapshots <= 1
            ? "Нет наблюдённой истории: в исходных данных нет дат удаления и истории статусов. Факт появится после следующей загрузки тестовых данных (изменение между загрузками, время — интервал)."
            : "Факт: изменения, наблюдённые между последовательными загрузками тестовых данных. Точное время неизвестно — указан интервал (между загрузками).";
        var history = new HistoryDto(observed.Snapshots, observed.EventsTotal, observed.DeletedByObservedDay, observed.Recent, note);
        return TypedResults.Ok(new SummaryResponse(ctx.Info, ctx.Pending, ctx.LastFailure, summary.Customers, summary.Services, summary.Money,
            summary.QualityFlags, summary.TopAtRisk, forecast, history, Definitions.Default, summary.Churn));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetSummary);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("analytics/summary", Handle)
                .RequireAuthorization(Policies.Reader)
                .ProducesProblem(StatusCodes.Status410Gone)
                .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
                .WithTags("Analytics")
                .WithSummary("Получение сводных показателей риска и денежной оценки потенциальных потерь")
                .WithDescription("Все числа — из одного поколения (generation.id), совпадают с /metrics того же поколения. Деньги — еврокопейки." +
                                 "forecast — прогноз (сценарий), history — только фактически наблюдённые изменения между загрузками тестовых данных; факты потока — /analytics/history." +
                                 "churn — индикаторы «уход целиком» и «тренд платежей» по клиентам.");
    }
}
