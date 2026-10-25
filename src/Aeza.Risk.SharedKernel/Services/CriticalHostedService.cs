using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aeza.Risk.SharedKernel.Services;

/// <summary>Критичный hosted-сервис: ошибка при старте останавливает приложение с ненулевым кодом выхода</summary>
public abstract class CriticalHostedService : IHostedService, IDisposable
{
    private readonly IServiceScope _serviceScope;

    /// <summary>Логгер сервиса</summary>
    protected ILogger Logger { get; }

    /// <summary>Провайдер собственного scope сервиса</summary>
    protected IServiceProvider ServiceProvider { get; }

    /// <summary>Создаёт сервис с собственным scope зависимостей</summary>
    protected CriticalHostedService(ILogger logger, IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScope = serviceScopeFactory.CreateScope();
        Logger = logger;
        ServiceProvider = _serviceScope.ServiceProvider;
    }

    /// <summary>Критичная часть старта</summary>
    protected abstract Task StartAsyncCritical(CancellationToken cancellationToken);

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StartAsyncCritical(cancellationToken);
        }
        catch (Exception exception)
        {
            FatalExit(exception);
            throw;
        }
    }

    /// <inheritdoc />
    public virtual Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Освобождает scope зависимостей</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing) _serviceScope.Dispose();
    }

    private void FatalExit(Exception exception)
    {
        Logger.LogCritical(exception, "Critical application hosted service error in {Name}", GetType().Name);
        Environment.ExitCode = -1;
    }
}
