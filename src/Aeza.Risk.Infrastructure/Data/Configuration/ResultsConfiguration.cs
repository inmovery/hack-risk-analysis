using Aeza.Risk.Core.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aeza.Risk.Infrastructure.Data.Configuration;

/// <summary>Конфигурация <see cref="ModelRevisionEntity"/></summary>
public sealed class ModelRevisionConfiguration : IEntityTypeConfiguration<ModelRevisionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ModelRevisionEntity> builder)
    {
        builder.ToTable("model_revisions");
        // Id = номер ревизии (назначается приложением, монотонно)
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SettingsJson).HasColumnType("jsonb");
    }
}

/// <summary>Конфигурация <see cref="RecalcJob"/></summary>
public sealed class RecalcJobConfiguration : IEntityTypeConfiguration<RecalcJob>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RecalcJob> builder)
    {
        builder.ToTable("recalc_jobs");
        builder.HasIndex(x => new { x.Status, x.AvailableAt });
    }
}

/// <summary>Конфигурация <see cref="GenerationEntity"/></summary>
public sealed class GenerationConfiguration : IEntityTypeConfiguration<GenerationEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<GenerationEntity> builder)
    {
        builder.ToTable("generations");
        builder.Property(x => x.SummaryJson).HasColumnType("jsonb");
        builder.Property(x => x.ForecastJson).HasColumnType("jsonb");
    }
}

/// <summary>Конфигурация <see cref="CustomerResultEntity"/>: индексы под сортировки (8 обязательных направлений + индикаторы оттока) и фильтры</summary>
public sealed class CustomerResultConfiguration : IEntityTypeConfiguration<CustomerResultEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CustomerResultEntity> builder)
    {
        builder.ToTable("customer_results");
        builder.HasKey(x => new { x.GenerationId, x.CustomerId });
        builder.HasOne<GenerationEntity>().WithMany().HasForeignKey(x => x.GenerationId).OnDelete(DeleteBehavior.Cascade);
        // Байтовый порядок email (collation C): сортировка не зависит от локали сервера БД
        builder.Property(x => x.EmailSort).UseCollation("C");
        builder.Property(x => x.Email).UseCollation("C");
        builder.Property(x => x.Score).HasPrecision(12, 6);
        builder.Property(x => x.PrimaryReasonContribution).HasPrecision(12, 6);
        builder.Property(x => x.DetailJson).HasColumnType("jsonb");
        builder.HasIndex(x => new { x.GenerationId, x.EmailSort, x.Email, x.CustomerId });
        builder.HasIndex(x => new { x.GenerationId, x.Balance, x.CustomerId });
        builder.HasIndex(x => new { x.GenerationId, x.TotalReplenished, x.CustomerId });
        builder.HasIndex(x => new { x.GenerationId, x.Score, x.CustomerId });
        builder.HasIndex(x => new { x.GenerationId, x.AtRiskMinor, x.CustomerId });
        builder.HasIndex(x => new { x.GenerationId, x.Level });
        builder.Property(x => x.FullChurnScore).HasPrecision(12, 6);
        builder.Property(x => x.PaymentChangePct).HasPrecision(9, 1);
        builder.HasIndex(x => new { x.GenerationId, x.FullChurnScore, x.CustomerId });
        builder.HasIndex(x => new { x.GenerationId, x.PaymentChangePct, x.CustomerId });
    }
}

/// <summary>Конфигурация <see cref="AppStateEntity"/></summary>
public sealed class AppStateConfiguration : IEntityTypeConfiguration<AppStateEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AppStateEntity> builder)
    {
        builder.ToTable("app_state");
        builder.Property(x => x.Id).ValueGeneratedNever();
    }
}
