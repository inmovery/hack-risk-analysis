using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Source;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Source.Validate;

/// <summary>Проверить тестовые данные без сохранения (dry-run)</summary>
[UsedImplicitly]
public class ValidateSnapshot
{
    private static async Task<Results<Ok<ValidationReport>, ProblemHttpResult>> Handle(HttpRequest req, TimeProvider time, CancellationToken cancellationToken)
    {
        var (body, err) = await SnapshotHttp.ReadBodyAsync(req, cancellationToken);
        if (err is not null) return err;
        var parsed = SnapshotParser.Parse(body!);
        var (asOf, assumed, asOfErr) = SnapshotHttp.ResolveAsOf(parsed, req, time);
        if (asOfErr is not null) return asOfErr;
        return TypedResults.Ok(new ValidationReport(parsed.IsValid, parsed.Counts(), parsed.Errors, parsed.ErrorCounts, parsed.Warnings, asOf, assumed));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(ValidateSnapshot);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPost("source/validate", Handle)
                .RequireAuthorization(Policies.SourceWriter)
                .AddEndpointFilter<CsrfFilter>()
                .Accepts<SnapshotEnvelope>("application/json")
                .WithTags("Source")
                .WithSummary("Проверить тестовые данные без сохранения (dry-run)");
    }
}
