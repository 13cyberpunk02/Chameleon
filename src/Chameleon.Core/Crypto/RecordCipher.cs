using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Chameleon.Core.Crypto;

/// <summary>
/// Формат record'а на проводе:
///   masked_len (2) ‖ ciphertext (N) ‖ tag (16)
/// Ни одного открытого поля: для наблюдателя поток выглядит как случайные байты.
/// </summary>
public static class RecordFormat
{
    public const int HeaderSize = 2;
    public const int TagSize = 16;
    public const int MaxPlaintext = 16 * 1024;
    public const int Overhead = HeaderSize + TagSize;
    public const int MaxRecordSize = MaxPlaintext + Overhead;

    internal static void WriteNonce(Span<byte> nonce, ulong counter)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }

    /// <summary>Маска длины = первые 2 байта AES-256-ECB(mask_key, 0^8 ‖ counter).</summary>
    internal static ushort ComputeLengthMask(Aes maskCipher, ulong counter)
    {
        Span<byte> block = stackalloc byte[16];
        block[..8].Clear();
        BinaryPrimitives.WriteUInt64BigEndian(block[8..], counter);

        Span<byte> output = stackalloc byte[16];
        maskCipher.EncryptEcb(block, output, PaddingMode.None);
        return BinaryPrimitives.ReadUInt16BigEndian(output);
    }

    internal static (Aead Aead, Aes Mask) CreateCiphers(DirectionKeys keys)
    {
        var aes = Aes.Create();
        aes.Key = keys.MaskKey;
        return (new Aead(keys.AeadKey), aes);
    }
}

/// <summary>Шифрует исходящие record'ы. Не потокобезопасен: вызывающий обязан сериализовать вызовы.</summary>
public sealed class RecordSealer : IDisposable
{
    private readonly Aead _aead;
    private readonly Aes _mask;
    private ulong _counter;

    public RecordSealer(DirectionKeys keys) => (_aead, _mask) = RecordFormat.CreateCiphers(keys);

    public int Seal(ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        if (plaintext.Length > RecordFormat.MaxPlaintext)
            throw new ArgumentException("Открытый текст больше максимума record'а", nameof(plaintext));

        int total = RecordFormat.Overhead + plaintext.Length;
        if (destination.Length < total)
            throw new ArgumentException("Буфер слишком мал", nameof(destination));
        if (_counter == ulong.MaxValue)
            throw new InvalidOperationException("Счётчик исчерпан, нужна смена ключей");

        Span<byte> nonce = stackalloc byte[12];
        RecordFormat.WriteNonce(nonce, _counter);

        var ciphertext = destination.Slice(RecordFormat.HeaderSize, plaintext.Length);
        var tag = destination.Slice(RecordFormat.HeaderSize + plaintext.Length, RecordFormat.TagSize);
        _aead.Encrypt(nonce, plaintext, ciphertext, tag);

        ushort length = (ushort)(plaintext.Length + RecordFormat.TagSize);
        ushort mask = RecordFormat.ComputeLengthMask(_mask, _counter);
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)(length ^ mask));

        _counter++;
        return total;
    }

    public void Dispose()
    {
        _aead.Dispose();
        _mask.Dispose();
    }
}

/// <summary>Расшифровывает входящие record'ы. Счётчик сдвигается только после успешной проверки тега.</summary>
public sealed class RecordOpener : IDisposable
{
    private readonly Aead _aead;
    private readonly Aes _mask;
    private ulong _counter;

    public RecordOpener(DirectionKeys keys) => (_aead, _mask) = RecordFormat.CreateCiphers(keys);

    /// <summary>Снимает маску с заголовка следующего record'а. Состояние не меняет.</summary>
    public int PeekBodyLength(ReadOnlySpan<byte> header)
    {
        ushort mask = RecordFormat.ComputeLengthMask(_mask, _counter);
        int length = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(header) ^ mask);

        if (length < RecordFormat.TagSize || length > RecordFormat.MaxPlaintext + RecordFormat.TagSize)
            throw new ChameleonProtocolException("Недопустимая длина record'а");
        return length;
    }

    /// <param name="body">Шифротекст вместе с тегом (без 2 байт заголовка).</param>
    /// <param name="destination">Цель конечная</param>
    public int Open(ReadOnlySpan<byte> body, Span<byte> destination)
    {
        int plaintextLength = body.Length - RecordFormat.TagSize;
        if (plaintextLength < 0)
            throw new ChameleonProtocolException("Record короче тега");
        if (destination.Length < plaintextLength)
            throw new ArgumentException("Буфер слишком мал", nameof(destination));

        Span<byte> nonce = stackalloc byte[12];
        RecordFormat.WriteNonce(nonce, _counter);

        try
        {
            _aead.Decrypt(nonce, body[..plaintextLength], body[plaintextLength..], destination[..plaintextLength]);
        }
        catch (CryptographicException)
        {
            throw new ChameleonProtocolException("Проверка подлинности record'а не прошла");
        }

        _counter++;
        return plaintextLength;
    }

    public void Dispose()
    {
        _aead.Dispose();
        _mask.Dispose();
    }
}