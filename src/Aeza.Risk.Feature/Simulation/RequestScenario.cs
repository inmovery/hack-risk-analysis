using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Simulation;

/// <summary>Запустить демонстрационный сценарий: симулятор пошагово меняет одного клиента</summary>
[UsedImplicitly]
public class RequestScenario
{
    private static async Task<Accepted<SimulationView>> Handle(HttpContext http, ISimulationStore store, CancellationToken cancellationToken)
        => TypedResults.Accepted("/api/v1/simulation", await store.RequestScenarioAsync(http.User.Identity?.Name ?? "unknown", cancellationToken));

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(RequestScenario);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPost("simulation/scenario", Handle)
                .RequireAuthorization(Policies.Admin)
                .AddEndpointFilter<CsrfFilter>()
                .WithTags("Simulation")
                .WithSummary("Запустить демонстрационный сценарий")
                .WithDescription("Симулятор подхватит запрос при следующем опросе. Только admin (cookie + X-CSRF-TOKEN).");
    }
}
