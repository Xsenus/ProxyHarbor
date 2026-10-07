using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace ProxyHarbor.Infrastructure;

/// <summary>Reads complete Clash documents without constructing objects or discarding connection settings.</summary>
internal static class ClashYamlFeedReader
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumEvents = 1_000_000;
    private const int MaximumDepth = 64;
    private const int MaximumScalarLength = 16_384;
    private const int MaximumAliases = 50_000;
    private const int MaximumRecords = 50_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static YamlMappingNode ReadRequired(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            if (content.Length > MaximumBytes || StrictUtf8.GetByteCount(content) > MaximumBytes) throw InvalidFeed();
            var text = content.TrimStart('\uFEFF');
            GuardEvents(text);
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root) throw InvalidFeed();
            GuardExpansion(root);
            if (!root.Children.TryGetValue(new YamlScalarNode("proxies"), out var value) ||
                value is not YamlSequenceNode proxies || proxies.Count() is < 1 or > MaximumRecords ||
                proxies.Any(node => node is not YamlMappingNode)) throw InvalidFeed();
            return root;
        }
        // Parser diagnostics can contain credentials from the original line.
        catch (YamlException) { throw InvalidFeed(); }
        catch (EncoderFallbackException) { throw InvalidFeed(); }
    }

    private static void GuardEvents(string content)
    {
        var parser = new Parser(new StringReader(content));
        var events = 0;
        var depth = 0;
        var aliases = 0;
        var containers = new Stack<(bool Mapping, bool ExpectKey)>();
        while (parser.MoveNext())
        {
            if (++events > MaximumEvents) throw InvalidFeed();
            // Complex or aliased mapping keys can recurse during dictionary hashing
            // before the representation model's graph is available for inspection.
            if (parser.Current is Scalar or AnchorAlias or MappingStart or SequenceStart &&
                containers.TryPeek(out var parent) && parent.Mapping)
            {
                if (parent.ExpectKey && parser.Current is not Scalar) throw InvalidFeed();
                containers.Pop();
                containers.Push((true, !parent.ExpectKey));
            }
            if (parser.Current is MappingStart) containers.Push((true, true));
            if (parser.Current is SequenceStart) containers.Push((false, false));
            if (parser.Current is MappingEnd or SequenceEnd)
            {
                if (!containers.TryPop(out var finished) || finished.Mapping && !finished.ExpectKey) throw InvalidFeed();
            }
            if (parser.Current is MappingStart or SequenceStart) depth++;
            if (parser.Current is MappingEnd or SequenceEnd) depth--;
            if (depth is < 0 or > MaximumDepth) throw InvalidFeed();
            if (parser.Current is AnchorAlias && ++aliases > MaximumAliases) throw InvalidFeed();
            if (parser.Current is Scalar scalar && scalar.Value.Length > MaximumScalarLength) throw InvalidFeed();
        }
    }

    private static void GuardExpansion(YamlNode root)
    {
        var nodes = 0;
        var path = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        void Visit(YamlNode node, int depth)
        {
            if (++nodes > MaximumEvents || depth > MaximumDepth || !path.Add(node)) throw InvalidFeed();
            if (node is YamlMappingNode mapping)
                foreach (var pair in mapping.Children)
                {
                    if (pair.Key is not YamlScalarNode) throw InvalidFeed();
                    Visit(pair.Key, depth + 1);
                    Visit(pair.Value, depth + 1);
                }
            else if (node is YamlSequenceNode sequence)
                foreach (var child in sequence.Children) Visit(child, depth + 1);
            path.Remove(node);
        }
        Visit(root, 0);
    }

    private static InvalidDataException InvalidFeed() => new("Источник не содержит безопасного полного YAML-списка Clash.");
}
