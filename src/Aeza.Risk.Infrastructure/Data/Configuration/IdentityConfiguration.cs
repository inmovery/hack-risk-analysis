using Aeza.Risk.Core.Entities;

using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aeza.Risk.Infrastructure.Data.Configuration;

/// <summary>Конфигурация <see cref="UserEntity"/></summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<UserEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserEntity> builder)
    {
        builder.ToTable("users");
        builder.HasIndex(x => x.LoginNormalized).IsUnique();
    }
}

/// <summary>Конфигурация <see cref="SessionEntity"/></summary>
public sealed class SessionConfiguration : IEntityTypeConfiguration<SessionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SessionEntity> builder)
    {
        builder.ToTable("sessions");
        builder.HasKey(x => x.IdHash);
        builder.HasIndex(x => x.UserId);
    }
}

/// <summary>Конфигурация <see cref="LoginThrottleEntity"/></summary>
public sealed class LoginThrottleConfiguration : IEntityTypeConfiguration<LoginThrottleEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LoginThrottleEntity> builder)
    {
        builder.ToTable("login_throttle");
        builder.HasKey(x => x.KeyHash);
        builder.Property(x => x.KeyHash).HasMaxLength(64);
        builder.HasIndex(x => x.UpdatedAt);
    }
}

/// <summary>Конфигурация <see cref="AuditEntry"/></summary>
public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_log");
        builder.Property(x => x.Details).HasColumnType("jsonb");
    }
}

/// <summary>Конфигурация <see cref="DataProtectionKey"/></summary>
public sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DataProtectionKey> builder) => builder.ToTable("data_protection_keys");
}
