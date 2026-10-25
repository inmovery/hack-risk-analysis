using Aeza.Risk.Core.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aeza.Risk.Infrastructure.Data.Configuration;

/// <summary>Конфигурация <see cref="MlModelEntity"/></summary>
public sealed class MlModelConfiguration : IEntityTypeConfiguration<MlModelEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MlModelEntity> builder)
    {
        builder.ToTable("ml_models");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.Property(x => x.Version).HasMaxLength(100);
        builder.Property(x => x.FeatureSchema).HasMaxLength(60);
        builder.Property(x => x.Kind).HasMaxLength(40);
        builder.Property(x => x.CardJson).HasColumnType("jsonb");
        builder.Property(x => x.OnnxSha256).HasMaxLength(64);
        builder.Property(x => x.UploadedBy).HasMaxLength(100);
    }
}

/// <summary>Конфигурация <see cref="MlShadowPredictionEntity"/>: один прогноз на модель, срок услуги и точку решения</summary>
public sealed class MlShadowPredictionConfiguration : IEntityTypeConfiguration<MlShadowPredictionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MlShadowPredictionEntity> builder)
    {
        builder.ToTable("ml_shadow_predictions");
        builder.HasKey(x => new { x.ModelId, x.ServiceId, x.ExpiresAt, x.OffsetDays });
        builder.HasOne<MlModelEntity>().WithMany().HasForeignKey(x => x.ModelId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.ServiceId, x.ExpiresAt });
    }
}
