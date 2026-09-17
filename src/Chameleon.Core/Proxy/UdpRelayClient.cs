using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Chameleon.Core.Session;

namespace Chameleon.Core.Proxy;

/// <summary>
/// Клиентская сторона UDP: локальный UDP-сокет (куда tun2socks шлёт датаграммы с
/// SOCKS5-заголовком) ⇄ поток сессии. Наружу датаграммы кадрируются с адресом
/// назначения, ответы возвращаются приложению с SOCKS5-заголовком источника.
/// Живёт, пока открыт управляющий TCP-сокет SOCKS5 (его закрытие завершает всё).
/// </summary>
public static class UdpRelayClient
{
    public static async Task RunAsync(Socket udpSocket, NetworkStream control, ChameleonStream stream,
        CancellationToken ct)
    {
        EndPoint? appEndpoint = null;
        var writeLock = new SemaphoreSlim(1, 1);

        var fromTunnel = UdpDatagram.ReadLoopAsync(stream.Input, async (src, payload, c) =>
        {
            if (appEndpoint is null) return;
            byte[] dgram = BuildSocksUdp(src, payload);
            await udpSocket.SendToAsync(dgram, SocketFlags.None, appEndpoint, c).ConfigureAwait(false);
        }, ct);

        var fromApp = Task.Run(async () =>
        {
            byte[] buf = new byte[65535];
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult r;
                try
                {
                    r = await udpSocket.ReceiveFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                appEndpoint = r.RemoteEndPoint;

                if (!TryParseSocksUdp(buf.AsSpan(0, r.ReceivedBytes), out UdpTarget target, out int dataOffset))
                    continue;
                await writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await UdpDatagram
                        .WriteAsync(stream, target, buf.AsMemory(dataOffset, r.ReceivedBytes - dataOffset), ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    writeLock.Release();
                }
            }
        }, ct);

        var control_ = Task.Run(async () =>
        {
            byte[] b = new byte[256];
            try
            {
                while (await control.ReadAsync(b, ct).ConfigureAwait(false) > 0)
                {
                }
            }
            catch
            {
                // ignored
            }
        }, ct);

        await Task.WhenAny(fromTunnel, fromApp, control_).ConfigureAwait(false);
        try
        {
            udpSocket.Close();
        }
        catch
        {
            // ignored
        }

        writeLock.Dispose();
    }

    private static bool TryParseSocksUdp(ReadOnlySpan<byte> d, out UdpTarget target, out int dataOffset)
    {
        target = default;
        dataOffset = 0;
        if (d.Length < 4 || d[2] != 0) return false; // FRAG не поддерживаем
        int p = 3;
        byte atyp = d[p++];
        byte[] addr;
        switch (atyp)
        {
            case 0x01:
                addr = d.Slice(p, 4).ToArray();
                p += 4;
                break;
            case 0x04:
                addr = d.Slice(p, 16).ToArray();
                p += 16;
                break;
            case 0x03:
                int n = d[p++];
                addr = d.Slice(p, n).ToArray();
                p += n;
                break;
            default: return false;
        }

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p));
        p += 2;
        byte tt = atyp == 0x01 ? UdpTarget.IPv4 : atyp == 0x04 ? UdpTarget.IPv6 : UdpTarget.Domain;
        target = new UdpTarget(tt, addr, port);
        dataOffset = p;
        return true;
    }

    private static byte[] BuildSocksUdp(UdpTarget src, byte[] payload)
    {
        int addrLen = src.AddressType == UdpTarget.Domain ? 1 + src.Address.Length : src.Address.Length;
        byte[] d = new byte[3 + 1 + addrLen + 2 + payload.Length];
        d[0] = 0;
        d[1] = 0;
        d[2] = 0; // RSV, FRAG
        int p = 3;
        d[p++] = src.AddressType == UdpTarget.IPv4 ? (byte)0x01 :
            src.AddressType == UdpTarget.IPv6 ? (byte)0x04 : (byte)0x03;
        if (src.AddressType == UdpTarget.Domain) d[p++] = (byte)src.Address.Length;
        src.Address.CopyTo(d.AsSpan(p));
        p += src.Address.Length;
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(p), src.Port);
        p += 2;
        payload.CopyTo(d.AsSpan(p));
        return d;
    }
}