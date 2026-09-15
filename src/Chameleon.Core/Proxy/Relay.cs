using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using Chameleon.Core.Session;

namespace Chameleon.Core.Proxy;

/// <summary>
/// Перекачивает байты в обе стороны между локальным TCP-сокетом (сокет
/// пользователя на клиенте или соединение с целевым сайтом на сервере) и
/// логическим <see cref="ChameleonStream"/>. Полу-закрытие пробрасывается:
/// EOF с одной стороны закрывает соответствующее направление у другой.
/// </summary>
public static class Relay
{
    public static async Task RunAsync(Socket local, ChameleonStream remote, CancellationToken cancellationToken)
    {
        var localStream = new NetworkStream(local, ownsSocket: false);
        Task upstream = LocalToRemoteAsync(localStream, remote, cancellationToken);
        Task downstream = RemoteToLocalAsync(remote, localStream, local, cancellationToken);
        await Task.WhenAll(upstream, downstream).ConfigureAwait(false);
        try
        {
            local.Dispose();
        }
        catch
        {
            // ignored
        }
    }

    private static async Task LocalToRemoteAsync(Stream local, ChameleonStream remote,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int n;
            while ((n = await local.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                await remote.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ignored
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try
            {
                await remote.CompleteOutputAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static async Task RemoteToLocalAsync(ChameleonStream remote, Stream local, Socket localSocket,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                ReadResult result = await remote.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                    await local.WriteAsync(segment, cancellationToken).ConfigureAwait(false);

                remote.Input.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (Exception)
        {
            // ignored
        }
        finally
        {
            try
            {
                await remote.Input.CompleteAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            try
            {
                localSocket.Shutdown(SocketShutdown.Send);
            }
            catch
            {
                // ignored
            }
        }
    }
}