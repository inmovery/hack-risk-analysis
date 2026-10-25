using System.Reflection;
using System.Text;

using Aeza.Risk.Core.Entities;

using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Aeza.Risk.Infrastructure.Data;

/// <summary>Контекст EF Core приложения (PostgreSQL)</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>Принятые снимки источника</summary>
    public DbSet<SourceSnapshotEntity> SourceSnapshots => Set<SourceSnapshotEntity>();

    /// <summary>Аккаунты снимков</summary>
    public DbSet<SrcAccount> SrcAccounts => Set<SrcAccount>();

    /// <summary>Услуги снимков</summary>
    public DbSet<SrcService> SrcServices => Set<SrcService>();

    /// <summary>Транзакции снимков</summary>
    public DbSet<SrcTransaction> SrcTransactions => Set<SrcTransaction>();

    /// <summary>Ревизии настроек модели</summary>
    public DbSet<ModelRevisionEntity> ModelRevisions => Set<ModelRevisionEntity>();

    /// <summary>Durable-задания пересчёта</summary>
    public DbSet<RecalcJob> RecalcJobs => Set<RecalcJob>();

    /// <summary>Поколения результатов</summary>
    public DbSet<GenerationEntity> Generations => Set<GenerationEntity>();

    /// <summary>Результаты клиентов по поколениям</summary>
    public DbSet<CustomerResultEntity> CustomerResults => Set<CustomerResultEntity>();

    /// <summary>Указатели на активное поколение и последние входы</summary>
    public DbSet<AppStateEntity> AppState => Set<AppStateEntity>();

    /// <summary>Наблюдённые изменения услуг между снимками</summary>
    public DbSet<ObservedServiceEvent> ObservedServiceEvents => Set<ObservedServiceEvent>();

    /// <summary>Журнал изменений записей источника (v1 и state событий v2)</summary>
    public DbSet<SourceEventEntity> SourceEvents => Set<SourceEventEntity>();

    /// <summary>Журнал доменных событий v2</summary>
    public DbSet<SourceDomainEventEntity> SourceDomainEvents => Set<SourceDomainEventEntity>();

    /// <summary>Последовательности событий аккаунтов (accountSeq)</summary>
    public DbSet<SourceAccountStreamEntity> SourceAccountStreams => Set<SourceAccountStreamEntity>();

    /// <summary>Входящие коннекторы: водяной знак</summary>
    public DbSet<SourceConnectorEntity> SourceConnectors => Set<SourceConnectorEntity>();

    /// <summary>Текущее состояние аккаунтов</summary>
    public DbSet<CurAccount> CurAccounts => Set<CurAccount>();

    /// <summary>Текущее состояние услуг</summary>
    public DbSet<CurService> CurServices => Set<CurService>();

    /// <summary>Текущее состояние транзакций</summary>
    public DbSet<CurTransaction> CurTransactions => Set<CurTransaction>();

    /// <summary>Исходы сроков услуг</summary>
    public DbSet<ExpiryOutcomeEntity> ExpiryOutcomes => Set<ExpiryOutcomeEntity>();

    /// <summary>Пользователи</summary>
    public DbSet<UserEntity> Users => Set<UserEntity>();

    /// <summary>Серверные сессии</summary>
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();

    /// <summary>Журнал аудита</summary>
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    /// <summary>Неудачные входы (общая для реплик блокировка перебора)</summary>
    public DbSet<LoginThrottleEntity> LoginThrottle => Set<LoginThrottleEntity>();

    /// <inheritdoc />
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    /// <summary>Дневная активность клиентов</summary>
    public DbSet<CustomerActivityEntity> CustomerActivity => Set<CustomerActivityEntity>();

    /// <summary>Пакетные импорты истории</summary>
    public DbSet<SourceImportEntity> SourceImports => Set<SourceImportEntity>();

    /// <summary>Куски импортов</summary>
    public DbSet<SourceImportChunkEntity> SourceImportChunks => Set<SourceImportChunkEntity>();

    /// <summary>Загруженные ML-модели</summary>
    public DbSet<MlModelEntity> MlModels => Set<MlModelEntity>();

    /// <summary>Прогнозы теневой модели в точках решения</summary>
    public DbSet<MlShadowPredictionEntity> MlShadowPredictions => Set<MlShadowPredictionEntity>();

    /// <summary>Настройки и состояние симуляции</summary>
    public DbSet<SimulationStateEntity> SimulationState => Set<SimulationStateEntity>();

    /// <summary>События, собранные в панели</summary>
    public DbSet<SimulationCommandEntity> SimulationCommands => Set<SimulationCommandEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        // snake_case колонок — удобно читать БД при отладке
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnake(property.Name));
    }

    private static string ToSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])))) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
