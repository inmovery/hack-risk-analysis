using System.Reflection;

using Aeza.Risk.SharedKernel.Abstraction;

using Asp.Versioning;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Aeza.Risk.Feature;

/// <summary>Обнаружение и маппинг endpoint-ов вертикальных срезов</summary>
public static class EndpointExtensions
{
    /// <summary>Регистрирует endpoint-ы сборок (Scrutor)</summary>
    [PublicAPI]
    public static IServiceCollection AddEndpoints(this IServiceCollection serviceCollection, Assembly[] assemblies)
    {
        return serviceCollection.Scan(scan => scan
            .FromAssemblies(assemblies)
            .AddClasses(classes => classes.AssignableTo<IEndpoint>())
            .AsImplementedInterfaces()
            .WithTransientLifetime());
    }

    /// <summary>Маппит endpoint-ы в группы версий: /api/v{version}</summary>
    [PublicAPI]
    public static IApplicationBuilder MapEndpoints(this WebApplication app)
    {
        var endpoints = app.Services
            .GetRequiredService<IEnumerable<IEndpoint>>()
            .ToList();

        var allVersions = endpoints
            .SelectMany(GetVersions)
            .Distinct()
            .Order()
            .ToArray();

        var versioningApi = app.NewVersionedApi("api");

        foreach (var version in allVersions)
        {
            var group = versioningApi
                .MapGroup("api/v{version:apiVersion}")
                .HasApiVersion(version);

            foreach (var endpoint in endpoints.Where(e => GetVersions(e).Contains(version)))
            {
                endpoint.MapEndpoint(group);
            }
        }

        return app;
    }

    private static ApiVersion[] GetVersions(IEndpoint endpoint)
    {
        var attrs = endpoint
            .GetType()
            .GetCustomAttributes<ApiVersionAttribute>()
            .SelectMany(a => a.Versions)
            .ToArray();

        // Нет атрибута — v1 по умолчанию
        return attrs.Length > 0 ? attrs : [new ApiVersion(1.0)];
    }
}
