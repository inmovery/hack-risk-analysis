using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.Infrastructure.Data.Repositories;
using Aeza.Risk.Infrastructure.Security;

using JetBrains.Annotations;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NLog.Extensions.Logging;

namespace Aeza.Risk.Migrator;

/// <summary>Консольное приложение для миграций БД отдельным шагом</summary>
[UsedImplicitly]
public static class Program
{
    private const string AppName = "Aeza.Risk.Migrator";

    /// <summary>Точка входа</summary>
    public static async Task<int> Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables(prefix: "ENV_")
            .Build();

        await using var serviceProvider = CreateServices(configuration);
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(AppName);
        try
        {
            if (args is ["set-password", var login, ..])
            {
                var password = Environment.GetEnvironmentVariable("NEW_PASSWORD");
                if (string.IsNullOrEmpty(password))
                {
                    logger.LogError("Usage: NEW_PASSWORD=... set-password <login>");
                    return 2;
                }
                using var scope = serviceProvider.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<UserAdministration>().SetPasswordAsync(login, password, CancellationToken.None);
            }

            await MigrationsRunner.ApplyMigrations(logger, serviceProvider, AppName);
            using (var scope = serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await DbRoles.EnsureAppRoleAsync(db, configuration["App:AppDbUser"], configuration["App:AppDbPassword"], logger);
            }
            return 0;
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "{AppName}: critical error", AppName);
            return 1;
        }
        finally
        {
            NLog.LogManager.Shutdown();
        }
    }

    private static ServiceProvider CreateServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddNLog(new NLogProviderOptions { RemoveLoggerFactoryFilter = false });
        });
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(ConnectionStrings.Normalize(configuration["App:DbConnectionString"] ?? ""),
            o => o.CommandTimeout(configuration.GetValue("App:MigrationTimeoutInSeconds", 3600))));
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddSingleton<IPasswordHasher, PasswordHasherService>();
        services.AddScoped<UserAdministration>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        Console.Error.WriteLine($"{AppName}: unhandled exception ({(e.ExceptionObject as Exception)?.GetType().Name})");
}
