using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ProxyHarbor.Infrastructure;

/// <summary>Compares YAML settings without equating scalar spellings that the native client interprets differently.</summary>
internal static partial class ClashYamlSettingsComparer
{
    internal static bool Equal(YamlNode left, YamlNode right)
    {
        if (left.NodeType != right.NodeType || left.Tag != right.Tag) return false;
        if (left is YamlScalarNode first && right is YamlScalarNode second)
        {
            if (first.Value != second.Value) return false;
            if (first.Style == second.Style || !first.Tag.IsEmpty ||
                first.Style != ScalarStyle.Plain && second.Style != ScalarStyle.Plain) return true;
            return !CouldResolveImplicitType(first.Value ?? "");
        }
        if (left is YamlSequenceNode sequence && right is YamlSequenceNode otherSequence)
            return sequence.Children.Count == otherSequence.Children.Count &&
                sequence.Children.Zip(otherSequence.Children).All(pair => Equal(pair.First, pair.Second));
        if (left is not YamlMappingNode mapping || right is not YamlMappingNode otherMapping ||
            mapping.Children.Count != otherMapping.Children.Count) return false;
        // RepresentationModel equality ignores scalar style. Retain the actual
        // right-hand keys so typed key differences are also checked recursively.
        var entries = otherMapping.Children.ToDictionary(pair => pair.Key);
        return mapping.Children.All(pair => entries.TryGetValue(pair.Key, out var other) &&
            Equal(pair.Key, other.Key) && Equal(pair.Value, other.Value));
    }

    private static bool CouldResolveImplicitType(string value)
    {
        if (value is "" or "~" or "null" or "Null" or "NULL" or
            "true" or "True" or "TRUE" or "false" or "False" or "FALSE" or
            ".nan" or ".NaN" or ".NAN" or ".inf" or ".Inf" or ".INF" or
            "+.inf" or "+.Inf" or "+.INF" or "-.inf" or "-.Inf" or "-.INF") return true;
        // Go YAML removes underscores before resolving decimal/octal integers
        // and floats. Overflow remains a string. Prefixed integers and date-like
        // values are conservatively kept distinct when only quoting changes.
        var numberText = value.Replace("_", "", StringComparison.Ordinal);
        return double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            double.IsFinite(number) || PrefixedInteger().IsMatch(value) || TimestampPrefix().IsMatch(value);
    }

    [GeneratedRegex("\\A[+-]?0(?:[xX][0-9a-fA-F_]+|[bB][01_]+|[oO][0-7_]+)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedInteger();

    [GeneratedRegex("\\A[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}(?:\\z|[Tt ])", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPrefix();
}
