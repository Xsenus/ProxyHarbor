using System.Buffers.Binary;
using System.Net;
using System.Text.RegularExpressions;
using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>Безопасно извлекает IP и порт из распространённых текстовых форматов.</summary>
public static partial class ProxyParser
{
    [GeneratedRegex(
        @"(?<host>(?:\[[0-9a-f:]+\])|(?:\d{1,3}\.){3}\d{1,3}):(?<port>\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EndpointRegex();

    /// <summary>Разбирает содержимое источника и возвращает только синтаксически допустимые уникальные адреса.</summary>
    public static IReadOnlyCollection<(string Host, int Port, ProxyProtocol Protocol)> Parse(
        string content,
        ProxyProtocol defaultProtocol) => ParseWithLimitStatus(content, defaultProtocol, int.MaxValue).Items;

    /// <summary>
    /// Разбирает не более <paramref name="maxResults"/> уникальных адресов, не создавая полный
    /// промежуточный список для потенциально многомиллионного недоверенного feed'а.
    /// </summary>
    public static IReadOnlyCollection<(string Host, int Port, ProxyProtocol Protocol)> Parse(
        string content,
        ProxyProtocol defaultProtocol,
        int maxResults) => ParseWithLimitStatus(content, defaultProtocol, maxResults).Items;

    /// <summary>
    /// Возвращает не только bounded-набор, но и точный признак наличия следующего уникального
    /// адреса. После обнаружения первого адреса сверх лимита разбор немедленно прекращается.
    /// </summary>
    internal static ProxyParseResult ParseWithLimitStatus(
        string content,
        ProxyProtocol defaultProtocol,
        int maxResults)
    {
        var result = new List<(string Host, int Port, ProxyProtocol Protocol)>(Math.Min(maxResults, 4_096));
        var summary = ParseTo(content, defaultProtocol, maxResults, candidate => result.Add(candidate.ToEndpoint()));
        return new ProxyParseResult(result, summary.Truncated);
    }

    /// <summary>
    /// Передаёт уникальные кандидаты прямо потребителю, сохраняя только компактный
    /// value-key для дедупликации текущего feed'а и не создавая список строк.
    /// </summary>
    internal static ProxyParseSummary ParseTo(
        string content,
        ProxyProtocol defaultProtocol,
        int maxResults,
        Action<ProxyCandidateKey> accept)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        var unique = new HashSet<ProxyCandidateKey>(Math.Min(maxResults, 4_096));

        foreach (Match match in EndpointRegex().Matches(content))
        {
            // Regex ищет кандидата внутри свободного текста, поэтому границы проверяются
            // отдельно без unsupported lookaround в NonBacktracking engine. Иначе хвост
            // пятиоктетного IPv4 или первые пять цифр шестизначного порта становились
            // самостоятельным ложным endpoint.
            if (!HasTokenBoundaries(content, match.Index, match.Length)) continue;
            var host = match.Groups["host"].ValueSpan;
            if (host.Length >= 2 && host[0] == '[' && host[^1] == ']') host = host[1..^1];
            var portText = match.Groups["port"].ValueSpan;
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
                continue;

            // IPAddress.TryParse исторически понимает ведущий ноль IPv4 как octal:
            // 010.0.0.1 превращается в 8.0.0.1. Feed обязан содержать однозначные
            // canonical decimal octets, чтобы parser не менял фактический destination.
            if (host.IndexOf('.') >= 0 && !HasCanonicalIpv4Octets(host)) continue;

            // Regex даже не выделяет доменные endpoints: их исключение блокирует DNS rebinding.
            if (!IPAddress.TryParse(host, out var ip) || !NetworkSafety.IsPublicAddress(ip)) continue;

            // Scheme находится непосредственно перед IP, но намеренно не включён в regex:
            // так URL/временные метки в заголовке feed'а не влияют на поиск следующего endpoint.
            var protocol = ParseProtocolBefore(content.AsSpan(0, match.Index), defaultProtocol);
            var candidate = ProxyCandidateKey.Create(ip, port, protocol);
            if (unique.Count < maxResults)
            {
                if (!unique.Add(candidate)) continue;
                accept(candidate);
                continue;
            }

            // После заполнения коллекции lookup нужен только для различения безопасного
            // duplicate-tail и первого действительно потерянного уникального адреса.
            if (!unique.Contains(candidate)) return new ProxyParseSummary(unique.Count, Truncated: true);
        }

        return new ProxyParseSummary(unique.Count, Truncated: false);
    }

    private static bool HasTokenBoundaries(string content, int index, int length) =>
        (index == 0 || !IsEmbeddedTokenCharacter(content[index - 1])) &&
        (index + length == content.Length || !IsEmbeddedTokenCharacter(content[index + length]));

    /// <summary>Отделители URL/JSON/CSV допустимы, части hostname/числа/идентификатора — нет.</summary>
    private static bool IsEmbeddedTokenCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '.' or ':' or '_' or '-' or '@';

    /// <summary>Проверяет отсутствие octal-leading-zero без строковых аллокаций.</summary>
    private static bool HasCanonicalIpv4Octets(ReadOnlySpan<char> host)
    {
        var segmentStart = 0;
        while (segmentStart < host.Length)
        {
            var relativeDot = host[segmentStart..].IndexOf('.');
            var segmentEnd = relativeDot < 0 ? host.Length : segmentStart + relativeDot;
            if (segmentEnd - segmentStart > 1 && host[segmentStart] == '0') return false;
            if (relativeDot < 0) return true;
            segmentStart = segmentEnd + 1;
        }
        return false;
    }

    private static ProxyProtocol ParseProtocolBefore(ReadOnlySpan<char> prefix, ProxyProtocol fallback) =>
        prefix.EndsWith("http://", StringComparison.OrdinalIgnoreCase) ? ProxyProtocol.Http :
        prefix.EndsWith("https://", StringComparison.OrdinalIgnoreCase) ? ProxyProtocol.Https :
        prefix.EndsWith("socks4://", StringComparison.OrdinalIgnoreCase) ? ProxyProtocol.Socks4 :
        prefix.EndsWith("socks5://", StringComparison.OrdinalIgnoreCase) ? ProxyProtocol.Socks5 :
        fallback;
}

/// <summary>
/// Компактный канонический ключ IP/port/protocol без ссылок на строки или массивы.
/// Строка создаётся только один раз при потоковой записи итогового набора в PostgreSQL.
/// </summary>
internal readonly record struct ProxyCandidateKey(
    ulong AddressHigh,
    ulong AddressLow,
    ushort PortValue,
    byte ProtocolValue,
    bool IsIpv6)
{
    internal int Port => PortValue;
    internal ProxyProtocol Protocol => (ProxyProtocol)ProtocolValue;

    internal static ProxyCandidateKey Create(IPAddress address, int port, ProxyProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65_535);
        if (!Enum.IsDefined(protocol)) throw new ArgumentOutOfRangeException(nameof(protocol));
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written))
            throw new InvalidOperationException("Не удалось получить бинарное представление IP.");
        return written switch
        {
            4 => new ProxyCandidateKey(0, BinaryPrimitives.ReadUInt32BigEndian(bytes), (ushort)port, (byte)protocol, false),
            16 => new ProxyCandidateKey(
                BinaryPrimitives.ReadUInt64BigEndian(bytes),
                BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
                (ushort)port,
                (byte)protocol,
                true),
            _ => throw new InvalidOperationException("IP имеет неизвестное бинарное представление.")
        };
    }

    internal static ProxyCandidateKey Parse(
        string host,
        int port,
        ProxyProtocol protocol) =>
        IPAddress.TryParse(host, out var address)
            ? Create(address, port, protocol)
            : throw new ArgumentException("Host должен быть IP-адресом.", nameof(host));

    internal (string Host, int Port, ProxyProtocol Protocol) ToEndpoint()
    {
        Span<byte> bytes = stackalloc byte[16];
        IPAddress address;
        if (IsIpv6)
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes, AddressHigh);
            BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], AddressLow);
            address = new IPAddress(bytes);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)AddressLow));
            address = new IPAddress(bytes[..4]);
        }
        return (address.ToString(), Port, Protocol);
    }
}

/// <summary>Bounded-результат parser с явным сигналом, что вход содержал ещё уникальные адреса.</summary>
internal sealed record ProxyParseResult(
    IReadOnlyCollection<(string Host, int Port, ProxyProtocol Protocol)> Items,
    bool Truncated);

/// <summary>Итог потокового разбора без materialized списка endpoint'ов.</summary>
internal readonly record struct ProxyParseSummary(int Count, bool Truncated);
