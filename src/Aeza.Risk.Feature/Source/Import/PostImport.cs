using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Source;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aeza.Risk.Feature.Source.Import;

/// <summary>Пакетный импорт истории событий (коннектор batch-import): файл JSONL — по событию v1 или v2 в строке, можно gzip</summary>
[UsedImplicitly]
public class PostImport
{
    /// <summary>Лимит файла импорта (история 2000 клиентов × 365 дней — около 0,5 ГБ без сжатия)</summary>
    public const long MaxBodyBytes = 700L * 1024 * 1024;

    private static async Task<Results<Accepted<ImportView>, ProblemHttpResult>> Handle(
        HttpRequest req, bool? synthetic, ISourceImports imports, ILogger<PostImport> log, CancellationToken cancellationToken)
    {
        var feature = req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaxBodyBytes;
        if (req.ContentLength > MaxBodyBytes)
            return AppProblems.Create(StatusCodes.Status413PayloadTooLarge, $"Файл больше {MaxBodyBytes / 1024 / 1024} МБ: сожмите gzip или разделите", "payload_too_large");
        var user = req.HttpContext.User;
        var actor = user.Identity?.Name ?? "unknown";
        try
        {
            var view = await imports.UploadAsync(req.Body, actor, ConnectorNames.BatchImport, synthetic ?? false, cancellationToken);
            log.LogInformation("Import {ImportId} uploaded by {Actor}: {Events} events in {Chunks} chunks, {Bytes} bytes", view.Id, actor, view.EventsTotal, view.ChunksTotal, view.Bytes);
            return TypedResults.Accepted($"/api/v1/source/imports/{view.Id}", view);
        }
        catch (ImportRejectedException ex)
        {
            return AppProblems.Create(StatusCodes.Status422UnprocessableEntity, ex.Message, ex.Code);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return AppProblems.Create(StatusCodes.Status413PayloadTooLarge, $"Файл больше {MaxBodyBytes / 1024 / 1024} МБ", "payload_too_large");
        }
        catch (InvalidDataException)
        {
            return AppProblems.Create(StatusCodes.Status422UnprocessableEntity, "Файл похож на gzip, но не распаковывается", "invalid_gzip");
        }
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(PostImport);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPost("source/events/import", Handle)
                .RequireAuthorization(Policies.SourceWriter)
                .AddEndpointFilter<CsrfFilter>()
                .Accepts<string>("application/x-ndjson", "application/jsonl", "application/gzip", "text/plain")
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
                .WithTags("Source")
                .WithSummary("Пакетный импорт истории событий (JSONL, можно gzip): durable-задание, прогресс — GET /source/imports/{id}")
                .WithDescription("Строка — событие v1 или v2 в том же формате, что и в POST /source/events (без обёртки { events }). ≤ 3 млн строк, ≤ 700 МБ." +
                                 "Файл режется на куски по 5000 строк; куски применяются по порядку, после падения — со следующего куска; повтор файла безопасен (eventId → duplicate)." +
                                 "Пересчёт — один раз в конце. ?synthetic=true — пометить события как события симулятора. Bearer INGEST_TOKEN или admin (cookie + X-CSRF-TOKEN). 202 + номер импорта.");
    }
}
