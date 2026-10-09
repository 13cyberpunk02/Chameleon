using System;

namespace Chameleon.Mobile;

public enum VpnStatus { Disconnected, Connecting, Connected, Error }

/// <summary>Живая статистика соединения (для экрана подключения).</summary>
public readonly record struct VpnStats(long BytesUp, long BytesDown, int RttMs);

/// <summary>Управление VPN для UI. Реализуется в Android (MainActivity).</summary>
public interface IVpnController
{
    void Connect(string link);
    void Disconnect();
}