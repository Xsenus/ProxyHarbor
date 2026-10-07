using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProxyHarbor.Domain;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ProxyHarbor.Infrastructure;

/// <summary>Preserves published Clash connection settings as YAML instead of inventing equivalent URI parameters.</summary>
internal static partial class ClashYamlFeedParser
{
    internal const int MaximumConfigurationCharacters = 16_384;
    internal const int MaximumConfigurationBytes = 65_536;

    internal static bool LooksLike(string content)
    {
        var text = content.AsSpan().TrimStart().TrimStart('\uFEFF').TrimStart();
        if (!text.StartsWith("{")) return RootProxies().IsMatch(content);
        if (!FlowProxies().IsMatch(content)) return false;
        if (text.Length > 8 * 1024 * 1024) return true; // Let the guarded reader reject oversized YAML.
        try
        {
            using var document = JsonDocument.Parse(text.ToString(), new JsonDocumentOptions { MaxDepth = 64 });
            if (!document.RootElement.TryGetProperty("proxies", out var proxies)) return false;
            // Telegram API also uses a proxies array for links and server/secret
            // records. Clash JSON identifies its profiles with a type field.
            return proxies.ValueKind == JsonValueKind.Array &&
                (proxies.GetArrayLength() == 0 || proxies.EnumerateArray().Any(proxy =>
                    proxy.ValueKind == JsonValueKind.Object && proxy.TryGetProperty("type", out _)));
        }
        catch (JsonException) { return true; } // Flow YAML remains subject to the full YAML guards.
    }

    internal static bool IsValidStandalone(VpnCandidate candidate) => TryReadStandalone(candidate, out _);

