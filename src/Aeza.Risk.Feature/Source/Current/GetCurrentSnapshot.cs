using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Source.Current;

/// <summary>Текущие тестовые данные: provenance, свежесть, счётчики</summary>
[UsedImplicitly]
public class GetCurrentSnapshot
{
    private static async Task<Results<Ok<SnapshotInfo>, ProblemHttpResult>> Handle(ISnapshotRepository snapshots, CancellationToken cancellationToken)
    {
        var current = await snapshots.GetCurrentAsync(cancellationToken);
        return current is null
            ? AppProblems.Create(StatusCodes.Status404NotFound, "Тестовые данные ещё не загружены", "no_snapshot")
            : TypedResults.Ok(current.ToInfo(true));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetCurrentSnapshot);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("source/current", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("Source")
                .ProducesProblem(StatusCodes.Status404NotFound)
                .WithSummary("Текущие тестовые данные: provenance, свежесть, счётчики");
    }
}
