using System.Globalization;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace ProxyHarbor.Infrastructure;

/// <summary>Builds a bounded Clash client document with isolated names and complete static dependencies.</summary>
public static class ClashConfigurationExporter
{
    /// <summary>Maximum aggregate input text retained for one export.</summary>
    public const int MaximumInputCharacters = 4 * 1024 * 1024;
    private const int MaximumNodes = 250_000;
    private const int MaximumConnections = 5_000;

    /// <summary>Counts every emitted profile, including complete static dependencies, after validating the saved document.</summary>
    public static int ProfileCount(VpnCandidate candidate)
    {
        if (!ClashYamlFeedParser.TryReadStandalone(candidate, out var root)) throw InvalidExport();
        return ((YamlSequenceNode)root.Children[new YamlScalarNode("proxies")]).Children.Count;
    }

    /// <summary>Streams each profile with its original standalone settings for comparison with the current catalog.</summary>
    public static IEnumerable<VpnCandidate> Profiles(VpnCandidate candidate) =>
        ClashYamlFeedParser.EnumerateExportProfiles(candidate);

    /// <summary>Compares complete connection settings while ignoring mapping order and consistently renamed node/group labels.</summary>
    public static bool SameSettings(VpnCandidate expected, VpnCandidate current)
    {
        if (expected.Host != current.Host || expected.Port != current.Port ||
            expected.Protocol != current.Protocol || expected.Transport != current.Transport) return false;
        if (expected.ClashConfiguration == current.ClashConfiguration)
            return ClashYamlFeedParser.IsValidStandalone(expected);
        try
        {
            var left = ClashYamlFeedReader.ReadRequired(Encoding.UTF8.GetString(Export([expected])));
            var right = ClashYamlFeedReader.ReadRequired(Encoding.UTF8.GetString(Export([current])));
            return ClashYamlSettingsComparer.Equal(left, right);
        }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>Exports complete published YAML settings, refusing unsafe or incomplete input rather than dropping dependencies.</summary>
    public static byte[] Export(IEnumerable<VpnCandidate> candidates, int maximumProfiles = MaximumConnections)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (maximumProfiles is < 1 or > MaximumConnections) throw new ArgumentOutOfRangeException(nameof(maximumProfiles));
        var proxies = new YamlSequenceNode();
        var groups = new YamlSequenceNode();
        var choices = new YamlSequenceNode();
        var count = 0;
        var inputCharacters = 0;
        var nodes = 0;
        var profiles = 0;
        foreach (var candidate in candidates)
        {
            if (++count > MaximumConnections || candidate.ClashConfiguration is not { } configuration ||
                configuration.Length > MaximumInputCharacters - inputCharacters) throw InvalidExport();
            inputCharacters += configuration.Length;
            if (!ClashYamlFeedParser.TryReadStandalone(candidate, out var original)) throw InvalidExport();
            var root = (YamlMappingNode)ClashYamlFeedParser.CloneStandalone(original);
            CountNodes(root);
            var localProxies = (YamlSequenceNode)root.Children[new YamlScalarNode("proxies")];
            if (localProxies.Children.Count > maximumProfiles - profiles) throw InvalidExport();
            profiles += localProxies.Children.Count;
            var localGroups = ClashYamlFeedParser.Property(root, "proxy-groups") as YamlSequenceNode;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var prefix = "PH-" + count.ToString(CultureInfo.InvariantCulture) + "-";
            Rename(localProxies, prefix + "node-");
            if (localGroups is not null) Rename(localGroups, prefix + "group-");
            choices.Add(ClashYamlFeedParser.Property((YamlMappingNode)localProxies.Children[0], "name")!);
            foreach (YamlMappingNode proxy in localProxies)
            {
                if (ClashYamlFeedParser.Property(proxy, "dialer-proxy") is YamlScalarNode { Value: { } dependency })
                    proxy.Children[new YamlScalarNode("dialer-proxy")] = new YamlScalarNode(Resolve(dependency));
                proxies.Add(proxy);
            }
            if (localGroups is not null)
                foreach (YamlMappingNode group in localGroups)
                {
                    var members = (YamlSequenceNode)ClashYamlFeedParser.Property(group, "proxies")!;
                    group.Children[new YamlScalarNode("proxies")] = new YamlSequenceNode(members.Children.Cast<YamlScalarNode>()
                        .Select(member => new YamlScalarNode(Resolve(member.Value!))));
                    groups.Add(group);
                }

            void Rename(YamlSequenceNode collection, string namePrefix)
            {
                for (var index = 0; index < collection.Children.Count; index++)
                {
                    var node = (YamlMappingNode)collection.Children[index];
                    var name = namePrefix + (index + 1).ToString(CultureInfo.InvariantCulture);
                    if (ReferenceEquals(collection, localProxies))
                        name += " · " + Label(node);
                    if (ClashYamlFeedParser.Property(node, "name") is YamlScalarNode { Value: { Length: > 0 } previous } &&
                        !names.TryAdd(previous, name)) throw InvalidExport();
                    node.Children[new YamlScalarNode("name")] = new YamlScalarNode(name);
                }
            }
            string Resolve(string name) => name is "DIRECT" or "REJECT" or "REJECT-DROP" or "PASS" ? name :
                names.TryGetValue(name, out var renamed) ? renamed : throw InvalidExport();
        }
        if (count == 0) throw InvalidExport();
        groups.Add(new YamlMappingNode { { "name", "ProxyHarbor" }, { "type", "select" }, { "proxies", choices } });
        var document = new YamlMappingNode
        {
            { "mixed-port", "7890" }, { "mode", "rule" }, { "allow-lan", "false" },
            { "proxies", proxies }, { "proxy-groups", groups },
            { "rules", new YamlSequenceNode(new YamlScalarNode("MATCH,ProxyHarbor")) }
        };
        using var writer = new ExportWriter();
        new YamlStream(new YamlDocument(document)).Save(writer, assignAnchors: false);
        return Encoding.UTF8.GetBytes(writer.ToString());

        void CountNodes(YamlNode node)
        {
            if (++nodes > MaximumNodes) throw InvalidExport();
            if (node is YamlMappingNode map)
                foreach (var pair in map.Children) { CountNodes(pair.Key); CountNodes(pair.Value); }
            if (node is YamlSequenceNode sequence)
                foreach (var child in sequence.Children) CountNodes(child);
        }
    }

    private static InvalidDataException InvalidExport() => new("Невозможно экспортировать полные безопасные YAML-конфигурации в пределах лимита. Уменьшите число записей.");

    private static string Label(YamlMappingNode node)
    {
        var type = ((YamlScalarNode)ClashYamlFeedParser.Property(node, "type")!).Value;
        var host = ((YamlScalarNode)ClashYamlFeedParser.Property(node, "server")!).Value!;
        var port = ((YamlScalarNode)ClashYamlFeedParser.Property(node, "port")!).Value;
        return type + " " + (host.Contains(':') ? "[" + host + "]" : host) + ":" + port;
    }

    private sealed class ExportWriter : StringWriter
    {
        public override void Write(char value)
        {
            if (GetStringBuilder().Length >= MaximumInputCharacters) throw InvalidExport();
            base.Write(value);
        }

        public override void Write(string? value)
        {
            if (value is not null && value.Length > MaximumInputCharacters - GetStringBuilder().Length) throw InvalidExport();
            base.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            if (count > MaximumInputCharacters - GetStringBuilder().Length) throw InvalidExport();
            base.Write(buffer, index, count);
        }
    }
}
