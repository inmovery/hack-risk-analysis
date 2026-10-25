using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Live;
using Aeza.Risk.Core.Security;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aeza.Risk.Feature.Ml.Model;

/// <summary>Загрузка и включение теневой ML-модели: пакет model-bundle.zip от tools/Aeza.Risk.Ml (model.onnx + model.json [+ report.md])</summary>
[UsedImplicitly]
public class PutMlModel
{
    /// <summary>Лимит пакета</summary>
    public const long MaxBodyBytes = 50L * 1024 * 1024;

    private static async Task<Results<Accepted<MlModelActivated>, ProblemHttpResult>> Handle(
        HttpRequest req, IMlModels models, IRecalcSignal signal, ILiveNotifier live, ILogger<PutMlModel> log, CancellationToken cancellationToken)
    {
        var feature = req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaxBodyBytes;
        if (req.ContentLength > MaxBodyBytes)
            return AppProblems.Create(StatusCodes.Status413PayloadTooLarge, $"Пакет больше {MaxBodyBytes / 1024 / 1024} МБ", "payload_too_large");
        var actor = req.HttpContext.User.Identity?.Name ?? "unknown";
        try
        {
            var activated = await models.UploadAsync(req.Body, actor, cancellationToken);
            signal.Notify();
            await live.JobQueuedAsync(new JobQueuedEvent(activated.JobId, "ml_model", null, null), cancellationToken);
            log.LogInformation("Shadow ML model {ModelId} {Version} activated by {Actor}, job {JobId}", activated.Model.Id, activated.Model.Version, actor, activated.JobId);
            return TypedResults.Accepted("/api/v1/ml/status", activated);
        }
        catch (MlModelRejectedException ex)
        {
            var status = ex.Code == "payload_too_large" ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status422UnprocessableEntity;
            return AppProblems.Create(status, ex.Message, ex.Code);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return AppProblems.Create(StatusCodes.Status413PayloadTooLarge, $"Пакет больше {MaxBodyBytes / 1024 / 1024} МБ", "payload_too_large");
        }
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(PutMlModel);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapPut("ml/model", Handle)
                .RequireAuthorization(Policies.Admin)
                .AddEndpointFilter<CsrfFilter>()
                .Accepts<string>("application/zip", "application/octet-stream")
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
                .WithTags("ML")
                .WithSummary("Загрузить и включить теневую ML-модель (zip: model.onnx + model.json от tools/Aeza.Risk.Ml)")
                .WithDescription("Проверки: схема и порядок признаков совпадают с анализатором (aeza-ml-features-v1), SHA-256 ONNX из карточки, ONNX Runtime открывает модель" +
                                 "и даёт вероятности 0..1 на проверочных строках. Не прошло → 422 с кодом, включённая модель не меняется. Прошло → модель включается" +
                                 "в тени и ставится пересчёт (202). Тень не меняет баллы, уровни, сортировки, деньги и /metrics правил. Только admin (cookie + X-CSRF-TOKEN).");
    }
}
