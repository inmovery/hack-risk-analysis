using Microsoft.AspNetCore.Routing;

namespace Aeza.Risk.SharedKernel.Abstraction;

/// <summary>Endpoint вертикального среза: регистрируется сканированием сборки (Scrutor) и маппится в группу версии API</summary>
public interface IEndpoint
{
    /// <summary>Регистрирует маршрут в группе версии API</summary>
    void MapEndpoint(IEndpointRouteBuilder app);
}
