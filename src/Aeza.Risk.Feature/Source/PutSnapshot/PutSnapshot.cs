using System.Globalization;

using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Live;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Source;
using Aeza.Risk.SharedKernel.Abstraction;
using Aeza.Risk.SharedKernel.Json;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aeza.Risk.Feature.Source.PutSnapshot;

/// <summary>Загрузка тестовых данных (полная замена; snapshot) с автоматическим пересчётом</summary>
[UsedImplicitly]
public class PutSnapshot
{
    private static async Task<Results<Ok<SnapshotAcceptedResponse>, Accepted<SnapshotAcceptedResponse>, ProblemHttpResult>> Handle(
        HttpRequest req, ISnapshotRepository snapshots, IRecalcSignal signal, ILiveNotifier live, TimeProvider time,
        ILogger<PutSnapshot> log, CancellationToken cancellationToken)
    {
        var (body, err) = await SnapshotHttp.ReadBodyAsync(req, cancellationToken);
        if (err is not null) return err;
        var parsed = SnapshotParser.Parse(body!);
        if (!parsed.IsValid)
        {
            log.LogWarning("Snapshot rejected: {ErrorCounts}", string.Join(", ", parsed.ErrorCounts.Select(kv => $"{kv.Key}={kv.Value}")));
            return AppProblems.Create(StatusCodes.Status422UnprocessableEntity, "Тестовые данные не прошли проверку; активный источник не изменён", "snapshot_invalid",
                new Dictionary<string, object?> { ["errors"] = parsed.Errors, ["errorCounts"] = parsed.ErrorCounts, ["warnings"] = parsed.Warnings });
        }
        var (asOf, assumed, asOfErr) = SnapshotHttp.ResolveAsOf(parsed, req, time);
        if (asOfErr is not null) return asOfErr;

        long? ifMatch = null;
        var ifMatchRaw = req.Headers.IfMatch.ToString().Trim('"', ' ', 'W', '/');
        if (!string.IsNullOrEmpty(ifMatchRaw))
        {
            if (!long.TryParse(ifMatchRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var im))
                return AppProblems.Create(StatusCodes.Status412PreconditionFailed, "If-Match должен содержать номер ревизии тестовых данных", "precondition_failed");
            ifMatch = im;
        }
        var user = req.HttpContext.User;
        var actor = user.Identity?.Name ?? "unknown";
        var provenance = AppJson.Serialize(new
        {
            via = user.Identity?.AuthenticationType == AuthSchemes.Machine ? "ingest-token" : "api",
            adapter = SnapshotParser.AdapterName,
            envelope = parsed.Envelope,
        });
        var result = await snapshots.StoreAsync(parsed, body!, new SnapshotRequestMeta(asOf!.Value, assumed, actor, provenance, ifMatch), cancellationToken);
        var counts = parsed.Counts();
        switch (result.Outcome)
        {
            case StoreOutcome.PreconditionFailed:
                return AppProblems.Create(StatusCodes.Status412PreconditionFailed, "Тестовые данные уже заменены: ревизия не совпадает с If-Match", "precondition_failed",
                    new Dictionary<string, object?> { ["latestRevision"] = result.LatestRevision });
            case StoreOutcome.Stale:
                return AppProblems.Create(StatusCodes.Status409Conflict, "Тестовые данные старше уже загруженных (sourceAsOf); активный источник не изменён", "stale_snapshot",
                    new Dictionary<string, object?> { ["latestRevision"] = result.LatestRevision });
            case StoreOutcome.Idempotent:
                return TypedResults.Ok(new SnapshotAcceptedResponse(result.Revision!.Value, null, true, counts, parsed.Warnings, asOf.Value, assumed));
            default:
                signal.Notify();
                log.LogInformation("Snapshot r{Revision} accepted ({Accounts}/{Services}/{Transactions}), job {JobId}",
                    result.Revision, counts.Accounts, counts.Services, counts.Transactions, result.JobId);
                await live.JobQueuedAsync(new JobQueuedEvent(result.JobId!.Value, "source", null, result.Revision), cancellationToken);
                return TypedResults.Accepted((string?)null,
                    new SnapshotAcceptedResponse(result.Revision!.Value, result.JobId, false, counts, parsed.Warnings, asOf.Value, assumed));
        }
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(PutSnapshot);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPut("source/snapshot", Handle)
                .RequireAuthorization(Policies.SourceWriter)
                .AddEndpointFilter<CsrfFilter>()
                .Accepts<SnapshotEnvelope>("application/json")
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status412PreconditionFailed)
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .WithTags("Source")
                .WithSummary("Загрузка тестовых данных (полная замена; snapshot) с автоматическим пересчётом")
                .WithDescription("Тело — envelope {schemaVersion, sourceAsOf, accounts, services, transactions} или «голый» data.json (+ ?sourceAsOf=)." +
                                 "Деньги — целые еврокопейки. Ответ 202 + jobId; результат публикуется событием generationPublished. Тот же контент — 200 idempotent." +
                                 "Admin (cookie + X-CSRF-TOKEN) или Bearer INGEST_TOKEN. If-Match: \"<sourceRevision>\" — оптимистическая блокировка.");
    }
}
