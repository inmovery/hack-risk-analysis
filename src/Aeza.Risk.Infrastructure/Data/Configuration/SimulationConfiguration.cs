using Aeza.Risk.Core.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aeza.Risk.Infrastructure.Data.Configuration;

/// <summary>Конфигурация <see cref="SimulationStateEntity"/></summary>
public sealed class SimulationStateConfiguration : IEntityTypeConfiguration<SimulationStateEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SimulationStateEntity> builder)
    {
        builder.ToTable("simulation_state");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.SettingsJson).HasColumnType("jsonb");
        builder.Property(x => x.WorldSettingsJson).HasColumnType("jsonb");
        builder.Property(x => x.AgentStatusJson).HasColumnType("jsonb");
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);
        builder.Property(x => x.WorldResetBy).HasMaxLength(100);
    }
}

/// <summary>Конфигурация <see cref="SimulationCommandEntity"/></summary>
public sealed class SimulationCommandConfiguration : IEntityTypeConfiguration<SimulationCommandEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SimulationCommandEntity> builder)
    {
        builder.ToTable("simulation_commands");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
        builder.Property(x => x.Type).HasMaxLength(60);
        builder.Property(x => x.DataJson).HasColumnType("jsonb");
        builder.Property(x => x.Status).HasMaxLength(16);
        builder.Property(x => x.EventIdsJson).HasColumnType("jsonb");
        builder.Property(x => x.Message).HasMaxLength(500);
        builder.HasIndex(x => new { x.WorldRevision, x.Status });
    }
}
