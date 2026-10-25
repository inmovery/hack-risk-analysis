using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Live;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Source;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aeza.Risk.Feature.Source.Events;

/// <summary>Приём пакета событий источника: входящий коннектор http-push</summary>
[UsedImplicitly]
public class PostEvents
{
    /// <summary>Лимит тела пакета</summary>
    public const long MaxBodyBytes = 5L * 1024 * 1024;

    /// <summary>Заголовок ревизии snapshot системы</summary>
    public const string SimulationWorldHeader = "X-Simulation-World";

    private static async Task<Results<Ok<EventBatchResponse>, Accepted<EventBatchResponse>, ProblemHttpResult>> Handle(
        HttpRequest req, ISourceIngestion ingestion, IRecalcSignal signal, ILiveNotifier live, TimeProvider time,
        ILogger<PostEvents> log, CancellationToken cancellationToken)
    {
        var (body, err) = await SnapshotHttp.ReadBodyAsync(req, cancellationToken, MaxBodyBytes);
        if (err is not null) return err;
        var batch = EventBatchParser.Parse(body!, time.GetUtcNow());
        if (!batch.IsValid)
            return AppProblems.Validation(batch.EnvelopeErrors, "Пакет событий не прошёл проверку", "events_invalid");

        var errors = batch.Events.Where(e => !e.IsValid).SelectMany(e => e.Errors).ToDictionary(k => k.Key, k => k.Value);
        var rejected = batch.Events.Where(e => !e.IsValid).ToList();
        if (rejected.Count > 0) log.LogWarning("Source events rejected: {Count} of {Total}", rejected.Count, batch.Events.Count);

        var user = req.HttpContext.User;
        var actor = user.Identity?.Name ?? "unknown";
        // Коннектор — по токену (http-push или имя из Auth:IngestConnectors); сессия администратора — panel
        var connector = user.FindFirst(MachineClaims.Connector)?.Value ?? ConnectorNames.Panel;
        var valid = batch.Events.Where(e => e.IsValid).Select(e => new IngestItem(e.Index, e.Domain, e.Changes)).ToList();
        // Счётчики коннектора видят и отклонённые: путь первой ошибки (без значений) — «последняя ошибка» на странице «Источник»
        var firstError = errors.Keys.FirstOrDefault();
        // Симулятор помечает пакет ревизией своего snapshot системы: пакеты пересозданного snapshot системы не применяются
        long? world = long.TryParse(req.Headers[SimulationWorldHeader], out var w) ? w : null;
        IngestBatchResult result;
        try
        {
            result = valid.Count == 0 && rejected.Count == 0
                ? IngestBatchResult.Empty
                : await ingestion.IngestAsync(valid, new IngestMeta(SourceCodes.OriginPush, actor, batch.Synthetic, connector, rejected.Count, firstError, SimulationWorld: world),
                    cancellationToken);
        }
        catch (SimulationWorldChangedException ex)
        {
            return AppProblems.Create(StatusCodes.Status409Conflict, $"Snapshot системы симуляции пересоздан (текущая ревизия {ex.Current}): пакет старого snapshot системы не применён",
                "simulation_world_changed");
        }

        var results = result.Results
            .Concat(rejected.Select(r => new IngestItemResult(r.Index, r.EventId, SourceCodes.Rejected, null, "invalid", [])))
            .OrderBy(r => r.Index).ToList();
        var response = new EventBatchResponse(batch.Events.Count, result.Applied, result.NoChange, result.Stale, result.Duplicates, rejected.Count,
            result.Recorded, result.UnknownTypes, result.LastSeq, result.JobId, results, errors);
        if (result.Applied + result.NoChange + result.Stale + result.Recorded > 0)
            await live.SourceChangedAsync(new SourceChangedEvent(result.LastSeq, result.Applied, batch.Events.Count, result.ChangeCounts, batch.Synthetic, SourceCodes.OriginPush), cancellationToken);
        if (result.JobId is not { } jobId) return TypedResults.Ok(response);
        signal.Notify();
        await live.JobQueuedAsync(new JobQueuedEvent(jobId, "events", null, null), cancellationToken);
        return TypedResults.Accepted((string?)null, response);
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(PostEvents);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPost("source/events", Handle)
                .RequireAuthorization(Policies.SourceWriter)
                .AddEndpointFilter<CsrfFilter>()
                .Accepts<EventBatchRequest>("application/json")
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .WithTags("Source")
                .WithSummary("Приём пакета событий источника (v1 — состояние записи, v2 — доменное событие) с автоматическим пересчётом")
                .WithDescription("Тело: { synthetic?, events: [...] }, ≤ 1000 событий, ≤ 5 МБ; форматы можно смешивать." +
                                 "v2: { schemaVersion: 2, eventId, type: домен.действие, occurredAt, accountId?, accountSeq?, serviceId?, data, state? } —" +
                                 "state несёт полное состояние изменённых записей; время для истории и исходов — occurredAt; порядок — accountSeq в пределах аккаунта;" +
                                 "stream.watermark { data.upTo } — все события до upTo отправлены." +
                                 "v1: { eventId?, type: account|service|transaction, data } — полное состояние записи, время = время получения." +
                                 "Исходы: applied, no_change, stale (нарушена монотонность: deleted конечный, expiresAt и totalReplenished не убывают, performed не откатывается)," +
                                 "recorded (v2 без state), duplicate (повтор eventId), rejected (ошибка полей; пакет не отклоняется целиком). 202 + jobId, если состояние изменилось; иначе 200." +
                                 "Bearer INGEST_TOKEN (коннектор http-push) или токен из Auth:IngestConnectors, либо admin (cookie + X-CSRF-TOKEN).");
    }
}
