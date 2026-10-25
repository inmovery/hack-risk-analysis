using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aeza.Risk.Infrastructure.Data;

/// <summary>Применение миграций БД (Migrator; в веб-приложении — только при App:AutoMigrate)</summary>
public static class MigrationsRunner
{
    /// <summary>Применить все ожидающие миграции</summary>
    public static async Task ApplyMigrations(ILogger logger, IServiceProvider serviceProvider, string appName, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString()[..4];
        logger.LogInformation("{AppName}:UpdateDatabase:{OperationId}: starting...", appName, operationId);
        try
        {
            using var serviceScope = serviceProvider.CreateScope();
            var dbContext = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("{AppName}:UpdateDatabase:{OperationId}: successfully done", appName, operationId);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "{AppName}:UpdateDatabase:{OperationId}: Migration failed", appName, operationId);
            throw;
        }
    }
}
