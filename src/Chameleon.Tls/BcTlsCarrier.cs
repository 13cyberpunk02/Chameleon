using System.Net.Sockets;
using Chameleon.Core.Transport;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Chameleon.Tls;

/// <summary>Клиентская TLS-несущая на BouncyCastle с браузерным ClientHello.</summary>
public static class BcTlsCarrier
{
    public static CarrierWrapper Client(string sni) => async (socket, cancellationToken) =>
    {
        var network = new NetworkStream(socket, ownsSocket: true);
        var protocol = new TlsClientProtocol(network);
        var client = new ChromeTlsClient(new BcTlsCrypto(new SecureRandom()), sni);
        
        await Task.Run(() => protocol.Connect(client), cancellationToken).ConfigureAwait(false);
        return new BcDuplexStream(protocol.Stream);
    };
}