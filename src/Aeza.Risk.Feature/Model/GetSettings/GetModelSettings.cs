using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Model.GetSettings;

/// <summary>Получение действующих настроек и коэффициентов модели</summary>
[UsedImplicitly]
public class GetModelSettings
{
    private static async Task<Ok<ModelSettingsResponse>> Handle(IModelSettingsRepository settings, CancellationToken cancellationToken)
    {
        var state = await settings.GetStateAsync(cancellationToken);
        var active = state.Active is { } gen ? new ActiveModelDto(gen.ModelRevisionId, gen.Id, gen.AsOf) : null;
        return TypedResults.Ok(new ModelSettingsResponse(state.Latest.ToDto(), active,
            FeatureCatalog.All.Select(f => new FeatureMeta(f.Key, f.Title, f.Description, f.DefaultWeight, f.Behavioral)).ToList(),
            new SettingsLimits(ModelSettingsValidator.MaxHorizonDays, ModelSettingsValidator.MaxUrgencyWindowDays, 0, 100, 1, 60,
                ModelSettingsValidator.MaxNonRenewalDays, ModelSettingsValidator.MinPaymentWindowDays, ModelSettingsValidator.MaxPaymentWindowDays),
            state.Pending));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetModelSettings);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("model/settings", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("Model")
                .WithSummary("Получение действующих настроек и коэффициентов модели")
                .WithDescription("latest — последняя сохранённая ревизия; active — ревизия, по которой посчитано опубликованное поколение. Если они различаются, идёт пересчёт.");
    }
}
