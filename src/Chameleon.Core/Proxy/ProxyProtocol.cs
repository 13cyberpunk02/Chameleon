using System.Text;

namespace Chameleon.Core.Proxy;

/// <summary>
/// Разбор заголовка PROXY protocol v1 (текстовый), который nginx (proxy_protocol on)
/// шлёт в начале потока, чтобы передать реальный адрес клиента. Формат:
///   "PROXY TCP4 1.2.3.4 5.6.7.8 56324 443\r\n"
///   "PROXY TCP6 ::1 ::1 56324 443\r\n"
///   "PROXY UNKNOWN\r\n"
/// Заголовок идёт ДО TLS, открытым текстом; его надо считать и отрезать первым.
/// </summary>
public static class ProxyProtocol
{
    private const int MaxHeaderLength = 108; // предел v1 по спецификации
    private static readonly byte[] Signature = [.. "PROXY "u8];

    public readonly record struct Result(bool Present, string? SourceIp);

    /// <summary>
    /// Читает возможный PROXY-заголовок из начала потока. Если он есть - возвращает
    /// исходный IP; если нет (первые байты не "PROXY ") - ничего не потребляет
    /// сверх уже прочитанного и возвращает Present=false с буфером прочитанного.
    /// </summary>
    public static async Task<(Result Result, byte[] Consumed)> TryReadAsync(Stream stream, CancellationToken ct)
    {
        // Читаем побайтно до \n или предела - заголовок короткий и редкий.
        var buffer = new List<byte>(MaxHeaderLength);
        byte[] one = new byte[1];

        for (int i = 0; i < MaxHeaderLength; i++)
        {
            int n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) break;
            buffer.Add(one[0]);
            
            if (buffer.Count == Signature.Length && !StartsWithSignature(buffer))
                return (new Result(false, null), [.. buffer]);

            if (one[0] == (byte)'\n')
            {
                string line = Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd('\r', '\n');
                return (Parse(line), []);
            }
        }
        
        return (new Result(false, null), [.. buffer]);
    }

    private static bool StartsWithSignature(List<byte> b)
    {
        for (int i = 0; i < Signature.Length; i++)
            if (b[i] != Signature[i]) return false;
        return true;
    }

    private static Result Parse(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p is ["PROXY", _, _, _, _, ..] && (p[1] == "TCP4" || p[1] == "TCP6"))
            return new Result(true, p[2]);
        return new Result(true, null);
    }
}
