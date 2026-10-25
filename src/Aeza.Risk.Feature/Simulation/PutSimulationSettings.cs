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

/// <summary>Сохранить настройки симуляции: такт и сбои применяются симулятором сразу, параметры snapshot системы — при пересоздании. Симуляция постоянная: пауза не сохраняется</summary>
[UsedImplicitly]
public class PutSimulationSettings
{
    private static async Task<Results<Ok<SimulationView>, ProblemHttpResult>> Handle(
        SimulationSettings settings, HttpContext http, ISimulationStore store, CancellationToken cancellationToken)
    {
        var errors = SimulationRules.Validate(settings);
        if (errors.Count > 0) return AppProblems.Validation(errors, "Настройки симуляции не прошли проверку", "validation_failed");
        return TypedResults.Ok(await store.SaveSettingsAsync(settings, http.User.Identity?.Name ?? "unknown", cancellationToken));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(PutSimulationSettings);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPut("simulation/settings", Handle)
                .RequireAuthorization(Policies.Admin)
                .AddEndpointFilter<CsrfFilter>()
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .WithTags("Simulation")
                .WithSummary("Сохранить настройки симуляции")
                .WithDescription("Такт и сбои доставки применяются симулятором при следующем опросе; параметры snapshot системы — после POST /api/v1/simulation/reset. "
                    + "Симуляция постоянная: поле running всегда true. Только admin (cookie + X-CSRF-TOKEN).");
    }
}
