using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using ProxyHarbor.Domain;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ProxyHarbor.Infrastructure;

/// <summary>Rejects unusable Clash connection fields without rewriting published credentials or options.</summary>
internal static class ClashConnectionFieldPolicy
{
    // Complete registry in sing-shadowsocks2 v0.2.8, used by Mihomo v1.19.32.
    // Includes methods missing from the abbreviated client documentation.
    private static readonly FrozenSet<string> ShadowsocksCiphers = new[]
    {
        "none", "aes-128-gcm", "aes-192-gcm", "aes-256-gcm", "chacha20-ietf-poly1305",
        "xchacha20-ietf-poly1305", "chacha8-ietf-poly1305", "xchacha8-ietf-poly1305",
        "rabbit128-poly1305", "aes-128-ccm", "aes-192-ccm", "aes-256-ccm",
        "aes-128-gcm-siv", "aes-256-gcm-siv", "aegis-128l", "aegis-256", "aez-384",
        "deoxys-ii-256-128", "lea-128-gcm", "lea-192-gcm", "lea-256-gcm", "ascon128", "ascon128a",
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
        "2022-blake3-chacha8-poly1305", "2022-blake3-aes-128-ccm", "2022-blake3-aes-256-ccm",
        "aes-128-ctr", "aes-192-ctr", "aes-256-ctr", "aes-128-cfb", "aes-192-cfb", "aes-256-cfb",
        "rc4-md5", "chacha20-ietf", "xchacha20", "chacha20"
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static bool IsSupported(YamlMappingNode node, VpnProtocol protocol)
    {
        if (protocol == VpnProtocol.Shadowsocks)
        {
            var cipher = Scalar(node, "cipher");
            return cipher is not null && ShadowsocksCiphers.Contains(cipher) &&
                (cipher == "none" || Scalar(node, "password") is { Length: > 0 });
        }
        if (protocol != VpnProtocol.Vless) return true;
        if (ClashYamlFeedParser.Property(node, "flow") is { } flowNode)
        {
            if (flowNode is not YamlScalarNode) return false;
            var flow = Scalar(node, "flow");
            // Mihomo uses the first 16 UTF-8 bytes, preserving Vision's UDP suffixes.
            if (flow is not null && Encoding.UTF8.GetByteCount(flow) >= 16 &&
                !flow.StartsWith("xtls-rprx-vision", StringComparison.Ordinal)) return false;
        }
        if (ClashYamlFeedParser.Property(node, "reality-opts") is not { } realityNode) return true;
        if (realityNode is YamlScalarNode realityScalar && IsNull(realityScalar)) return true;
        if (realityNode is not YamlMappingNode reality) return false;
        if (ClashYamlFeedParser.Property(reality, "public-key") is { } keyNode && keyNode is not YamlScalarNode) return false;
        var publicKey = Scalar(reality, "public-key");
        // An absent/empty public key disables Reality in the native client.
        if (string.IsNullOrEmpty(publicKey)) return true;
        var key = publicKey.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        if (key.Length != 43 || !key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) return false;
        var shortIdNode = ClashYamlFeedParser.Property(reality, "short-id");
        if (shortIdNode is not null && shortIdNode is not YamlScalarNode) return false;
        var shortId = Scalar(reality, "short-id") ?? "";
        if (shortId.Length > 16 || !shortId.All(char.IsAsciiHexDigit)) return false;
        return IsNativeShortId(shortId, shortIdNode as YamlScalarNode);
    }

    private static bool IsNativeShortId(string value, YamlScalarNode? scalar)
    {
        if (scalar is null || IsNull(scalar)) return true;
        var integerTag = scalar.Tag == new TagName("tag:yaml.org,2002:int");
        var floatTag = scalar.Tag == new TagName("tag:yaml.org,2002:float");
        var resolvesNumber = integerTag || floatTag || scalar.Tag.IsEmpty && scalar.Style == ScalarStyle.Plain;
        if (!resolvesNumber) return value.Length % 2 == 0;
        // go.yaml.in/yaml/v3 v3.0.5 resolves plain numbers before Mihomo's
        // weak string decoder. Decimal/octal/binary integers lose leading zeros;
        // floats become scientific notation containing a sign, which is not hex.
        string? nativeInteger = null;
        if (value.Length > 0 && value.All(char.IsAsciiDigit))
        {
            if (value[0] != '0' || value.All(character => character is >= '0' and <= '7'))
                nativeInteger = Convert.ToUInt64(value, value[0] == '0' ? 8 : 10).ToString(CultureInfo.InvariantCulture);
        }
        else if (value.Length > 2 && value.StartsWith("0b", StringComparison.OrdinalIgnoreCase) &&
            value.AsSpan(2).IndexOfAnyExcept('0', '1') < 0)
            nativeInteger = Convert.ToUInt64(value[2..], 2).ToString(CultureInfo.InvariantCulture);
        if (nativeInteger is not null) return !floatTag && nativeInteger.Length % 2 == 0;
        if (floatTag || integerTag) return false;
        // In the already hex-only input, a finite decimal/exponent value is the
        // only remaining float form. Overflow remains a string in Go YAML.
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return false;
        return value.Length % 2 == 0;
    }

    private static string? Scalar(YamlMappingNode node, string key)
    {
        if (ClashYamlFeedParser.Property(node, key) is not YamlScalarNode scalar) return null;
        return IsNull(scalar) ? null : scalar.Value;
    }

    private static bool IsNull(YamlScalarNode scalar) => scalar.Tag == new TagName("tag:yaml.org,2002:null") ||
        scalar.Tag.IsEmpty && scalar.Style == ScalarStyle.Plain &&
        scalar.Value is "~" or "null" or "Null" or "NULL";
}
