using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Проверенный список публичных VPN feed'ов. В каталог включаются только официальные
/// страницы и репозитории с явной открытой лицензией; пользовательские URL добавляются отдельно.
/// </summary>
public static class BuiltInVpnSourceCatalog
{
    /// <summary>Дата последней ручной проверки происхождения и лицензий.</summary>
    public static DateOnly LastAuditedOn { get; } = new(2026, 8, 26);

    /// <summary>Начальный набор разрешённых VPN feed'ов.</summary>
    public static IReadOnlyList<VpnSourceDefinition> Sources { get; } =
    [
        new("VPN Gate OpenVPN", "VPN Gate", "https://www.vpngate.net/api/iphone/", VpnProtocol.OpenVpn, "VPN Gate public relay terms"),

        new("V2ray-Config VLESS", "barry-far/V2ray-Config", "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Splitted-By-Protocol/vless.txt", VpnProtocol.Vless, "MIT"),
        new("V2ray-Config VMess", "barry-far/V2ray-Config", "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Splitted-By-Protocol/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("V2ray-Config Trojan", "barry-far/V2ray-Config", "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Splitted-By-Protocol/trojan.txt", VpnProtocol.Trojan, "MIT"),
        new("V2ray-Config Shadowsocks", "barry-far/V2ray-Config", "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Splitted-By-Protocol/ss.txt", VpnProtocol.Shadowsocks, "MIT"),

        new("Free Proxy List VLESS", "gfpcom/free-proxy-list", "https://raw.githubusercontent.com/wiki/gfpcom/free-proxy-list/lists/vless.txt", VpnProtocol.Vless, "MIT"),
        new("Free Proxy List VMess", "gfpcom/free-proxy-list", "https://raw.githubusercontent.com/wiki/gfpcom/free-proxy-list/lists/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("Free Proxy List Trojan", "gfpcom/free-proxy-list", "https://raw.githubusercontent.com/wiki/gfpcom/free-proxy-list/lists/trojan.txt", VpnProtocol.Trojan, "MIT"),
        new("Free Proxy List WireGuard", "gfpcom/free-proxy-list", "https://raw.githubusercontent.com/wiki/gfpcom/free-proxy-list/lists/wireguard.txt", VpnProtocol.WireGuard, "MIT"),
        new("Free Proxy List Hysteria2", "gfpcom/free-proxy-list", "https://raw.githubusercontent.com/wiki/gfpcom/free-proxy-list/lists/hy2.txt", VpnProtocol.Hysteria2, "MIT"),
        new("Free Proxy List TUIC", "gfpcom/free-proxy-list", "https://raw.githubusercontent.com/wiki/gfpcom/free-proxy-list/lists/tuic.txt", VpnProtocol.Tuic, "MIT"),

        new("Free v2ray VLESS", "0xRadikal/Free-v2ray-Configs", "https://raw.githubusercontent.com/0xRadikal/Free-v2ray-Configs/main/protocols/vless.txt", VpnProtocol.Vless, "MIT"),
        new("Free v2ray VMess", "0xRadikal/Free-v2ray-Configs", "https://raw.githubusercontent.com/0xRadikal/Free-v2ray-Configs/main/protocols/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("Free v2ray Trojan", "0xRadikal/Free-v2ray-Configs", "https://raw.githubusercontent.com/0xRadikal/Free-v2ray-Configs/main/protocols/trojan.txt", VpnProtocol.Trojan, "MIT"),
        new("Free v2ray Shadowsocks", "0xRadikal/Free-v2ray-Configs", "https://raw.githubusercontent.com/0xRadikal/Free-v2ray-Configs/main/protocols/shadowsocks.txt", VpnProtocol.Shadowsocks, "MIT"),
        new("Free v2ray Hysteria2", "0xRadikal/Free-v2ray-Configs", "https://raw.githubusercontent.com/0xRadikal/Free-v2ray-Configs/main/protocols/hysteria2.txt", VpnProtocol.Hysteria2, "MIT")
    ];
}

/// <summary>Неизменяемое описание встроенного VPN feed.</summary>
public sealed record VpnSourceDefinition
{
    /// <summary>Создаёт описание встроенного feed.</summary>
    public VpnSourceDefinition(string name, string provider, string url, VpnProtocol protocol, string license) =>
        (Name, Provider, Url, Protocol, License) = (name, provider, url, protocol, license);
    /// <summary>Название feed.</summary>
    public string Name { get; }
    /// <summary>Владелец feed.</summary>
    public string Provider { get; }
    /// <summary>Публичный HTTPS URL.</summary>
    public string Url { get; }
    /// <summary>Протокол содержимого.</summary>
    public VpnProtocol Protocol { get; }
    /// <summary>Лицензия или условия публикации.</summary>
    public string License { get; }
}
