using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Live;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Simulation;

/// <summary>Пересоздать snapshot системы: данные источника и результаты удаляются, симулятор строит новый snapshot системы и шлёт его событиями</summary>
[UsedImplicitly]
public class ResetSimulation
{
    private static async Task<Results<Accepted<SimulationView>, ProblemHttpResult>> Handle(
        HttpContext http, ISimulationStore store, ILiveNotifier live, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(cancellationToken);
        if (!current.EventsOnly)
            return AppProblems.Create(StatusCodes.Status409Conflict, "Пересоздание snapshot системы доступно только в режиме работы на событиях", "events_only_disabled");
        var view = await store.ResetAsync(http.User.Identity?.Name ?? "unknown", cancellationToken);
        await live.SourceChangedAsync(new SourceChangedEvent(0, 0, 0, new Dictionary<string, int>(), true, "simulation_reset"), cancellationToken);
        return TypedResults.Accepted("/api/v1/simulation", view);
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(ResetSimulation);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPost("simulation/reset", Handle)
                .RequireAuthorization(Policies.Admin)
                .AddEndpointFilter<CsrfFilter>()
                .ProducesProblem(StatusCodes.Status409Conflict)
                .WithTags("Simulation")
                .WithSummary("Пересоздать snapshot системы")
                .WithDescription("Удаляет данные источника, журнал и результаты анализатора (пользователи, ревизии модели, ML-модели и аудит остаются), ревизия snapshot системы + 1. "
                    + "Пакеты старого snapshot системы после этого отклоняются (409 simulation_world_changed). 409 events_only_disabled — режим работы на событиях выключен. "
                    + "Только admin (cookie + X-CSRF-TOKEN).");
    }
}
