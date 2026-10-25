using System.Text.Json;

using Aeza.Risk.Infrastructure.Data;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aeza.Risk.Infrastructure.Health;

/// <summary>Готовность: БД доступна, миграции применены, есть опубликованное поколение (или ещё нет снимка — сервис готов его принять)</summary>
public sealed class ReadinessHealthCheck(AppDbContext db) : IHealthCheck
{
    /// <summary>Тег проверки готовности</summary>
    public const string Tag = "ready";

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any()) return Unhealthy("migrations_pending");
            var state = await db.AppState.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            // Пустая БД (снимок ещё не загружен) — сервис готов принять снимок; есть снимок, но нет расчёта — ещё не готов
            if (state?.LatestSourceSnapshotId is null) return Healthy("ready_no_data");
            return state.ActiveGenerationId is null ? Unhealthy("computing") : Healthy("ready");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unhealthy("database_unavailable");
        }
    }

    private static HealthCheckResult Healthy(string status) =>
        HealthCheckResult.Healthy(status, new Dictionary<string, object> { ["status"] = status });

    private static HealthCheckResult Unhealthy(string status) =>
        HealthCheckResult.Unhealthy(status, data: new Dictionary<string, object> { ["status"] = status });
}

/// <summary>Регистрация health checks: /health/live (без БД), /health/ready (БД, миграции, поколение)</summary>
public static class Entry
{
    /// <summary>Проверки здоровья</summary>
    public static IServiceCollection AddAppHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<ReadinessHealthCheck>("readiness", tags: [ReadinessHealthCheck.Tag]);
        return services;
    }

    /// <summary>Маршруты проверок: ответ {"status": "..."}</summary>
    public static IEndpointRouteBuilder MapAppHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = (ctx, _) => Write(ctx, "live") })
            .ExcludeFromDescription();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = c => c.Tags.Contains(ReadinessHealthCheck.Tag),
                ResultStatusCodes = { [HealthStatus.Healthy] = 200, [HealthStatus.Degraded] = 200, [HealthStatus.Unhealthy] = 503 },
                // Статус — первой неуспешной проверки (БД/миграции/поколение или канал событий), иначе первой
                ResponseWriter = (ctx, report) => Write(ctx, report.Entries.Values.OrderBy(e => e.Status == HealthStatus.Healthy ? 1 : 0)
                    .Select(e => e.Data.GetValueOrDefault("status") as string).FirstOrDefault() ?? "unknown"),
            })
            .ExcludeFromDescription();
        return app;
    }

    private static Task Write(HttpContext ctx, string status)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new { status }));
    }
}
