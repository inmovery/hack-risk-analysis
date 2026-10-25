using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Generations;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;
using Aeza.Risk.SharedKernel.Json;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Customers.Get;

/// <summary>Карточка клиента: деталь поколения</summary>
[PublicAPI]
public sealed record CustomerDetailResponse(GenerationInfo Generation, PendingInfo? Pending, CustomerDetailBody Detail);

/// <summary>Получение подробной оценки клиента, его услуг, признаков риска и объяснений</summary>
[UsedImplicitly]
public class GetCustomer
{
    /// <summary>Карточка читает ровно то, что посчитал движок для поколения</summary>
    /// <param name="id">Идентификатор клиента</param>
    /// <param name="generationId">Явное поколение (по умолчанию — активное)</param>
    /// <param name="generations">Выбор поколения</param>
    /// <param name="results">Результаты клиентов</param>
    /// <param name="cancellationToken">Отмена</param>
    private static async Task<Results<Ok<CustomerDetailResponse>, ProblemHttpResult>> Handle(long id, long? generationId,
        IGenerationReader generations, ICustomerResultsRepository results, CancellationToken cancellationToken)
    {
        var lookup = await generations.ResolveAsync(generationId, cancellationToken);
        if (lookup.Context is not { } ctx) return AppProblems.Generation(lookup.Error);
        var json = await results.GetDetailJsonAsync(ctx.Generation.Id, id, cancellationToken);
        if (json is null) return AppProblems.Create(StatusCodes.Status404NotFound, "Клиент не найден в текущем поколении", "customer_not_found");
        return TypedResults.Ok(new CustomerDetailResponse(ctx.Info, ctx.Pending, AppJson.Deserialize<CustomerDetailBody>(json)));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetCustomer);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("customers/{id:long}", Handle)
                .RequireAuthorization(Policies.Reader)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status410Gone)
                .WithTags("Customers")
                .WithSummary("Получение подробной оценки клиента, его услуг, признаков риска и объяснений");
    }
}
