using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.Feature.Source.Import;

/// <summary>Последние импорты истории</summary>
[UsedImplicitly]
public class GetImports
{
    private static async Task<Ok<IReadOnlyList<ImportView>>> Handle(ISourceImports imports, CancellationToken cancellationToken) =>
        TypedResults.Ok(await imports.ListAsync(20, cancellationToken));

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetImports);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("source/imports", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("Source")
                .WithSummary("Последние импорты истории событий (новые сверху): статус, прогресс по кускам, исходы");
    }
}

/// <summary>Импорт истории по номеру</summary>
[UsedImplicitly]
public class GetImport
{
    private static async Task<Results<Ok<ImportView>, ProblemHttpResult>> Handle(long id, ISourceImports imports, CancellationToken cancellationToken) =>
        await imports.GetAsync(id, cancellationToken) is { } view
            ? TypedResults.Ok(view)
            : AppProblems.Create(StatusCodes.Status404NotFound, "Импорт не найден", "import_not_found");

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetImport);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("source/imports/{id:long}", Handle)
                .RequireAuthorization(Policies.ImportStatus)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .WithTags("Source")
                .WithSummary("Импорт истории: статус, куски, исходы и первые ошибки (номер строки и путь поля)");
    }
}
