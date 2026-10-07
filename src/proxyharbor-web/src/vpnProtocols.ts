/** Protocol names shared by the VPN catalog and source editor. */
export const vpnProtocols = [
  'OpenVpn', 'WireGuard', 'Vless', 'Vmess', 'Trojan', 'Shadowsocks',
  'Hysteria2', 'Tuic', 'AnyTls', 'Hysteria', 'ShadowsocksR',
  'HttpProxy', 'Socks4Proxy', 'Socks5Proxy', 'MtProto',
] as const

export type VpnProtocol = typeof vpnProtocols[number]
