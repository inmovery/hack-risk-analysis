using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Live;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Ml.Model;

/// <summary>Выключить теневую ML-модель: следующее поколение считается без неё (модель остаётся в истории)</summary>
[UsedImplicitly]
public class DeleteMlModel
{
    private static async Task<Results<Accepted, NoContent>> Handle(
        HttpContext http, IMlModels models, IRecalcSignal signal, ILiveNotifier live, CancellationToken cancellationToken)
    {
        var job = await models.DeactivateAsync(http.User.Identity?.Name ?? "unknown", cancellationToken);
        if (job is not { } jobId) return TypedResults.NoContent();
        signal.Notify();
        await live.JobQueuedAsync(new JobQueuedEvent(jobId, "ml_model", null, null), cancellationToken);
        return TypedResults.Accepted("/api/v1/ml/status");
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(DeleteMlModel);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapDelete("ml/model", Handle)
                .RequireAuthorization(Policies.Admin)
                .AddEndpointFilter<CsrfFilter>()
                .WithTags("ML")
                .WithSummary("Выключить теневую ML-модель")
                .WithDescription("202 — модель выключена, поставлен пересчёт без неё; 204 — модель и так выключена. Только admin (cookie + X-CSRF-TOKEN).");
    }
}
