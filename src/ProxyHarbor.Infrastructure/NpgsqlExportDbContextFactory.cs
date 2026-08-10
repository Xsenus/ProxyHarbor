using Microsoft.EntityFrameworkCore;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Создаёт контексты исключительно для потокового публичного экспорта.
/// Отдельный контракт не позволяет случайно подменить их обычным retry-enabled пулом.
/// </summary>
public interface IProxyExportDbContextFactory
{
    /// <summary>Создаёт независимый контекст, жизненным циклом которого владеет вызывающий код.</summary>
    Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Фабрика Npgsql-контекстов без автоматических повторов команд.
/// Потоковый HTTP-ответ нельзя безопасно повторить после отправки первых байтов, поэтому
/// экспорт использует RepeatableRead snapshot, но намеренно не использует retry strategy.
/// </summary>
public sealed class NpgsqlExportDbContextFactory : IProxyExportDbContextFactory
{
    private readonly DbContextOptions<ProxyHarborDbContext> _options;

    /// <summary>Подготавливает неизменяемые thread-safe параметры подключения.</summary>
    public NpgsqlExportDbContextFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _options = new DbContextOptionsBuilder<ProxyHarborDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    /// <inheritdoc />
    public Task<ProxyHarborDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProxyHarborDbContext(_options));
    }
}
