using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Source.Snapshots;

/// <summary>История загрузок тестовых данных (последние 50)</summary>
[UsedImplicitly]
public class GetSnapshots
{
    private static async Task<Ok<List<SnapshotInfo>>> Handle(ISnapshotRepository snapshots, CancellationToken cancellationToken)
    {
        var list = await snapshots.ListAsync(50, cancellationToken);
        return TypedResults.Ok(list.Select(x => x.Snapshot.ToInfo(x.Active)).ToList());
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetSnapshots);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("source/snapshots", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("Source")
                .WithSummary("История загрузок тестовых данных (последние 50)");
    }
}
