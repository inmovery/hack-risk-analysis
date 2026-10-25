using System.Text.Json.Serialization;

using Aeza.Risk.Core;
using Aeza.Risk.Core.Options;
using Aeza.Risk.Feature;
using Aeza.Risk.Infrastructure;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.Infrastructure.Health;
using Aeza.Risk.Infrastructure.Metrics;
using Aeza.Risk.Infrastructure.Source;
using Aeza.Risk.Web.Configuration.Swagger;
using Aeza.Risk.Web.Core;
using Aeza.Risk.Web.HostedServices;

using JetBrains.Annotations;

using Microsoft.Extensions.Options;


namespace Aeza.Risk.Web;

/// <summary>Composition root: регистрация зависимостей и конвейер запросов</summary>
public class Startup
{
    private readonly string _basePath;

    /// <summary>Создаёт Startup над конфигурацией приложения</summary>
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
        _basePath = string.IsNullOrWhiteSpace(Configuration["GlobalPrefix"])
            ? ""
            : $"/{Configuration["GlobalPrefix"]!.Trim('/')}";
    }

    private IConfiguration Configuration { get; }

    /// <summary>Регистрация зависимостей</summary>
    public void ConfigureServices(IServiceCollection services)
    {
        // Инициализация БД (миграции в разработке, стартовая ревизия, пользователи, seed) регистрируется первой
        services.AddHostedService<BootstrapHostedService>();
        services.AddDomain();
        services.AddCorsPolicy(Configuration.GetSection("CorsOrigins").Get<string[]>());
        services.AddPostgreSqlStorage(Configuration["App:DbConnectionString"] ?? "");
        services.AddSecurityServices();
        services.AddAppAuthentication(Configuration);
        services.AddBusinessMetrics(Configuration.GetSection(MetricsOptions.Section).Get<MetricsOptions>() ?? new MetricsOptions());
        services.AddAppHealthChecks();
        services.AddLiveUpdates();
        services.AddRecalcJobs();
        services.AddSourceConnectors();
        services.AddFeatures();

        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddEndpointsApiExplorer();
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        services.Configure<AppSwaggerOptions>(Configuration);
    }

    /// <summary>Конвейер запросов</summary>
    [UsedImplicitly]
    public void Configure(WebApplication app, IWebHostEnvironment env, IHostApplicationLifetime lifetime, ILogger logger, IOptions<AppSwaggerOptions> swaggerOptions)
    {
        RegisterLifetimeLogging(lifetime, logger);
        app.UsePathBase(new PathString(_basePath));
        app.UseProxyHeaders(Configuration);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseSecurityHeaders();
        // /metrics: бизнес-метрики OpenTelemetry без авторизации и вне Swagger (до SPA и маршрутизации)
        app.UseBusinessMetricsEndpoint(app.Services.GetRequiredService<IOptions<MetricsOptions>>().Value);

        var spaEnabled = Configuration.GetValue("Spa:Enabled", true);
        if (spaEnabled) app.UseSpaStaticFiles();

        app.UseRouting();
        app.UseCorsPolicy();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        var useSwagger = swaggerOptions.Value.UseSwagger;
        if (useSwagger) app.UseApiDocs(Configuration);

        app.MapEndpoints();
        app.MapAppHealthChecks();
        app.MapLiveUpdates();
        app.MapApiFallbacks(spaEnabled);
    }

    private static void RegisterLifetimeLogging(IHostApplicationLifetime lifetime, ILogger logger)
    {
        lifetime.ApplicationStarted.Register(() => logger.LogInformation("Service started"));
        lifetime.ApplicationStopped.Register(() => logger.LogInformation("Service stopped"));
    }
}
