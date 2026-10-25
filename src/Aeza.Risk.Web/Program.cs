using Aeza.Risk.Web.Configuration.Swagger;

using JetBrains.Annotations;

using Microsoft.Extensions.Options;

using NLog.Web;

namespace Aeza.Risk.Web;

/// <summary>Точка входа веб-сервиса</summary>
[UsedImplicitly]
public class Program
{
    private static readonly ILogger SystemLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<Program>();

    /// <summary>Запуск: веб-сервис; `healthcheck` — проверка /health/ready для HEALTHCHECK в chiseled-образе (без shell/curl)</summary>
    public static async Task<int> Main(string[] args)
    {
        if (args is ["healthcheck", ..]) return await HealthcheckAsync();
        try
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnTaskSchedulerOnUnobservedTaskException;

            await RunWebApplication(args);
            return 0;
        }
        catch (Exception exception) when (exception.GetType().Name != "HostAbortedException")
        {
            SystemLogger.LogCritical(exception, "Critical error in Main");
            throw;
        }
    }

    /// <summary>Сборка и запуск приложения: appsettings.json → appsettings.{env}.json → переменные ENV_* → user secrets → аргументы</summary>
    [UsedImplicitly]
    public static async Task RunWebApplication(string[] args)
    {
        // AEZA_WEB_ROOT позволяет локально отдавать собранный web/dist без копирования в wwwroot
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = Environment.GetEnvironmentVariable("AEZA_WEB_ROOT") });
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "ENV_")
            .AddUserSecrets<Program>(optional: true)
            .AddCommandLine(args);

        builder.Logging.ClearProviders();
        builder.Host.UseNLog(new NLogAspNetCoreOptions { RemoveLoggerFactoryFilter = false });

        var startup = new Startup(builder.Configuration);
        startup.ConfigureServices(builder.Services);
        var app = builder.Build();
        startup.Configure(app, app.Environment, app.Lifetime, app.Logger, app.Services.GetRequiredService<IOptions<AppSwaggerOptions>>());

        await app.RunAsync();
    }

    private static async Task<int> HealthcheckAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var url = Environment.GetEnvironmentVariable("HEALTHCHECK_URL") ?? "http://127.0.0.1:5000/health/ready";
        try
        {
            return (await http.GetAsync(new Uri(url))).IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }

    private static void OnTaskSchedulerOnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        eventArgs.SetObserved();
        eventArgs.Exception.Flatten().Handle(ex =>
        {
            SystemLogger.LogError(ex, "Unhandled exception in Task Scheduler handler");
            return true;
        });
    }

    private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        if (e.IsTerminating)
        {
            SystemLogger.LogCritical(ex, "Service terminating with fatal exception");
            return;
        }
        SystemLogger.LogError(ex, "Unhandled exception in global handler");
    }
}
