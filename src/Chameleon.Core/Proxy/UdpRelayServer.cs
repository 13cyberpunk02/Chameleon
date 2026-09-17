using System.Net;
using System.Net.Sockets;
using Chameleon.Core.Session;

namespace Chameleon.Core.Proxy;

/// <summary>
/// Серверная сторона UDP: поток сессии (StreamKind.Udp) ⇄ реальные UDP-датаграммы.
/// Исходящие датаграммы отправляются сокетом того же семейства, что и адрес
/// назначения (IPv4/IPv6 создаются лениво - работает и там, где IPv6 выключен).
/// Ответы кадрируются обратно с адресом источника.
/// </summary>
public static class UdpRelayServer
{
    public static async Task RunAsync(ChameleonStream stream, CancellationToken ct)
    {
        var sockets = new Dictionary<AddressFamily, Socket>();
        var recvTasks = new List<Task>();
        var writeLock = new SemaphoreSlim(1, 1);

        try
        {
            await UdpDatagram.ReadLoopAsync(stream.Input, async (target, payload, c) =>
            {
                IPEndPoint dst;
                try
                {
                    dst = await target.ResolveAsync(c).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                Socket? sock = GetOrCreate(sockets, dst.AddressFamily, recvTasks, stream, writeLock, ct);
                if (sock is null) return;
                try
                {
                    await sock.SendToAsync(payload, SocketFlags.None, dst, c).ConfigureAwait(false);
                }
                catch
                {
                    // ignored
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var s in sockets.Values)
            {
                try
                {
                    s.Close();
                }
                catch
                {
                    // ignored
                }
            }

            try
            {
                await Task.WhenAll(recvTasks).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            writeLock.Dispose();
        }
    }

    private static Socket? GetOrCreate(
        Dictionary<AddressFamily, Socket> sockets, AddressFamily family, List<Task> recvTasks,
        ChameleonStream stream, SemaphoreSlim writeLock, CancellationToken ct)
    {
        if (sockets.TryGetValue(family, out var existing)) return existing;
        Socket sock;
        try
        {
            sock = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            sock.Bind(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        }
        catch
        {
            return null;
        }

        sockets[family] = sock;
        recvTasks.Add(ReceiveLoopAsync(sock, stream, writeLock, ct));
        return sock;
    }

    private static async Task ReceiveLoopAsync(Socket sock, ChameleonStream stream, SemaphoreSlim writeLock,
        CancellationToken ct)
    {
        byte[] buf = new byte[65535];
        var from = new IPEndPoint(
            sock.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await sock.ReceiveFromAsync(buf, SocketFlags.None, from, ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            var src = (IPEndPoint)r.RemoteEndPoint;
            await writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await UdpDatagram.WriteAsync(stream, UdpTarget.FromEndPoint(src), buf.AsMemory(0, r.ReceivedBytes), ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
            finally
            {
                writeLock.Release();
            }
        }
    }
}