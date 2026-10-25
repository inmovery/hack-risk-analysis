using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Simulation;

/// <summary>Инструкции симулятору: какой snapshot системы вести и с какими настройками</summary>
[UsedImplicitly]
public class GetAgentInstructions
{
    private static async Task<Ok<SimulationAgentInstructions>> Handle(ISimulationStore store, CancellationToken cancellationToken)
        => TypedResults.Ok(await store.GetInstructionsAsync(cancellationToken));

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetAgentInstructions);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("simulation/agent", Handle)
                .RequireAuthorization(Policies.SimulationAgent)
                .WithTags("Simulation")
                .WithSummary("Инструкции симулятору")
                .WithDescription("Только симулятор (Authorization: Bearer INGEST_TOKEN). Симулятор опрашивает анализатор; анализатор к источнику не обращается.");
    }
}
