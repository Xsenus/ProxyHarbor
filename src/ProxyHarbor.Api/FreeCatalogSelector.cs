using System.Security.Cryptography;
using System.Text;

namespace ProxyHarbor.Api;

/// <summary>
/// Формирует бесплатную витрину из небольшой уже отфильтрованной сервером выборки.
/// Набор стабилен в пределах десятиминутного окна, содержит до двух быстрых адресов,
/// затем адреса среднего диапазона и по возможности не повторяет страны.
/// </summary>
internal static class FreeCatalogSelector
{
    internal const int CandidatePoolSize = 500;

    internal static IReadOnlyList<T> Select<T>(
        IReadOnlyList<T> orderedByQuality,
        Func<T, string> key,
        Func<T, string?> country,
        int limit,
        DateTimeOffset now)
    {
        if (limit <= 0 || orderedByQuality.Count == 0) return [];

        var target = Math.Min(limit, orderedByQuality.Count);
        var fastTarget = Math.Min(2, target);
        var fastBandSize = Math.Max(fastTarget, (int)Math.Ceiling(orderedByQuality.Count * .20m));
        var mediumEnd = Math.Max(fastBandSize, (int)Math.Ceiling(orderedByQuality.Count * .85m));
        var bucket = now.ToUnixTimeSeconds() / FreeExportAccessService.CooldownSeconds;

        var fast = Shuffle(orderedByQuality.Take(fastBandSize), key, bucket, "fast");
        var medium = Shuffle(orderedByQuality.Skip(fastBandSize).Take(mediumEnd - fastBandSize), key, bucket, "medium");
        var fallback = Shuffle(orderedByQuality, key, bucket, "fallback");
        var selected = new List<T>(target);
        var selectedKeys = new HashSet<string>(StringComparer.Ordinal);
        var selectedCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddDiversified(fast, fastTarget, selected, selectedKeys, selectedCountries, key, country);
        AddDiversified(medium, target, selected, selectedKeys, selectedCountries, key, country);
        AddDiversified(fallback, target, selected, selectedKeys, selectedCountries, key, country);
        return selected;
    }

    private static void AddDiversified<T>(
        IReadOnlyList<T> candidates,
        int target,
        List<T> selected,
        HashSet<string> selectedKeys,
        HashSet<string> selectedCountries,
        Func<T, string> key,
        Func<T, string?> country)
    {
        foreach (var preferNewCountry in new[] { true, false })
        {
            foreach (var candidate in candidates)
            {
                if (selected.Count >= target) return;
                var candidateKey = key(candidate);
                if (selectedKeys.Contains(candidateKey)) continue;
                var candidateCountry = country(candidate)?.Trim();
                var countryAlreadyUsed = !string.IsNullOrEmpty(candidateCountry) && selectedCountries.Contains(candidateCountry);
                if (preferNewCountry && (string.IsNullOrEmpty(candidateCountry) || countryAlreadyUsed)) continue;

                selected.Add(candidate);
                selectedKeys.Add(candidateKey);
                if (!string.IsNullOrEmpty(candidateCountry)) selectedCountries.Add(candidateCountry);
            }
        }
    }

    private static T[] Shuffle<T>(
        IEnumerable<T> source,
        Func<T, string> key,
        long bucket,
        string band) => source
        .OrderBy(item => StableRank($"{bucket}:{band}:{key(item)}"))
        .ToArray();

    private static ulong StableRank(string value) =>
        BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0);
}
