using System.Net.Sockets;

namespace ProxyHarbor.Infrastructure;

/// <summary>Проверяющий не получил доказательства отказа endpoint; результат нельзя засчитывать как ошибку VPN.</summary>
internal sealed class VpnProbeDeferredException(string message, Exception? inner = null) : IOException(message, inner)
{
    internal static bool IsLocalFailure(SocketException exception) => exception.SocketErrorCode is
        SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.AddressFamilyNotSupported or
        SocketError.ProtocolFamilyNotSupported or SocketError.AddressNotAvailable or SocketError.TooManyOpenSockets or
        SocketError.NoBufferSpaceAvailable or SocketError.SystemNotReady or SocketError.AccessDenied;

    internal static VpnProbeDeferredException FromSocket(SocketException exception) =>
        new($"Проверка отложена: сеть или ресурсы проверяющего недоступны ({exception.SocketErrorCode}).", exception);
}
