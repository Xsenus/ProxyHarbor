using Microsoft.EntityFrameworkCore;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Применяет миграции и синхронизирует встроенный каталог, не удаляя пользовательские источники.</summary>
public static class DatabaseSeeder
{
    /// <summary>Добавляет недостающие feed'ы и обновляет их метаданные, сохраняя выбор Enabled/Disabled.</summary>
    public static async Task InitializeAsync(ProxyHarborDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.MigrateAsync(cancellationToken);
        var legacyUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "https://cdn.jsdelivr.net/gh/proxifly/free-proxy-list@main/proxies/protocols/http/data.txt",
            "https://cdn.jsdelivr.net/gh/proxifly/free-proxy-list@main/proxies/protocols/socks4/data.txt",
            "https://cdn.jsdelivr.net/gh/proxifly/free-proxy-list@main/proxies/protocols/socks5/data.txt",
            "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt",
            "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks4.txt",
            "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks5.txt",
            "https://raw.githubusercontent.com/iplocate/free-proxy-list/main/protocols/http.txt",
            "https://raw.githubusercontent.com/iplocate/free-proxy-list/main/protocols/socks5.txt"
        };
        var existing = await db.Sources.ToDictionaryAsync(x => x.Url, StringComparer.OrdinalIgnoreCase, cancellationToken);

        // Удаляем только URL из первоначальной встроенной версии, заменённые каноническими feed'ами.
        db.Sources.RemoveRange(existing.Values.Where(x => legacyUrls.Contains(x.Url)));

        foreach (var definition in BuiltInSourceCatalog.Sources)
        {
            if (existing.TryGetValue(definition.Url, out var source))
            {
                source.Name = definition.Name;
                source.DefaultProtocol = definition.Protocol;
                source.Priority = definition.Rank * 10;
                continue;
            }

            db.Sources.Add(new ProxySource
            {
                Name = definition.Name,
                Url = definition.Url,
                DefaultProtocol = definition.Protocol,
                Priority = definition.Rank * 10
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
