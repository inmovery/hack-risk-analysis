using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Jobs.Get;

/// <summary>Статус задания пересчёта</summary>
[PublicAPI]
public sealed record JobInfo(
    long Id, string Reason, string Status, int Attempts, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    long? GenerationId, long? CoalescedInto, string? ErrorCode);

/// <summary>Статус задания пересчёта</summary>
[UsedImplicitly]
public class GetJob
{
    /// <param name="id">Идентификатор задания</param>
    /// <param name="jobs">Задания пересчёта</param>
    /// <param name="cancellationToken">Отмена</param>
    private static async Task<Results<Ok<JobInfo>, NotFound>> Handle(long id, IRecalcJobRepository jobs, CancellationToken cancellationToken)
    {
        var j = await jobs.GetAsync(id, cancellationToken);
        return j is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new JobInfo(j.Id, j.Reason, j.Status, j.Attempts, j.CreatedAt, j.StartedAt, j.FinishedAt, j.GenerationId, j.CoalescedInto, j.ErrorCode));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetJob);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("jobs/{id:long}", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("Jobs")
                .WithSummary("Статус задания пересчёта");
    }
}
