using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Customers.List;

/// <summary>Получение списка клиентов с оценками риска; фильтрация и сортировка рейтинга</summary>
[UsedImplicitly]
public class GetCustomers
{
    private static async Task<Results<Ok<CustomerListResponse>, ProblemHttpResult>> Handle(
        [AsParameters] CustomerListQuery query, IGenerationReader generations, ICustomerResultsRepository results, CancellationToken cancellationToken)
    {
        var lookup = await generations.ResolveAsync(query.GenerationId, cancellationToken);
        if (lookup.Context is not { } ctx) return AppProblems.Generation(lookup.Error);

        var criteria = query.ToCriteria();
        var page = await results.ListAsync(ctx.Generation.Id, criteria, cancellationToken);
        var items = page.Rows.Select(r => r.ToListItem()).ToList();
        return TypedResults.Ok(new CustomerListResponse(ctx.Info, ctx.Pending, criteria.Page, criteria.PageSize, page.Total,
            criteria.SortBy, criteria.Ascending ? "asc" : "desc", items));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetCustomers);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("customers", Handle)
                .AddEndpointFilter<ValidationFilter<CustomerListQuery>>()
                .RequireAuthorization(Policies.Reader)
                .ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status410Gone)
                .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
                .WithTags("Customers")
                .WithSummary("Получение списка клиентов с оценками риска; фильтрация и сортировка рейтинга")
                .WithDescription("Сортировка по всему набору на сервере: sortBy = id | email | balance | totalReplenished | riskScore | atRisk | fullChurnRisk | paymentChange, sortDirection = asc | desc." +
                                 "Равенства — по id ↑; «нет оценки» (riskScore = null) и пустые балансы — всегда в конце. Email сравнивается без учёта регистра (байтовый порядок)." +
                                 "Фильтры: riskLevel (повторяемый), scoreState, flag, search (ID или часть email), fullChurnLevel (повторяемый), fullChurnState, paymentTrend (повторяемый)." +
                                 "Все строки — из одного поколения (generation.id).");
    }
}
