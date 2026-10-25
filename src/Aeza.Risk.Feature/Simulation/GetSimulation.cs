using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Simulation;

/// <summary>Настройки симуляции, текущий snapshot системы и состояние симулятора</summary>
[UsedImplicitly]
public class GetSimulation
{
    private static async Task<Ok<SimulationView>> Handle(ISimulationStore store, CancellationToken cancellationToken)
        => TypedResults.Ok(await store.GetAsync(cancellationToken));

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetSimulation);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("simulation", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("Simulation")
                .WithSummary("Симуляция: настройки, текущий snapshot системы, отчёт симулятора")
                .WithDescription("eventsOnly — анализатор получает данные только событиями; pending.worldChanged — параметры snapshot системы изменены и применятся после пересоздания.");
    }
}
