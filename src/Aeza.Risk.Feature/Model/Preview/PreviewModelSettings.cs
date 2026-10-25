using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Settings;
using Aeza.Risk.SharedKernel.Abstraction;
using Aeza.Risk.SharedKernel.Json;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Model.Preview;

/// <summary>Предпросмотр черновика настроек без сохранения (what-if на текущих данных источника)</summary>
[UsedImplicitly]
public class PreviewModelSettings
{
    private static readonly string[] Levels = ["high", "medium", "low", "unknown"];

    private static async Task<Results<Ok<PreviewResponse>, ProblemHttpResult>> Handle([FromBody] SettingsPatch patch, IModelSettingsRepository settings,
        ISnapshotRepository snapshots, ICustomerResultsRepository results, IBehaviorRepository behavior, RiskEngine engine, CancellationToken cancellationToken)
    {
        var state = await settings.GetStateAsync(cancellationToken);
        if (state.Active is not { } gen)
            return AppProblems.Create(StatusCodes.Status409Conflict, "Нет активного поколения для сравнения", "no_generation");
        var merged = ModelSettingsRules.Merge(AppJson.Deserialize<ModelSettingsDto>(state.Latest.SettingsJson).ToModel(), patch);
        var errors = ModelSettingsRules.Validate(merged);
        if (errors.Count > 0) return AppProblems.Validation(errors);

        var snapshot = await snapshots.GetAsync(gen.SourceSnapshotId, cancellationToken);
        var asOf = merged.Clock.Mode == ClockMode.Source ? snapshot.SourceAsOf : gen.AsOf;
        var data = await snapshots.LoadCurrentAsync(cancellationToken); // текущее состояние источника: тестовые данные ⊕ события
        var accounts = (await behavior.LoadAsync(asOf, cancellationToken)).Accounts; // те же признаки поведения, что и в пересчёте
        var after = engine.Evaluate(data, merged, asOf, accounts);
        var before = await results.GetScoresAsync(gen.Id, cancellationToken);
        var changes = after.Customers
            .Select(c => (c, b: before.GetValueOrDefault(c.Account.Id)))
            .Where(x => x.b is not null && (x.b.Level != x.c.Level.ToApi() || x.b.AtRiskMinor != x.c.Money.AtRiskMinor || x.b.Score != x.c.Score))
            .Select(x => new LevelShift(x.c.Account.Id, x.c.Account.Email, x.b!.Score, x.c.Score, x.b.Level, x.c.Level.ToApi(), x.b.AtRiskMinor, x.c.Money.AtRiskMinor))
            .OrderByDescending(s => s.LevelBefore != s.LevelAfter)
            .ThenByDescending(s => Math.Abs((s.ScoreAfter ?? 0) - (s.ScoreBefore ?? 0)))
            .ThenBy(s => s.CustomerId)
            .ToList();
        return TypedResults.Ok(new PreviewResponse(asOf, gen.Id,
            Levels.ToDictionary(l => l, l => before.Values.Count(b => b.Level == l)), after.Summary.ByLevel,
            before.Values.Sum(b => b.AtRiskMinor), after.Summary.AtRiskMinor, changes.Count(c => c.LevelBefore != c.LevelAfter), changes.Take(50).ToList()));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(PreviewModelSettings);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPost("model/preview", Handle)
                .RequireAuthorization(Policies.Admin)
                .AddEndpointFilter<CsrfFilter>()
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .WithTags("Model")
                .WithSummary("Предпросмотр черновика настроек без сохранения (what-if на текущих данных источника)");
    }
}
