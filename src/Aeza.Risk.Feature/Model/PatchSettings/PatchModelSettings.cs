using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Live;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Settings;
using Aeza.Risk.SharedKernel.Abstraction;
using Aeza.Risk.SharedKernel.Json;

using FluentValidation;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Model.PatchSettings;

/// <summary>Изменение настроек модели с автоматическим пересчётом результатов</summary>
[UsedImplicitly]
public class PatchModelSettings
{
    private const int MaxCommentLength = 500;

    private static async Task<Results<Ok<SettingsAccepted>, Accepted<SettingsAccepted>, ProblemHttpResult, ValidationProblem>> Handle(
        [FromBody] SettingsPatch patch, HttpContext http, IModelSettingsRepository settings, IRecalcSignal signal, ILiveNotifier live,
        CancellationToken cancellationToken)
    {
        var actor = http.User.Identity?.Name ?? "unknown";
        var result = await settings.AppendAsync(patch.BaseRevision, current =>
        {
            var merged = ModelSettingsRules.Merge(current, patch);
            var errors = ModelSettingsRules.Validate(merged);
            if (errors.Count > 0) return new ModelChangeDecision.Invalid(errors);
            if (AppJson.Serialize(ModelSettingsDto.From(merged)) == AppJson.Serialize(ModelSettingsDto.From(current))) return new ModelChangeDecision.Unchanged();
            var comment = patch.Comment is { Length: > 0 } c ? c[..Math.Min(c.Length, MaxCommentLength)] : null;
            return new ModelChangeDecision.Change(merged, comment);
        }, actor, cancellationToken);

        switch (result.Outcome)
        {
            case ModelAppendOutcome.Conflict:
                return AppProblems.Create(StatusCodes.Status409Conflict, "Настройки уже изменены другой ревизией", "revision_conflict",
                    new Dictionary<string, object?> { ["latestRevision"] = result.Latest.Id, ["latest"] = result.Latest.ToDto() });
            case ModelAppendOutcome.Invalid:
                return AppProblems.Validation(result.Errors!, "Настройки не прошли проверку; действующая ревизия не изменена", "validation_failed");
            case ModelAppendOutcome.Unchanged:
                return TypedResults.Ok(new SettingsAccepted(result.Latest.Id, null, "unchanged"));
            default:
                signal.Notify();
                await live.JobQueuedAsync(new JobQueuedEvent(result.JobId!.Value, "model", result.NewRevision, null), cancellationToken);
                return TypedResults.Accepted((string?)null, new SettingsAccepted(result.NewRevision!.Value, result.JobId, "pending"));
        }
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(PatchModelSettings);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPatch("model/settings", Handle)
                .RequireAuthorization(Policies.Admin)
                .AddEndpointFilter<CsrfFilter>()
                .AddEndpointFilter<ValidationFilter<SettingsPatch>>()
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .WithTags("Model")
                .WithSummary("Изменение настроек модели с автоматическим пересчётом результатов")
                .WithDescription("Новая неизменяемая ревизия + durable job в одной транзакции → 202. Невалидно → 422, действующая ревизия не меняется. baseRevision устарел → 409.");
    }
}

/// <summary>Валидатор для <see cref="SettingsPatch"/>: форма запроса; допустимость итоговых настроек проверяет модель (422)</summary>
internal sealed class SettingsPatchValidator : AbstractValidator<SettingsPatch>
{
    public SettingsPatchValidator()
    {
        RuleFor(p => p.BaseRevision)
            .GreaterThanOrEqualTo(1)
            .WithMessage("baseRevision — номер ревизии, на которой основано изменение (≥ 1).")
            .OverridePropertyName("baseRevision");
    }
}
