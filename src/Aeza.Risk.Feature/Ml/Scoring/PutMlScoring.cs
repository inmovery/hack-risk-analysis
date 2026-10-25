using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Live;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Ml.Scoring;

/// <summary>Режим основного балла</summary>
/// <param name="Primary">rules — правила; ml — только ML-модель</param>
[PublicAPI]
public sealed record ScoringRequest(string? Primary);

/// <summary>Переключить основной балл: правила или только ML</summary>
[UsedImplicitly]
public class PutMlScoring
{
    private static async Task<Results<Ok<MlScoringChanged>, Accepted<MlScoringChanged>, ProblemHttpResult>> Handle(
        ScoringRequest request, HttpContext http, IMlModels models, IRecalcSignal signal, ILiveNotifier live, CancellationToken cancellationToken)
    {
        if (!ScoringModes.IsValid(request.Primary))
            return AppProblems.Validation(new Dictionary<string, string[]> { ["primary"] = ["Режим: rules или ml."] });
        try
        {
            var changed = await models.SetScoringAsync(request.Primary!, http.User.Identity?.Name ?? "unknown", cancellationToken);
            if (changed.JobId is not { } jobId) return TypedResults.Ok(changed);
            signal.Notify();
            await live.JobQueuedAsync(new JobQueuedEvent(jobId, "scoring", null, null), cancellationToken);
            return TypedResults.Accepted("/api/v1/ml/status", changed);
        }
        catch (MlModelRequiredException ex)
        {
            return AppProblems.Create(StatusCodes.Status409Conflict, ex.Message, "ml_model_required");
        }
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(PutMlScoring);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPut("ml/scoring", Handle)
                .RequireAuthorization(Policies.Admin)
                .AddEndpointFilter<CsrfFilter>()
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .WithTags("ML")
                .WithSummary("Основной балл: правила или только ML")
                .WithDescription("rules — балл по правилам (Σ вес × сила). ml — балл услуги = вероятность непродления по включённой модели × 100; уровни, деньги, "
                    + "сортировки и /metrics считаются от него, услуги вне окна модели получают «нет оценки». 202 — режим сменён, поставлен пересчёт; 200 — режим уже такой; "
                    + "409 ml_model_required — модель не загружена. Если модель не сработала, поколение считается правилами (generation.scoring = rules). "
                    + "Выключение модели возвращает режим rules. Только admin (cookie + X-CSRF-TOKEN).");
    }
}
