using ProxyHarbor.Domain;

namespace ProxyHarbor.Infrastructure;

/// <summary>
/// Проверенный список публичных VPN feed'ов. В каталог включаются только официальные
/// страницы и репозитории с явной открытой лицензией; пользовательские URL добавляются отдельно.
/// </summary>
public static class BuiltInVpnSourceCatalog
{
    /// <summary>Дата последней ручной проверки происхождения и лицензий.</summary>
    public static DateOnly LastAuditedOn { get; } = new(2026, 9, 2);

    /// <summary>Начальный набор разрешённых VPN feed'ов.</summary>
    public static IReadOnlyList<VpnSourceDefinition> Sources { get; } =
    [
        new("Auto OVPN catalog", "9xN/auto-ovpn", "https://raw.githubusercontent.com/9xN/auto-ovpn/main/json/data.json", VpnProtocol.OpenVpn, "AGPL-3.0"),

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
        new("Free v2ray Hysteria2", "0xRadikal/Free-v2ray-Configs", "https://raw.githubusercontent.com/0xRadikal/Free-v2ray-Configs/main/protocols/hysteria2.txt", VpnProtocol.Hysteria2, "MIT"),

        // Дополнительные protocol-, subscription- и country-feed'ы, прошедшие live-аудит 28.08.2026.
        new("PyroConfig shadowsocks", "0xAbolfazl/PyroConfig", "https://raw.githubusercontent.com/0xAbolfazl/PyroConfig/main/Configs/shadowsocks.txt", VpnProtocol.Shadowsocks, "MIT"),
        new("PyroConfig trojan", "0xAbolfazl/PyroConfig", "https://raw.githubusercontent.com/0xAbolfazl/PyroConfig/main/Configs/trojan.txt", VpnProtocol.Trojan, "MIT"),
        new("PyroConfig vless", "0xAbolfazl/PyroConfig", "https://raw.githubusercontent.com/0xAbolfazl/PyroConfig/main/Configs/vless.txt", VpnProtocol.Vless, "MIT"),
        new("PyroConfig vmess", "0xAbolfazl/PyroConfig", "https://raw.githubusercontent.com/0xAbolfazl/PyroConfig/main/Configs/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("Alexant ss", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Splitted-By-Protocol/ss.txt", VpnProtocol.Shadowsocks, "MIT"),
        new("Alexant trojan", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Splitted-By-Protocol/trojan.txt", VpnProtocol.Trojan, "MIT"),
        new("Alexant vless", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Splitted-By-Protocol/vless.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant vmess", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Splitted-By-Protocol/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("Alexant mixed 1", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub1.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 2", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub2.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 3", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub3.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 4", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub4.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 5", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub5.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 6", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub6.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 7", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub7.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 8", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub8.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 9", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub9.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 10", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub10.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 11", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub11.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 12", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub12.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 13", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub13.txt", VpnProtocol.Vless, "MIT"),
        new("Alexant mixed 14", "alexantSWE/V2ray-Config", "https://raw.githubusercontent.com/alexantSWE/V2ray-Config/main/Sub14.txt", VpnProtocol.Vless, "MIT"),
        new("Freedom V2Ray ss", "MahanKenway/Freedom-V2Ray", "https://raw.githubusercontent.com/MahanKenway/Freedom-V2Ray/main/configs/ss.txt", VpnProtocol.Shadowsocks, "MIT"),
        new("Freedom V2Ray trojan", "MahanKenway/Freedom-V2Ray", "https://raw.githubusercontent.com/MahanKenway/Freedom-V2Ray/main/configs/trojan.txt", VpnProtocol.Trojan, "MIT"),
        new("Freedom V2Ray vless", "MahanKenway/Freedom-V2Ray", "https://raw.githubusercontent.com/MahanKenway/Freedom-V2Ray/main/configs/vless.txt", VpnProtocol.Vless, "MIT"),
        new("Freedom V2Ray vmess", "MahanKenway/Freedom-V2Ray", "https://raw.githubusercontent.com/MahanKenway/Freedom-V2Ray/main/configs/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("Matin filtered hysteria2", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/filtered/subs/hysteria2.txt", VpnProtocol.Hysteria2, "MIT"),
        new("Matin filtered ss", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/filtered/subs/ss.txt", VpnProtocol.Shadowsocks, "MIT"),
        new("Matin filtered trojan", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/filtered/subs/trojan.txt", VpnProtocol.Trojan, "MIT"),
        new("Matin filtered vless", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/filtered/subs/vless.txt", VpnProtocol.Vless, "MIT"),
        new("Matin filtered vmess", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/filtered/subs/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("Matin mixed 1", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub1.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 2", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub2.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 3", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub3.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 4", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub4.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 5", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub5.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 6", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub6.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 7", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub7.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 8", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub8.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 9", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub9.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 10", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub10.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 11", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub11.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 12", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub12.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 13", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub13.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 14", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub14.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 15", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub15.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 16", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub16.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 17", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub17.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 18", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub18.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 19", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub19.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 20", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub20.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 21", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub21.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 22", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub22.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 23", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub23.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 24", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub24.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 25", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub25.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 26", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub26.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 27", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub27.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 28", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub28.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 29", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub29.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 30", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub30.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 31", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub31.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 32", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub32.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 33", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub33.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 34", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub34.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 35", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub35.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 36", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub36.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 37", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub37.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 38", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub38.txt", VpnProtocol.Vless, "MIT"),
        new("Matin mixed 39", "MatinGhanbari/v2ray-configs", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/v2ray/subs/sub39.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector ss", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/ss.txt", VpnProtocol.Shadowsocks, "MIT"),
        new("Telegram collector trojan", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/trojan.txt", VpnProtocol.Trojan, "MIT"),
        new("Telegram collector vless", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/vless.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector vmess", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("Telegram collector wireguard", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/wireguard.txt", VpnProtocol.WireGuard, "MIT"),
        new("Telegram collector Albania", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Albania.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Armenia", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Armenia.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Australia", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Australia.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Austria", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Austria.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Belgium", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Belgium.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Brazil", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Brazil.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Bulgaria", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Bulgaria.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Canada", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Canada.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector China", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/China.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Czechia", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Czechia.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Denmark", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Denmark.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Estonia", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Estonia.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Finland", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Finland.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector France", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/France.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Germany", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Germany.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Hong Kong", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Hong%20Kong.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector India", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/India.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Ireland", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Ireland.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Italy", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Italy.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Japan", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Japan.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Netherlands", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Netherlands.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector United States", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/United%20States.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Poland", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Poland.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Singapore", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Singapore.txt", VpnProtocol.Vless, "MIT"),
        new("Telegram collector Sweden", "mohamadfg-dev/telegram-v2ray-configs-collector", "https://raw.githubusercontent.com/mohamadfg-dev/telegram-v2ray-configs-collector/main/category/Sweden.txt", VpnProtocol.Vless, "MIT"),

        // Независимые публичные каталоги, найденные и live-проверенные 28.08.2026.
        new("Nyein Ko Ko Aung mixed", "nyeinkokoaung404/V2ray-Configs", "https://raw.githubusercontent.com/nyeinkokoaung404/V2ray-Configs/main/All_Configs_Sub.txt", VpnProtocol.Vless, "MIT"),
        new("Vovaplus secure VLESS", "VovaplusEXP/p-configs", "https://raw.githubusercontent.com/VovaplusEXP/p-configs/main/Splitted-By-Protocol-Secure/vless.txt", VpnProtocol.Vless, "GPL-3.0"),
        new("Vovaplus secure VMess", "VovaplusEXP/p-configs", "https://raw.githubusercontent.com/VovaplusEXP/p-configs/main/Splitted-By-Protocol-Secure/vmess.txt", VpnProtocol.Vmess, "GPL-3.0"),
        new("RichTiTAN tested mixed", "RichTiTAN/V2rayTested", "https://raw.githubusercontent.com/RichTiTAN/V2rayTested/main/working_configs.txt", VpnProtocol.Vless, "GPL-3.0"),
        new("Danialsamadi mixed", "Danialsamadi/v2go", "https://raw.githubusercontent.com/Danialsamadi/v2go/main/AllConfigsSub.txt", VpnProtocol.Vless, "GPL-3.0"),
        new("MhdiTaheri mixed", "MhdiTaheri/V2rayCollector", "https://raw.githubusercontent.com/MhdiTaheri/V2rayCollector/main/sub/mix", VpnProtocol.Vless, "MIT"),
        new("MhdiTaheri Shadowsocks", "MhdiTaheri/V2rayCollector", "https://raw.githubusercontent.com/MhdiTaheri/V2rayCollector/main/sub/ss", VpnProtocol.Shadowsocks, "MIT"),
        new("MhdiTaheri VLESS", "MhdiTaheri/V2rayCollector", "https://raw.githubusercontent.com/MhdiTaheri/V2rayCollector/main/sub/vless", VpnProtocol.Vless, "MIT"),
        new("MhdiTaheri VMess", "MhdiTaheri/V2rayCollector", "https://raw.githubusercontent.com/MhdiTaheri/V2rayCollector/main/sub/vmess", VpnProtocol.Vmess, "MIT"),
        new("MhdiTaheri Trojan", "MhdiTaheri/V2rayCollector", "https://raw.githubusercontent.com/MhdiTaheri/V2rayCollector/main/sub/trojan", VpnProtocol.Trojan, "MIT"),
        new("FlareFeed Shadowsocks", "svinakraft-maker/FlareFeed", "https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/public/ss.txt", VpnProtocol.Shadowsocks, "MIT"),
        new("FlareFeed Trojan", "svinakraft-maker/FlareFeed", "https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/public/trojan.txt", VpnProtocol.Trojan, "MIT"),
        new("FlareFeed VLESS", "svinakraft-maker/FlareFeed", "https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/public/vless.txt", VpnProtocol.Vless, "MIT"),
        new("FlareFeed VMess", "svinakraft-maker/FlareFeed", "https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/public/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("Hamedcode Shadowsocks 1080", "hamedcode/port-based-v2ray-configs", "https://raw.githubusercontent.com/hamedcode/port-based-v2ray-configs/main/detailed/ss/1080.txt", VpnProtocol.Shadowsocks, "GPL-3.0"),
        new("Proxy Hunter tested mixed", "YawStar/Proxy-Hunter", "https://raw.githubusercontent.com/YawStar/Proxy-Hunter/main/configs/proxy_configs_tested.txt", VpnProtocol.Vless, "MIT"),
        new("Jagger tested mixed", "jagger235711/V2rayCollector", "https://raw.githubusercontent.com/jagger235711/V2rayCollector/main/results/mixed_tested.txt", VpnProtocol.Vless, "MIT"),
        new("Adapt VPN mixed", "PrinceVSFX/Adapt-Configs", "https://raw.githubusercontent.com/PrinceVSFX/Adapt-Configs/main/Configs/Adapt_VPN.txt", VpnProtocol.Vless, "MIT"),
        new("VestraNet VLESS", "MustafaBaqer/VestraNet-Nodes", "https://raw.githubusercontent.com/MustafaBaqer/VestraNet-Nodes/main/protocols/vless.txt", VpnProtocol.Vless, "MIT"),
        new("VestraNet VMess", "MustafaBaqer/VestraNet-Nodes", "https://raw.githubusercontent.com/MustafaBaqer/VestraNet-Nodes/main/protocols/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("VestraNet Trojan", "MustafaBaqer/VestraNet-Nodes", "https://raw.githubusercontent.com/MustafaBaqer/VestraNet-Nodes/main/protocols/trojan.txt", VpnProtocol.Trojan, "MIT"),
        new("VestraNet Shadowsocks", "MustafaBaqer/VestraNet-Nodes", "https://raw.githubusercontent.com/MustafaBaqer/VestraNet-Nodes/main/protocols/shadowsocks.txt", VpnProtocol.Shadowsocks, "MIT"),
        new("VestraNet Hysteria2", "MustafaBaqer/VestraNet-Nodes", "https://raw.githubusercontent.com/MustafaBaqer/VestraNet-Nodes/main/protocols/hy2.txt", VpnProtocol.Hysteria2, "MIT"),
        new("VestraNet WireGuard", "MustafaBaqer/VestraNet-Nodes", "https://raw.githubusercontent.com/MustafaBaqer/VestraNet-Nodes/main/protocols/wireguard.txt", VpnProtocol.WireGuard, "MIT"),
        new("Barabama mixed", "Barabama/FreeNodes", "https://raw.githubusercontent.com/Barabama/FreeNodes/feat/ai-crawler-v2/nodes/merged.txt", VpnProtocol.Vless, "MIT"),
        new("Nexus Nodes mixed", "ninjastrikers/nexus-nodes", "https://raw.githubusercontent.com/ninjastrikers/nexus-nodes/main/configs/all.txt", VpnProtocol.Vless, "MIT"),
        new("Nexus Nodes VLESS", "ninjastrikers/nexus-nodes", "https://raw.githubusercontent.com/ninjastrikers/nexus-nodes/main/configs/vless.txt", VpnProtocol.Vless, "MIT"),
        new("Nexus Nodes VMess", "ninjastrikers/nexus-nodes", "https://raw.githubusercontent.com/ninjastrikers/nexus-nodes/main/configs/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("Black Crow VLESS", "nukcrow/black-crow", "https://raw.githubusercontent.com/nukcrow/black-crow/main/sub/protocols/vless.txt", VpnProtocol.Vless, "MIT"),
        new("Black Crow VMess", "nukcrow/black-crow", "https://raw.githubusercontent.com/nukcrow/black-crow/main/sub/protocols/vmess.txt", VpnProtocol.Vmess, "MIT"),
        new("Black Crow Shadowsocks", "nukcrow/black-crow", "https://raw.githubusercontent.com/nukcrow/black-crow/main/sub/protocols/ss.txt", VpnProtocol.Shadowsocks, "MIT"),
        new("Black Crow Trojan", "nukcrow/black-crow", "https://raw.githubusercontent.com/nukcrow/black-crow/main/sub/protocols/trojan.txt", VpnProtocol.Trojan, "MIT"),
        new("Black Crow Hysteria2", "nukcrow/black-crow", "https://raw.githubusercontent.com/nukcrow/black-crow/main/sub/protocols/hysteria2.txt", VpnProtocol.Hysteria2, "MIT"),

        // Российские и зарубежные каталоги, live-проверенные 01.09.2026. Включены
        // только поддерживаемые URI/base64-feed с явной лицензией репозитория.
        new("VPN for Russia VLESS", "igareck/vpn-configs-for-russia", "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/BLACK_VLESS_RUS.txt", VpnProtocol.Vless, "GPL-3.0"),
        new("VPN for Russia mixed", "igareck/vpn-configs-for-russia", "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/BLACK_SS%2BAll_RUS.txt", VpnProtocol.Shadowsocks, "GPL-3.0"),
        new("Russia mirror VLESS", "kort0881/vpn-vless-configs-russia", "https://raw.githubusercontent.com/kort0881/vpn-vless-configs-russia/main/data/githubmirror/clean/vless.txt", VpnProtocol.Vless, "GPL-3.0"),
        new("Russia mirror VMess", "kort0881/vpn-vless-configs-russia", "https://raw.githubusercontent.com/kort0881/vpn-vless-configs-russia/main/data/githubmirror/clean/vmess.txt", VpnProtocol.Vmess, "GPL-3.0"),
        new("Russia mirror Trojan", "kort0881/vpn-vless-configs-russia", "https://raw.githubusercontent.com/kort0881/vpn-vless-configs-russia/main/data/githubmirror/clean/trojan.txt", VpnProtocol.Trojan, "GPL-3.0"),
        new("Russia mirror Shadowsocks", "kort0881/vpn-vless-configs-russia", "https://raw.githubusercontent.com/kort0881/vpn-vless-configs-russia/main/data/githubmirror/clean/ss.txt", VpnProtocol.Shadowsocks, "GPL-3.0"),
        new("Russia mirror Hysteria2", "kort0881/vpn-vless-configs-russia", "https://raw.githubusercontent.com/kort0881/vpn-vless-configs-russia/main/data/githubmirror/clean/hysteria2.txt", VpnProtocol.Hysteria2, "GPL-3.0"),
        new("ALIILA mixed", "ALIILAPRO/v2rayNG-Config", "https://raw.githubusercontent.com/ALIILAPRO/v2rayNG-Config/main/sub.txt", VpnProtocol.Vless, "GPL-3.0"),
        new("Epodonios mixed", "Epodonios/v2ray-configs", "https://raw.githubusercontent.com/Epodonios/v2ray-configs/main/All_Configs_Sub.txt", VpnProtocol.Vless, "GPL-3.0"),
        new("Firmfox VLESS", "Firmfox/Proxify", "https://raw.githubusercontent.com/Firmfox/Proxify/main/v2ray_configs/separated_by_protocol/vless.txt", VpnProtocol.Vless, "GPL-3.0"),
        new("Firmfox VMess", "Firmfox/Proxify", "https://raw.githubusercontent.com/Firmfox/Proxify/main/v2ray_configs/separated_by_protocol/vmess.txt", VpnProtocol.Vmess, "GPL-3.0"),
        new("Firmfox Trojan", "Firmfox/Proxify", "https://raw.githubusercontent.com/Firmfox/Proxify/main/v2ray_configs/separated_by_protocol/trojan.txt", VpnProtocol.Trojan, "GPL-3.0"),
        new("Firmfox Shadowsocks", "Firmfox/Proxify", "https://raw.githubusercontent.com/Firmfox/Proxify/main/v2ray_configs/separated_by_protocol/shadowsocks.txt", VpnProtocol.Shadowsocks, "GPL-3.0"),
        new("Surfboard converted", "Surfboardv2ray/Proxy-sorter", "https://raw.githubusercontent.com/Surfboardv2ray/Proxy-sorter/main/output/converted.txt", VpnProtocol.Vless, "MIT"),
        new("V2ray Tester Pro verified", "Shayanthn/V2ray-Tester-Pro", "https://raw.githubusercontent.com/Shayanthn/V2ray-Tester-Pro/main/subscriptions/subscription.txt", VpnProtocol.Vless, "MPL-2.0"),
        new("FreeProxies mixed", "mfuu/FreeProxies", "https://raw.githubusercontent.com/mfuu/FreeProxies/master/sub", VpnProtocol.Vless, "CC-BY-SA-4.0"),
        new("Au1rxx verified 1", "Au1rxx/free-vpn-subscriptions", "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/all-verified/v2ray-base64-0001.txt", VpnProtocol.Vless, "MIT"),
        new("Au1rxx verified 2", "Au1rxx/free-vpn-subscriptions", "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/all-verified/v2ray-base64-0002.txt", VpnProtocol.Vless, "MIT"),
        new("Au1rxx verified 3", "Au1rxx/free-vpn-subscriptions", "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/all-verified/v2ray-base64-0003.txt", VpnProtocol.Vless, "MIT"),
        new("Au1rxx verified 4", "Au1rxx/free-vpn-subscriptions", "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/all-verified/v2ray-base64-0004.txt", VpnProtocol.Vless, "MIT"),
        new("Au1rxx verified 5", "Au1rxx/free-vpn-subscriptions", "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/all-verified/v2ray-base64-0005.txt", VpnProtocol.Vless, "MIT"),
        new("Au1rxx verified 6", "Au1rxx/free-vpn-subscriptions", "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/all-verified/v2ray-base64-0006.txt", VpnProtocol.Vless, "MIT"),
        new("Au1rxx verified 7", "Au1rxx/free-vpn-subscriptions", "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/all-verified/v2ray-base64-0007.txt", VpnProtocol.Vless, "MIT"),
        new("Au1rxx verified 8", "Au1rxx/free-vpn-subscriptions", "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/all-verified/v2ray-base64-0008.txt", VpnProtocol.Vless, "MIT"),
        new("Au1rxx verified 9", "Au1rxx/free-vpn-subscriptions", "https://raw.githubusercontent.com/Au1rxx/free-vpn-subscriptions/main/output/all-verified/v2ray-base64-0009.txt", VpnProtocol.Vless, "MIT"),
    ];

    /// <summary>Число независимых владельцев встроенных VPN feed.</summary>
    public static int ProviderCount { get; } = Sources.Select(source => source.Provider)
        .Distinct(StringComparer.Ordinal).Count();

    /// <summary>Возвращает каноническое описание только для точного URL.</summary>
    public static VpnSourceDefinition? FindByUrl(string url) =>
        Sources.FirstOrDefault(source => string.Equals(source.Url, url, StringComparison.Ordinal));
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
