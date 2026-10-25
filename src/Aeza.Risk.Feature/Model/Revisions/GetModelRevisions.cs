using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Model.Revisions;

/// <summary>История ревизий модели (аудит)</summary>
[UsedImplicitly]
public class GetModelRevisions
{
    private static async Task<Ok<List<RevisionDto>>> Handle(IModelSettingsRepository settings, CancellationToken cancellationToken)
    {
        var list = await settings.ListRevisionsAsync(50, cancellationToken);
        return TypedResults.Ok(list.Select(r => r.ToDto()).ToList());
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetModelRevisions);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("model/revisions", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("Model")
                .WithSummary("История ревизий модели (аудит)");
    }
}
