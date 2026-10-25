using Aeza.Risk.Core.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aeza.Risk.Infrastructure.Data.Configuration;

/// <summary>Конфигурация <see cref="SourceEventEntity"/>: журнал событий источника</summary>
public sealed class SourceEventConfiguration : IEntityTypeConfiguration<SourceEventEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceEventEntity> builder)
    {
        builder.ToTable("source_events");
        builder.HasKey(x => x.Seq);
        builder.Property(x => x.Seq).UseIdentityAlwaysColumn();
        builder.HasIndex(x => x.EventId).IsUnique().HasFilter("event_id IS NOT NULL");
        builder.HasIndex(x => x.ReceivedAt);
        builder.HasIndex(x => x.OccurredAt);
        builder.HasIndex(x => x.DomainSeq);
        builder.HasIndex(x => new { x.OwnerId, x.Seq });
        builder.HasIndex(x => new { x.Kind, x.EntityId, x.Seq });
        builder.Property(x => x.Effect).HasColumnType("jsonb");
        builder.Property(x => x.Payload).HasColumnType("jsonb");
    }
}

/// <summary>Конфигурация <see cref="SourceDomainEventEntity"/>: журнал доменных событий v2</summary>
public sealed class SourceDomainEventConfiguration : IEntityTypeConfiguration<SourceDomainEventEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceDomainEventEntity> builder)
    {
        builder.ToTable("source_domain_events");
        builder.HasKey(x => x.Seq);
        builder.Property(x => x.Seq).UseIdentityAlwaysColumn();
        builder.HasIndex(x => x.EventId).IsUnique();
        builder.HasIndex(x => new { x.AccountId, x.OccurredAt });
        builder.HasIndex(x => new { x.Type, x.OccurredAt });
        builder.HasIndex(x => x.OccurredAt);
        builder.HasIndex(x => x.ReceivedAt);
        builder.Property(x => x.EventId).HasMaxLength(128);
        builder.Property(x => x.Type).HasMaxLength(64);
        builder.Property(x => x.Connector).HasMaxLength(40);
        builder.Property(x => x.Data).HasColumnType("jsonb");
    }
}

/// <summary>Конфигурация <see cref="SourceAccountStreamEntity"/></summary>
public sealed class SourceAccountStreamConfiguration : IEntityTypeConfiguration<SourceAccountStreamEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceAccountStreamEntity> builder)
    {
        builder.ToTable("source_account_streams");
        builder.HasKey(x => x.AccountId);
        builder.Property(x => x.AccountId).ValueGeneratedNever();
        builder.HasIndex(x => x.GapSince).HasFilter("gap_since IS NOT NULL");
    }
}

/// <summary>Конфигурация <see cref="SourceConnectorEntity"/></summary>
public sealed class SourceConnectorConfiguration : IEntityTypeConfiguration<SourceConnectorEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceConnectorEntity> builder)
    {
        builder.ToTable("source_connectors");
        builder.HasKey(x => x.Name);
        builder.Property(x => x.Name).HasMaxLength(40);
        builder.Property(x => x.LastError).HasMaxLength(200);
    }
}

/// <summary>Конфигурация <see cref="CustomerActivityEntity"/></summary>
public sealed class CustomerActivityConfiguration : IEntityTypeConfiguration<CustomerActivityEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CustomerActivityEntity> builder)
    {
        builder.ToTable("customer_activity_daily");
        builder.HasKey(x => new { x.AccountId, x.Day });
        builder.HasIndex(x => x.Day);
    }
}

/// <summary>Конфигурация <see cref="SourceImportEntity"/></summary>
public sealed class SourceImportConfiguration : IEntityTypeConfiguration<SourceImportEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceImportEntity> builder)
    {
        builder.ToTable("source_imports");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.Property(x => x.Actor).HasMaxLength(100);
        builder.Property(x => x.Connector).HasMaxLength(40);
        builder.Property(x => x.Status).HasMaxLength(20);
        builder.Property(x => x.ErrorCode).HasMaxLength(60);
        builder.Property(x => x.Errors).HasColumnType("jsonb");
        builder.HasIndex(x => x.Status);
    }
}

/// <summary>Конфигурация <see cref="SourceImportChunkEntity"/></summary>
public sealed class SourceImportChunkConfiguration : IEntityTypeConfiguration<SourceImportChunkEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SourceImportChunkEntity> builder)
    {
        builder.ToTable("source_import_chunks");
        builder.HasKey(x => new { x.ImportId, x.No });
        builder.HasOne<SourceImportEntity>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Конфигурация <see cref="CurAccount"/></summary>
public sealed class CurAccountConfiguration : IEntityTypeConfiguration<CurAccount>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CurAccount> builder)
    {
        builder.ToTable("cur_accounts");
        builder.Property(x => x.Id).ValueGeneratedNever();
    }
}

/// <summary>Конфигурация <see cref="CurService"/></summary>
public sealed class CurServiceConfiguration : IEntityTypeConfiguration<CurService>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CurService> builder)
    {
        builder.ToTable("cur_services");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasIndex(x => x.OwnerId);
    }
}

/// <summary>Конфигурация <see cref="CurTransaction"/></summary>
public sealed class CurTransactionConfiguration : IEntityTypeConfiguration<CurTransaction>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CurTransaction> builder)
    {
        builder.ToTable("cur_transactions");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasIndex(x => x.ServiceId);
    }
}

/// <summary>Конфигурация <see cref="ExpiryOutcomeEntity"/></summary>
public sealed class ExpiryOutcomeConfiguration : IEntityTypeConfiguration<ExpiryOutcomeEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ExpiryOutcomeEntity> builder)
    {
        builder.ToTable("expiry_outcomes");
        builder.HasKey(x => new { x.ServiceId, x.ExpiresAt });
        builder.HasIndex(x => new { x.Outcome, x.ExpiresAt });
        builder.HasIndex(x => x.ResolvedAt);
    }
}
