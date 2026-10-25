using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Simulation;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Simulation;

/// <summary>Отчёт симулятора о своём состоянии</summary>
[UsedImplicitly]
public class PostAgentStatus
{
    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(SimulationAgentStatus status, ISimulationStore store, CancellationToken cancellationToken)
    {
        if (!SimulationRules.Phases.Contains(status.Phase))
            return AppProblems.Validation(new Dictionary<string, string[]> { ["phase"] = ["Фаза: " + string.Join(", ", SimulationRules.Phases) + "."] });
        await store.ReportAsync(status with { LastError = Clip(status.LastError), LastNarration = Clip(status.LastNarration) }, cancellationToken);
        return TypedResults.NoContent();
    }

    private static string? Clip(string? s) => s is { Length: > 500 } ? s[..500] : s;

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(PostAgentStatus);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPost("simulation/agent/status", Handle)
                .RequireAuthorization(Policies.SimulationAgent)
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .WithTags("Simulation")
                .WithSummary("Отчёт симулятора")
                .WithDescription("Только симулятор (Authorization: Bearer INGEST_TOKEN). Панель показывает последний отчёт; "
                    + "симулятор на связи, если отчёт свежее Simulation:AgentOnlineSeconds.");
    }
}