    internal static bool TryReadStandalone(VpnCandidate candidate, out YamlMappingNode root)
    {
        root = null!;
        if (candidate.ClashConfiguration is not { Length: > 0 } configuration ||
            configuration.Length > MaximumConfigurationCharacters || Encoding.UTF8.GetByteCount(configuration) > MaximumConfigurationBytes)
            return false;
        try
        {
            var document = ClashYamlFeedReader.ReadRequired(configuration);
            var proxies = (YamlSequenceNode)document.Children[new YamlScalarNode("proxies")];
            if (!TryGetEndpoint((YamlMappingNode)proxies.Children[0], out var primary) ||
                primary.Host != candidate.Host || primary.Port != candidate.Port ||
                primary.Protocol != candidate.Protocol || primary.Transport != candidate.Transport) return false;
            var proxySet = new HashSet<YamlNode>(proxies.Children, ReferenceEqualityComparer.Instance);
            var namedNodes = new Lazy<Dictionary<string, List<YamlMappingNode>>>(() => IndexNamedNodes(document, proxies));
            // Require precisely the public primary and its complete static dependencies.
            // Cached data must not smuggle unrelated private nodes or global client options.
            if (!BuildStandalone(proxySet, namedNodes, (YamlMappingNode)proxies.Children[0]).Equals(document)) return false;
            root = document;
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    internal static void ParseAllTo(string content, Func<VpnCandidate, bool> accept)
    {
        var root = ClashYamlFeedReader.ReadRequired(content);
        var proxies = (YamlSequenceNode)root.Children[new YamlScalarNode("proxies")];
        var proxySet = new HashSet<YamlNode>(proxies.Children, ReferenceEqualityComparer.Instance);
        var namedNodes = new Lazy<Dictionary<string, List<YamlMappingNode>>>(() => IndexNamedNodes(root, proxies));
        foreach (YamlMappingNode proxy in proxies)
        {
            if (!TryGetEndpoint(proxy, out var candidate)) continue;
            var standalone = CloneStandalone(BuildStandalone(proxySet, namedNodes, proxy));
            using var writer = new BoundedWriter();
            new YamlStream(new YamlDocument(standalone)).Save(writer, assignAnchors: false);
            var configuration = writer.ToString();
            if (Encoding.UTF8.GetByteCount(configuration) > MaximumConfigurationBytes) throw InvalidConfiguration();
            candidate = candidate with { ClashConfiguration = configuration };
            if (!accept(candidate)) return;
        }
    }

    internal static IEnumerable<VpnCandidate> EnumerateExportProfiles(VpnCandidate candidate)
    {
        if (!TryReadStandalone(candidate, out var root)) throw InvalidConfiguration();
        var proxies = (YamlSequenceNode)root.Children[new YamlScalarNode("proxies")];
        var proxySet = new HashSet<YamlNode>(proxies.Children, ReferenceEqualityComparer.Instance);
        var namedNodes = new Lazy<Dictionary<string, List<YamlMappingNode>>>(() => IndexNamedNodes(root, proxies));
        foreach (YamlMappingNode proxy in proxies)
        {
            if (!TryGetEndpoint(proxy, out var profile)) throw InvalidConfiguration();
            var standalone = CloneStandalone(BuildStandalone(proxySet, namedNodes, proxy));
            using var writer = new BoundedWriter();
            new YamlStream(new YamlDocument(standalone)).Save(writer, assignAnchors: false);
            var configuration = writer.ToString();
            if (Encoding.UTF8.GetByteCount(configuration) > MaximumConfigurationBytes) throw InvalidConfiguration();
            yield return profile with { ClashConfiguration = configuration };
        }
    }

    internal static bool TryGetEndpoint(YamlMappingNode proxy, out VpnCandidate candidate)
    {
        candidate = default;
        var protocol = Scalar(proxy, "type")?.ToLowerInvariant() switch
        {
            "vless" => VpnProtocol.Vless,
            "vmess" => VpnProtocol.Vmess,
            "trojan" => VpnProtocol.Trojan,
            "ss" => VpnProtocol.Shadowsocks,
            "ssr" => VpnProtocol.ShadowsocksR,
            "hysteria" => VpnProtocol.Hysteria,
            "hysteria2" => VpnProtocol.Hysteria2,
            "tuic" => VpnProtocol.Tuic,
            "wireguard" => VpnProtocol.WireGuard,
            "anytls" => VpnProtocol.AnyTls,
            "http" => VpnProtocol.HttpProxy,
            "socks4" => VpnProtocol.Socks4Proxy,
            "socks5" => VpnProtocol.Socks5Proxy,
            _ => (VpnProtocol)(-1)
        };
        if (!Enum.IsDefined(protocol) || Scalar(proxy, "server") is not { } server ||
            !int.TryParse(Scalar(proxy, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65_535)
            return false;
        server = server.Trim().Trim('[', ']').ToLowerInvariant();
        if (IPAddress.TryParse(server, out var address)) server = address.ToString();
        var transport = protocol is VpnProtocol.WireGuard or VpnProtocol.Hysteria or VpnProtocol.Hysteria2 or VpnProtocol.Tuic ? "udp" : "tcp";
        candidate = new VpnCandidate(server, port, protocol, transport);
        return VpnFeedParser.IsSafe(candidate) && ClashConnectionFieldPolicy.IsSupported(proxy, protocol);
    }

    private static string? Scalar(YamlMappingNode node, string key) => (Property(node, key) as YamlScalarNode)?.Value;

    private static Dictionary<string, List<YamlMappingNode>> IndexNamedNodes(YamlMappingNode root, YamlSequenceNode proxies)
    {
        var index = new Dictionary<string, List<YamlMappingNode>>(StringComparer.Ordinal);
        var groups = Property(root, "proxy-groups") as YamlSequenceNode;
        foreach (var node in proxies.Concat(groups?.Children ?? Enumerable.Empty<YamlNode>()).OfType<YamlMappingNode>())
        {
            if (Scalar(node, "name") is not { Length: > 0 } name) continue;
            if (!index.TryGetValue(name, out var matches)) index[name] = matches = [];
            if (!matches.Any(match => ReferenceEquals(match, node))) matches.Add(node);
        }
        return index;
    }

    private static YamlMappingNode BuildStandalone(HashSet<YamlNode> proxies,
        Lazy<Dictionary<string, List<YamlMappingNode>>> namedNodes, YamlMappingNode primary)
    {
        var selected = new List<YamlNode> { primary };
        var selectedGroups = new List<YamlNode>();
        var complete = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        var active = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        void Resolve(string name)
        {
            if (name is "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS") return;
            if (!namedNodes.Value.TryGetValue(name, out var choices) || choices.Count != 1) throw InvalidDependencies();
            Visit(choices[0], true);
        }
        void Visit(YamlMappingNode node, bool add)
        {
            if (complete.Contains(node)) return;
            if (!active.Add(node)) throw InvalidDependencies();
            if (active.Count > 64 || complete.Count + active.Count > 1_000) throw InvalidDependencies();
            if (proxies.Contains(node))
            {
                if (!TryGetEndpoint(node, out _)) throw InvalidDependencies();
                if (add) selected.Add(node);
                if (Property(node, "dialer-proxy") is { } dependency)
                {
                    if (dependency is not YamlScalarNode { Value: { Length: > 0 } name }) throw InvalidDependencies();
                    Resolve(name);
                }
            }
            else
            {
                // Remote provider references are not a self-contained configuration.
                // Never silently remove them or ask the client to load an absent file.
                if (Property(node, "use") is { } providers && providers is not YamlSequenceNode { Children.Count: 0 } ||
                    HasDynamicFlag(node, "include-all") || HasDynamicFlag(node, "include-all-proxies") || HasDynamicFlag(node, "include-all-providers") ||
                    Property(node, "proxies") is not YamlSequenceNode members) throw InvalidDependencies();
                selectedGroups.Add(node);
                foreach (var member in members)
                {
                    if (member is not YamlScalarNode { Value: { Length: > 0 } name }) throw InvalidDependencies();
                    Resolve(name);
                }
            }
            active.Remove(node);
            complete.Add(node);
        }
        Visit(primary, false);
        var document = new YamlMappingNode { { "proxies", new YamlSequenceNode(selected) } };
        if (selectedGroups.Count > 0) document.Add("proxy-groups", new YamlSequenceNode(selectedGroups));
        return document;
    }

    private static InvalidDataException InvalidDependencies() => new("YAML-источник не содержит однозначной безопасной полной цепочки подключений.");

    private static bool HasDynamicFlag(YamlMappingNode node, string key) =>
        Property(node, key) is { } value &&
        (value is not YamlScalarNode scalar || !string.Equals(scalar.Value, "false", StringComparison.OrdinalIgnoreCase));

    internal static YamlNode CloneStandalone(YamlNode root)
    {
        var nodes = 0;
        YamlNode Clone(YamlNode node)
        {
            if (++nodes > MaximumConfigurationCharacters) throw InvalidConfiguration();
            if (node is YamlScalarNode scalar)
                return new YamlScalarNode(scalar.Value) { Tag = scalar.Tag, Style = scalar.Style };
            if (node is YamlSequenceNode sequence)
                return new YamlSequenceNode(sequence.Children.Select(Clone)) { Tag = sequence.Tag, Style = sequence.Style };
            if (node is YamlMappingNode mapping)
            {
                var copy = new YamlMappingNode { Tag = mapping.Tag, Style = mapping.Style };
                foreach (var pair in mapping.Children) copy.Add(Clone(pair.Key), Clone(pair.Value));
                return copy;
            }
            throw InvalidConfiguration();
        }
        return Clone(root);
    }

    internal static YamlNode? Property(YamlMappingNode node, string key)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var value)) return value;
        // The guarded graph is acyclic and depth-bounded. YAML merge sequences give
        // earlier maps precedence; explicit fields always override merged defaults.
        foreach (var pair in node.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: "<<", Style: ScalarStyle.Plain }) continue;
            if (pair.Value is YamlMappingNode map && Property(map, key) is { } merged) return merged;
            if (pair.Value is YamlSequenceNode sequence)
                foreach (var child in sequence.OfType<YamlMappingNode>())
                    if (Property(child, key) is { } item) return item;
        }
        return null;
    }

    private static InvalidDataException InvalidConfiguration() => new("YAML-конфигурация превышает безопасный размер подключения.");

    private sealed class BoundedWriter : StringWriter
    {
        public override void Write(char value)
        {
            if (GetStringBuilder().Length >= MaximumConfigurationCharacters) throw InvalidConfiguration();
            base.Write(value);
        }

        public override void Write(string? value)
        {
            if (value is not null && GetStringBuilder().Length + value.Length > MaximumConfigurationCharacters) throw InvalidConfiguration();
            base.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            if (GetStringBuilder().Length + count > MaximumConfigurationCharacters) throw InvalidConfiguration();
            base.Write(buffer, index, count);
        }
    }

    [GeneratedRegex("(?:^|\\n)[ \\t\\uFEFF]*[\\\"']?proxies[\\\"']?[ \\t]*:", RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex RootProxies();

    [GeneratedRegex("[\\{,][ \\t\\r\\n]*[\\\"']?proxies[\\\"']?[ \\t\\r\\n]*:", RegexOptions.NonBacktracking, 2_000)]
    private static partial Regex FlowProxies();
}
