using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.SharedKernel.Abstraction;

/// <summary>Базовый класс endpoint-а, принуждает задавать имя</summary>
public abstract class EndpointBase : IEndpoint
{
    /// <summary>Имя endpoint-а (используется для генерации ссылок и как operationId в OpenAPI)</summary>
    protected abstract string Name { get; }

    /// <inheritdoc />
    void IEndpoint.MapEndpoint(IEndpointRouteBuilder app)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        MapEndpointCore(app).WithName(Name);
    }

    /// <summary>Регистрирует маршрут</summary>
    protected abstract RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app);
}
