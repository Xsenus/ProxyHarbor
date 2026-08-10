using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ProxyHarbor.Infrastructure;

/// <summary>Позволяет создавать миграции без запущенного API и доступной production-БД.</summary>
public sealed class ProxyHarborDesignTimeFactory : IDesignTimeDbContextFactory<ProxyHarborDbContext>
{
    /// <inheritdoc />
    public ProxyHarborDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=proxyharbor;Username=proxyharbor;Password=proxyharbor_dev";
        var options = new DbContextOptionsBuilder<ProxyHarborDbContext>().UseNpgsql(connection).Options;
        return new ProxyHarborDbContext(options);
    }
}
