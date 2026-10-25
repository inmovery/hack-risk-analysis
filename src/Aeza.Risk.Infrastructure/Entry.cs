using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Infrastructure.BackgroundJobs;
using Aeza.Risk.Infrastructure.Source;
using Aeza.Risk.Infrastructure.Live;
using Aeza.Risk.Infrastructure.Metrics;
using Aeza.Risk.Infrastructure.Security;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aeza.Risk.Infrastructure;

/// <summary>Регистрация инфраструктурных сервисов: безопасность, live-канал, фоновый пересчёт, метрики</summary>
public static class Entry
{
    /// <summary>Хеширование паролей и серверные сессии</summary>
    public static IServiceCollection AddSecurityServices(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<IPasswordHasher, PasswordHasherService>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<SessionStore>());
        services.AddSingleton<ILoginThrottle, LoginThrottle>();
        return services;
    }

    /// <summary>SignalR, реестр соединений сессий, канал событий между репликами (NOTIFY → LISTEN → SignalR) и закрытие соединений истёкших сессий</summary>
    public static IServiceCollection AddLiveUpdates(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<HubConnectionRegistry>();
        services.AddSingleton<ILiveSessions>(sp => sp.GetRequiredService<HubConnectionRegistry>());
        services.AddSingleton<ILiveNotifier, PgLiveNotifier>();
        services.AddSingleton<LiveBridgeState>();
        services.AddHostedService<LiveBridge>();
        services.AddSingleton<HubSessionSweeper>();
        services.AddHostedService(sp => sp.GetRequiredService<HubSessionSweeper>());
        services.AddHealthChecks().AddCheck<LiveChannelHealthCheck>("live-channel", tags: [Health.ReadinessHealthCheck.Tag]);
        return services;
    }

    /// <summary>Durable-пересчёт: сигнал и worker-лидер</summary>
    public static IServiceCollection AddRecalcJobs(this IServiceCollection services)
    {
        services.AddSingleton<RecalcSignal>();
        services.AddSingleton<IRecalcSignal>(sp => sp.GetRequiredService<RecalcSignal>());
        services.AddHostedService<RecalcWorker>();
        // Пакетный импорт истории событий: тот же приём, один пересчёт в конце
        services.AddSingleton<ImportSignal>();
        services.AddHostedService<ImportWorker>();
        services.AddScoped<ISourceImports, SourceImports>();
        return services;
    }

    /// <summary>Маршрут hub-а live-обновлений (только с действующей сессией)</summary>
    public static IEndpointRouteBuilder MapLiveUpdates(this IEndpointRouteBuilder app)
    {
        app.MapHub<RiskHub>(RiskHub.Path).RequireAuthorization(Policies.Reader);
        return app;
    }
}
