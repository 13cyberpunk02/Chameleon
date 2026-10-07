using System;

namespace Chameleon.Mobile;

public enum VpnStatus { Disconnected, Connecting, Connected, Error }

/// <summary>
/// Управление VPN для UI. Реализуется в Android (MainActivity) - запрос разрешения
/// идёт через Activity. UI зовёт через Vpn.Current.
/// </summary>
public interface IVpnController
{
    void Connect(string link);
    void Disconnect();
}