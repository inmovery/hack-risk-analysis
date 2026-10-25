using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Feature.Source.Events;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Customers.Events;

/// <summary>Журнал источника по клиенту</summary>
/// <param name="Coverage">Покрытие журнала</param>
/// <param name="Items">События аккаунта, услуг и транзакций клиента (новые сверху)</param>
[PublicAPI]
public sealed record CustomerEventsResponse(StreamCoverage Coverage, IReadOnlyList<JournalItemDto> Items);

/// <summary>Журнал источника по клиенту: что и когда изменилось в его аккаунте, услугах и платежах</summary>
[UsedImplicitly]
public class GetCustomerEvents
{
    /// <param name="id">Идентификатор клиента</param>
    /// <param name="limit">Сколько событий (1–200, по умолчанию 50)</param>
    /// <param name="journal">Журнал</param>
    /// <param name="cancellationToken">Отмена</param>
    private static async Task<Results<Ok<CustomerEventsResponse>, ProblemHttpResult>> Handle(long id, int? limit, ISourceStreamRepository journal, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200) return AppProblems.Validation(new Dictionary<string, string[]> { ["limit"] = ["limit от 1 до 200."] });
        var coverage = await journal.GetCoverageAsync(cancellationToken);
        var items = await journal.ListAsync(null, limit ?? 50, null, null, id, cancellationToken);
        return TypedResults.Ok(new CustomerEventsResponse(coverage, items.Select(i => i.ToDto()).ToList()));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetCustomerEvents);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("customers/{id:long}/events", Handle)
                .RequireAuthorization(Policies.Reader)
                .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
                .WithTags("Customers")
                .WithSummary("Журнал источника по клиенту: изменения аккаунта, услуг и платежей по времени получения");
    }
}
