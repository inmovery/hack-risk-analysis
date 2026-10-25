using System.Text.Json;

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

/// <summary>Событие из конструктора</summary>
/// <param name="Type">Тип из GET /api/v1/simulation/events/types</param>
/// <param name="AccountId">Клиент</param>
/// <param name="ServiceId">Услуга клиента (для событий услуги)</param>
/// <param name="Data">Поля события; суммы — в евро</param>
[PublicAPI]
public sealed record ComposeEventRequest(string? Type, long AccountId, long? ServiceId, JsonElement Data);

/// <summary>Собрать событие и отправить его в общий поток: симулятор применяет его к snapshot системы и отправляет со следующим номером аккаунта</summary>
[UsedImplicitly]
public class ComposeEvent
{
    private static async Task<Results<Accepted<SimulationCommandView>, ProblemHttpResult>> Handle(
        ComposeEventRequest request, HttpContext http, ISimulationStore store, CancellationToken cancellationToken)
    {
        var errors = EventComposer.Validate(request.Type, request.AccountId, request.ServiceId, request.Data, out var data);
        if (errors.Count > 0) return AppProblems.Validation(errors, "Событие собрано не полностью", "validation_failed");
        if (!(await store.GetAsync(cancellationToken)).EventsOnly)
            return AppProblems.Create(StatusCodes.Status409Conflict, "Симуляция выключена: событие некому применить", "events_only_disabled");
        var type = EventComposer.Find(request.Type)!;
        try
        {
            var view = await store.EnqueueCommandAsync(type.Type, request.AccountId, type.NeedsService ? request.ServiceId : null, data,
                http.User.Identity?.Name ?? "unknown", cancellationToken);
            return TypedResults.Accepted("/api/v1/simulation/events", view);
        }
        catch (ComposerTargetException ex)
        {
            return AppProblems.Validation(new Dictionary<string, string[]> { [ex.Path] = [ex.Message] }, "Событие собрано не полностью", "validation_failed");
        }
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(ComposeEvent);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPost("simulation/events", Handle)
                .RequireAuthorization(Policies.Admin)
                .AddEndpointFilter<CsrfFilter>()
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .WithTags("Simulation")
                .WithSummary("Собрать событие и отправить в общий поток")
                .WithDescription("Событие встаёт в очередь симулятора (202, статус pending). Симулятор применяет его к snapshot системы, отправляет со следующим номером "
                    + "аккаунта и сообщает итог: applied (с идентификатором события) или rejected (с причиной, например «клиент уже ушёл»). "
                    + "422 — поля не заполнены или клиента/услуги нет в текущих данных; 409 — симуляция выключена. Только admin (cookie + X-CSRF-TOKEN).");
    }
}

/// <summary>Типы событий конструктора и их поля</summary>
[UsedImplicitly]
public class GetComposerTypes
{
    private static Ok<IReadOnlyList<ComposerEventType>> Handle() => TypedResults.Ok(EventComposer.Types);

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetComposerTypes);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("simulation/events/types", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("Simulation")
                .WithSummary("Типы событий конструктора: поля формы, варианты и границы");
    }
}

/// <summary>Последние события, собранные в панели, и их итог</summary>
[UsedImplicitly]
public class GetComposedEvents
{
    private static async Task<Ok<IReadOnlyList<SimulationCommandView>>> Handle(ISimulationStore store, CancellationToken cancellationToken) =>
        TypedResults.Ok(await store.ListCommandsAsync(30, cancellationToken));

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetComposedEvents);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("simulation/events", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("Simulation")
                .WithSummary("Последние события из конструктора и их итог (pending, applied, rejected)");
    }
}

/// <summary>Итоги событий конструктора от симулятора</summary>
[UsedImplicitly]
public class PostAgentCommands
{
    private static async Task<NoContent> Handle(IReadOnlyList<SimulationCommandResult> results, ISimulationStore store, CancellationToken cancellationToken)
    {
        await store.ReportCommandsAsync(results, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(PostAgentCommands);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPost("simulation/agent/commands", Handle)
                .RequireAuthorization(Policies.SimulationAgent)
                .WithTags("Simulation")
                .WithSummary("Итоги событий конструктора")
                .WithDescription("Только симулятор (Authorization: Bearer INGEST_TOKEN). applied — событие отправлено в поток, rejected — snapshot системы его не принял.");
    }
}
