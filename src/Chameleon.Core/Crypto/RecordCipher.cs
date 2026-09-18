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

    internal static (Aead Aead, LengthMask Mask) CreateCiphers(DirectionKeys keys)
        => (new Aead(keys.AeadKey), new LengthMask(keys.MaskKey));
}

/// <summary>
/// Маска длины = первые 2 байта AES-256-ECB(mask_key, 0^8 ‖ counter). Значения те же,
/// что и при поблочном расчёте (формат на проводе не меняется), но считаются
/// БАТЧАМИ: один bulk-вызов AES на 128 record'ов вместо вызова на каждый. Это
/// убирает основную лишнюю крипто-нагрузку на потоковых данных (мелкие вызовы
/// AES-NI не окупаются). Идемпотентно для текущего counter (нужно для PeekBodyLength).
/// </summary>
internal sealed class LengthMask : IDisposable
{
    private const int Batch = 128;
    private readonly Aes _aes;
    private readonly byte[] _input = new byte[Batch * 16];
    private readonly byte[] _output = new byte[Batch * 16];
    private ulong _batchStart;
    private bool _valid;

    public LengthMask(byte[] key)
    {
        _aes = Aes.Create();
        _aes.Key = key;
    }

    public ushort Get(ulong counter)
    {
        if (!_valid || counter < _batchStart || counter >= _batchStart + Batch)
            Refill(counter);
        int i = (int)(counter - _batchStart);
        return BinaryPrimitives.ReadUInt16BigEndian(_output.AsSpan(i * 16, 2));
    }

    private void Refill(ulong start)
    {
        for (int b = 0; b < Batch; b++)
        {
            Span<byte> blk = _input.AsSpan(b * 16, 16);
            blk[..8].Clear();
            BinaryPrimitives.WriteUInt64BigEndian(blk[8..], start + (ulong)b);
        }

        _aes.EncryptEcb(_input, _output, PaddingMode.None); // один bulk AES-NI вызов на 128 масок
        _batchStart = start;
        _valid = true;
    }

    public void Dispose() => _aes.Dispose();
}

/// <summary>Шифрует исходящие record'ы. Не потокобезопасен: вызывающий обязан сериализовать вызовы.</summary>
public sealed class RecordSealer : IDisposable
{
    private readonly Aead _aead;
    private readonly LengthMask _mask;
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
        ushort mask = _mask.Get(_counter);
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
    private readonly LengthMask _mask;
    private ulong _counter;

    public RecordOpener(DirectionKeys keys) => (_aead, _mask) = RecordFormat.CreateCiphers(keys);

    /// <summary>Снимает маску с заголовка следующего record'а. Состояние не меняет.</summary>
    public int PeekBodyLength(ReadOnlySpan<byte> header)
    {
        ushort mask = _mask.Get(_counter);
        int length = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(header) ^ mask);

        if (length < RecordFormat.TagSize || length > RecordFormat.MaxPlaintext + RecordFormat.TagSize)
            throw new ChameleonProtocolException("Недопустимая длина record'а");
        return length;
    }

    /// <param name="body">Шифротекст вместе с тегом (без 2 байт заголовка).</param>
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