using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Infrastructure.Data.Repositories;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Aeza.Risk.Infrastructure.Data;

/// <summary>Регистрация хранилища PostgreSQL</summary>
public static class Entry
{
    /// <summary>Источник соединений, DbContext, репозитории и ключи DataProtection в БД</summary>
    /// <param name="serviceCollection">serviceCollection</param>
    /// <param name="connectionString">Строка подключения (App:DbConnectionString)</param>
    public static IServiceCollection AddPostgreSqlStorage(this IServiceCollection serviceCollection, string connectionString)
    {
        // NpgsqlDataSource — singleton: все соединения из общего пула; DI закроет пул при остановке
        serviceCollection.AddSingleton(_ => new NpgsqlDataSourceBuilder(ConnectionStrings.Normalize(connectionString)).Build());
        // Канал live-событий между репликами слушает отдельное постоянное соединение (Live.LiveBridge)
        serviceCollection.AddSingleton(new Live.ListenConnectionString(ConnectionStrings.ForListen(connectionString)));
        serviceCollection.AddDbContext<AppDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), o => o.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)));

        serviceCollection.AddScoped<IUserRepository, UserRepository>();
        serviceCollection.AddScoped<IAuditLog, AuditLog>();
        serviceCollection.AddScoped<IGenerationReader, GenerationReader>();
        serviceCollection.AddScoped<ICustomerResultsRepository, CustomerResultsRepository>();
        serviceCollection.AddScoped<IAnalyticsRepository, AnalyticsRepository>();
        serviceCollection.AddScoped<IModelSettingsRepository, ModelSettingsRepository>();
        serviceCollection.AddScoped<ISnapshotRepository, SnapshotRepository>();
        serviceCollection.AddScoped<IRecalcJobRepository, RecalcJobRepository>();
        serviceCollection.AddScoped<ISourceStreamRepository, SourceStreamRepository>();
        serviceCollection.AddScoped<IBehaviorRepository, BehaviorRepository>();
        serviceCollection.AddScoped<IMlModels, Ml.MlModelStore>();
        serviceCollection.AddSingleton<Ml.OnnxShadowRuntime>();
        serviceCollection.AddScoped<DataSeeder>();

        serviceCollection.AddDataProtection().PersistKeysToDbContext<AppDbContext>().SetApplicationName("aeza-risk");
        return serviceCollection;
    }
}
