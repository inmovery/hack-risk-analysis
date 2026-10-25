using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Source;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Source.Events;

/// <summary>Журнал событий источника: новые сверху, без состояния записей</summary>
[UsedImplicitly]
public class GetEvents
{
    private static readonly string[] Kinds = ["account", "service", "transaction"];
    private static readonly string[] Outcomes = [SourceCodes.Applied, SourceCodes.NoChange, SourceCodes.Stale];

    /// <param name="beforeSeq">Страница старше этого номера (для «загрузить ещё»)</param>
    /// <param name="limit">Размер страницы (1–200, по умолчанию 50)</param>
    /// <param name="kind">account | service | transaction</param>
    /// <param name="outcome">applied | no_change | stale</param>
    /// <param name="journal">Журнал</param>
    /// <param name="cancellationToken">Отмена</param>
    private static async Task<Results<Ok<JournalPageDto>, ProblemHttpResult>> Handle(
        long? beforeSeq, int? limit, string? kind, string? outcome, ISourceStreamRepository journal, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (limit is < 1 or > 200) errors["limit"] = ["limit от 1 до 200."];
        if (kind is not null && !Kinds.Contains(kind)) errors["kind"] = ["Допустимо: account, service, transaction."];
        if (outcome is not null && !Outcomes.Contains(outcome)) errors["outcome"] = ["Допустимо: applied, no_change, stale."];
        if (errors.Count > 0) return AppProblems.Validation(errors);
        var take = limit ?? 50;
        var items = await journal.ListAsync(beforeSeq, take, kind, outcome, null, cancellationToken);
        return TypedResults.Ok(new JournalPageDto(items.Select(i => i.ToDto()).ToList(), items.Count == take ? items[^1].Seq : null));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetEvents);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("source/events", Handle)
                .RequireAuthorization(Policies.Reader)
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .WithTags("Source")
                .WithSummary("Журнал событий источника (новые сверху)")
                .WithDescription("Время — момент получения. Возвращаются исход, коды изменений и значения «от → до» без PII; состояние записей не возвращается.");
    }
}
