global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProxyHarbor.Infrastructure;

namespace ProxyHarbor.SourceAudit;

// This is bounded endpoint reachability evidence. Complete feed validation,
// country projection and wire-protocol admission remain the collector's job.
public static class SourceEndpointSample
{
    private static readonly string[] CsvHeaders = ["ip", "port", "protocols", "anonymity", "country", "city", "latency_ms", "uptime_pct", "alive", "uptime_24h", "uptime_7d", "reliability"];

    public static bool ContainsEndpoint(string url, byte[] body, bool complete)
    {
        var text = Encoding.UTF8.GetString(body).TrimStart('\uFEFF');
        try
        {
            if (Regex.IsMatch(url, @"\Ahttps://raw\.githubusercontent\.com/hproxy-com/free-proxy-list/(?:refs/heads/)?main/all\.csv(?:\?country=[A-Z]{2})?\z"))
                return ReadCsv(text);
            if (url == "https://raw.githubusercontent.com/proxio-io/proxy-list/main/all.json")
                return ReadProxio(Encoding.UTF8.GetBytes(text), complete);
            return Regex.IsMatch(text, @"(?<![A-Za-z0-9.:_@-])(?:\d{1,3}\.){3}\d{1,3}:\d{1,5}(?![A-Za-z0-9.:_@-])");
        }
        catch (InvalidDataException) { return false; }
        catch (JsonException) { return false; }
    }

    private static bool Endpoint(string host, string port) =>
        IPAddress.TryParse(host, out var address) && address.ToString() == host &&
        !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) &&
        port.Length is >= 1 and <= 5 && port.All(c => c is >= '0' and <= '9') &&
        int.TryParse(port, out var number) && number is >= 1 and <= 65535;

    private static bool KnownProtocol(string value) => value.ToUpperInvariant() is "HTTP" or "HTTPS" or "SOCKS4" or "SOCKS5";

    private static bool ReadCsv(string text)
    {
        using var rows = BoundedCsvReader.ReadRows(text, CsvHeaders.Length).GetEnumerator();
        if (!rows.MoveNext() || !rows.Current.SequenceEqual(CsvHeaders)) return false;
        if (!rows.MoveNext()) return false;
        var row = rows.Current;
        if (row.Length != CsvHeaders.Length || !Endpoint(row[0], row[1]) ||
            row[4].Length != 2 || row[4].Any(c => c is < 'A' or > 'Z')) return false;
        var protocols = row[2].Split('|');
        return protocols.Length > 0 && protocols.Distinct(StringComparer.Ordinal).Count() == protocols.Length &&
            protocols.All(p => p is "http" or "https" or "socks4" or "socks5");
    }

    private static bool ReadProxio(byte[] body, bool complete)
    {
        var reader = new Utf8JsonReader(body, complete, new JsonReaderState(new JsonReaderOptions { MaxDepth = 16 }));
        var inProxies = false;
        var seenProxies = false;
        var found = false;
        var envelopeFailed = false;
        string property = null;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                property = reader.GetString();
                if (property == "proxies")
                {
                    if (seenProxies) return false;
                    seenProxies = true;
                }
                continue;
            }
            if (reader.CurrentDepth == 1 && property is "error" or "success" or "status")
            {
                if (!JsonDocument.TryParseValue(ref reader, out var flag)) break;
                using (flag)
                {
                    var value = flag.RootElement;
                    if (property == "error" && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)) envelopeFailed = true;
                    if (property == "success" && value.ValueKind == JsonValueKind.False) envelopeFailed = true;
                    if (property == "status" && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var status) && status == 0) envelopeFailed = true;
                }
                property = null;
                continue;
            }
            if (reader.TokenType == JsonTokenType.StartArray && reader.CurrentDepth == 1 && property == "proxies")
            {
                inProxies = true;
                property = null;
            }
            else if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 1) inProxies = false;
            else if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 2 && inProxies)
            {
                // A bounded sample may end mid-record. Only complete objects count.
                if (!JsonDocument.TryParseValue(ref reader, out var record)) break;
                using (record) found |= ReadRecord(record.RootElement);
            }
        }
        return found && !envelopeFailed;
    }

    private static bool ReadRecord(JsonElement record)
    {
        foreach (var credential in new[] { "user", "pass", "username", "password" })
            if (record.TryGetProperty(credential, out var secret) && secret.ValueKind != JsonValueKind.Null &&
                (secret.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(secret.GetString()))) return false;
        if (!record.TryGetProperty("ip", out var ip) || ip.ValueKind != JsonValueKind.String ||
            !record.TryGetProperty("port", out var port) || port.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
            !Endpoint(ip.GetString(), port.ValueKind == JsonValueKind.String ? port.GetString() : port.GetRawText()) ||
            !record.TryGetProperty("protocols", out var protocols) || protocols.ValueKind != JsonValueKind.Array) return false;
        return protocols.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && KnownProtocol(p.GetString()));
    }
}
