using Aeza.Risk.Feature.OpenApi;
using Aeza.Risk.SharedKernel.OpenApi;

using Asp.Versioning;

using FluentValidation;

using Microsoft.Extensions.DependencyInjection;

namespace Aeza.Risk.Feature;

/// <summary>Регистрация зависимостей уровня приложения</summary>
public static class Entry
{
    /// <summary>Валидаторы, endpoint-ы вертикальных срезов и версионированная документация OpenAPI</summary>
    /// <param name="serviceCollection">serviceCollection</param>
    public static IServiceCollection AddFeatures(this IServiceCollection serviceCollection)
    {
        serviceCollection.AddValidatorsFromAssemblyContaining<IFeatureMarker>(includeInternalTypes: true);
        serviceCollection.AddEndpoints([typeof(IFeatureMarker).Assembly]);
        serviceCollection.AddVersioningOpenApi();
        return serviceCollection;
    }

    /// <summary>Регистрация поддержки версионирования документации OpenApi</summary>
    private static void AddVersioningOpenApi(this IServiceCollection serviceCollection)
    {
        serviceCollection.AddApiVersioning(options =>
            {
                options.ReportApiVersions = true;
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.DefaultApiVersion = new ApiVersion(1.0);
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            })
            .AddOpenApi(o =>
            {
                o.Document.AddDocumentTransformer<ConfigDocument>();
                o.Document.AddDocumentTransformer<SecuritySchemesTransformer>();
                o.Document.AddOperationTransformer<SecurityRequirementsTransformer>();
                o.Document.CreateSchemaReferenceId = info => info.GetSchemaId();
            });
    }
}
