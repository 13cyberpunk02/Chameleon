namespace Chameleon.Core.Crypto;

/// <summary>
/// Размерные константы протокола. (Ранее здесь жил AEAD record-слой; после отказа
/// от TLS-in-TLS шифрование потока делает TLS, а данные кадрируются FrameChannel.)
/// </summary>
public static class RecordFormat
{
    /// <summary>Максимальный размер одного сессионного пакета (открытого текста).</summary>
    public const int MaxPlaintext = 16 * 1024;
}