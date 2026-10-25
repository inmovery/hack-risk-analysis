using Aeza.Risk.Core.Options;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.SharedKernel.Services;

using JetBrains.Annotations;

using Microsoft.Extensions.Options;

namespace Aeza.Risk.Web.HostedServices;

/// <summary>Инициализация БД до старта worker'ов и приёма запросов</summary>
[UsedImplicitly]
public sealed class BootstrapHostedService(
    ILogger<BootstrapHostedService> logger,
    IServiceScopeFactory serviceScopeFactory,
    IHostEnvironment env,
    IOptions<AppOptions> appOptions,
    IOptions<AuthOptions> authOptions)
    : CriticalHostedService(logger, serviceScopeFactory)
{
    /// <inheritdoc />
    protected override async Task StartAsyncCritical(CancellationToken cancellationToken)
    {
        if (appOptions.Value.AutoMigrate)
            await MigrationsRunner.ApplyMigrations(Logger, ServiceProvider, "Aeza.Risk.Web", cancellationToken);
        var requireStrong = !env.IsDevelopment() && !authOptions.Value.AllowWeakBootstrapPasswords;
        await ServiceProvider.GetRequiredService<DataSeeder>().InitializeAsync(requireStrong, cancellationToken);
    }
}
