using Aeza.Risk.Core.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aeza.Risk.Infrastructure.Data.Configuration;

/// <summary>Конфигурация <see cref="SourceSnapshotEntity"/></summary>
public sealed class SourceSnapshotConfiguration : IEntityTypeConfiguration<SourceSnapshotEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceSnapshotEntity> builder)
    {
        builder.ToTable("source_snapshots");
        builder.HasIndex(x => x.Revision).IsUnique();
        builder.HasIndex(x => x.ContentHash).IsUnique();
        builder.Property(x => x.Provenance).HasColumnType("jsonb");
        builder.Property(x => x.Warnings).HasColumnType("jsonb");
    }
}

/// <summary>Конфигурация <see cref="SrcAccount"/></summary>
public sealed class SrcAccountConfiguration : IEntityTypeConfiguration<SrcAccount>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SrcAccount> builder)
    {
        builder.ToTable("src_accounts");
        builder.HasKey(x => new { x.SnapshotId, x.Id });
        builder.HasOne<SourceSnapshotEntity>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Конфигурация <see cref="SrcService"/></summary>
public sealed class SrcServiceConfiguration : IEntityTypeConfiguration<SrcService>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SrcService> builder)
    {
        builder.ToTable("src_services");
        builder.HasKey(x => new { x.SnapshotId, x.Id });
        builder.HasIndex(x => new { x.SnapshotId, x.OwnerId });
        builder.HasOne<SourceSnapshotEntity>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Конфигурация <see cref="SrcTransaction"/></summary>
public sealed class SrcTransactionConfiguration : IEntityTypeConfiguration<SrcTransaction>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SrcTransaction> builder)
    {
        builder.ToTable("src_transactions");
        builder.HasKey(x => new { x.SnapshotId, x.Id });
        builder.HasIndex(x => new { x.SnapshotId, x.ServiceId });
        builder.HasOne<SourceSnapshotEntity>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Конфигурация <see cref="ObservedServiceEvent"/></summary>
public sealed class ObservedServiceEventConfiguration : IEntityTypeConfiguration<ObservedServiceEvent>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ObservedServiceEvent> builder)
    {
        builder.ToTable("observed_service_events");
        builder.HasIndex(x => x.ObservedTo);
    }
}
