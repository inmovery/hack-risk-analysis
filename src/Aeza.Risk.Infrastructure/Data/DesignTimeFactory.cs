using JetBrains.Annotations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Aeza.Risk.Infrastructure.Data;

/// <summary>Фабрика для dotnet-ef: миграции генерируются без запуска хоста (см. Data/Tools/add-migrations.md)</summary>
[UsedImplicitly]
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <inheritdoc />
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=aeza_design;Username=design;Password=design").Options);
}
