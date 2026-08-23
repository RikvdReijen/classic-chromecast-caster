using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RikRealization.ClassicCast.Protocol.Rtp;

/// <summary>
/// Cast Streaming payload encryption: AES-128 in counter mode, keyed by the
/// <c>aesKey</c> and <c>aesIvMask</c> agreed in the OFFER.
///
/// The nonce is built the way Open Screen's <c>frame_crypto.cc</c> does it: sixteen zero
/// bytes with the frame id written big-endian at offset 8, then the whole block XORed
/// with the IV mask. Because the nonce is derived from the frame id, every frame starts
/// its own keystream and packets can be encrypted independently.
/// </summary>
public sealed class FrameCrypto : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _ivMask;

    public FrameCrypto(byte[] aesKey, byte[] aesIvMask)
    {
        ArgumentNullException.ThrowIfNull(aesKey);
        ArgumentNullException.ThrowIfNull(aesIvMask);

        if (aesKey.Length != 16)
            throw new ArgumentException("Cast Streaming uses AES-128; the key must be 16 bytes.", nameof(aesKey));
        if (aesIvMask.Length != 16)
            throw new ArgumentException("The IV mask must be 16 bytes.", nameof(aesIvMask));

        // ECB with no padding is the standard way to build CTR mode by hand: we only ever
        // encrypt counter blocks with it, never plaintext, so ECB's usual weakness does
        // not apply. .NET has no built-in CTR transform.
        _aes = Aes.Create();
        _aes.Key = aesKey;
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;

        _ivMask = (byte[])aesIvMask.Clone();
    }

    public byte[] Encrypt(uint frameId, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[plaintext.Length];
        Transform(frameId, plaintext, output);
        return output;
    }

    /// <summary>CTR is symmetric, so this is the same operation as encryption.</summary>
    public byte[] Decrypt(uint frameId, ReadOnlySpan<byte> ciphertext) =>
        Encrypt(frameId, ciphertext);

    private void Transform(uint frameId, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> counter = stackalloc byte[16];
        counter.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(counter[8..], frameId);
        for (int i = 0; i < 16; i++) counter[i] ^= _ivMask[i];

        Span<byte> keystream = stackalloc byte[16];
        using var encryptor = _aes.CreateEncryptor();

        var counterBlock = new byte[16];
        var keystreamBlock = new byte[16];

        for (int offset = 0; offset < input.Length; offset += 16)
        {
            counter.CopyTo(counterBlock);
            encryptor.TransformBlock(counterBlock, 0, 16, keystreamBlock, 0);
            keystreamBlock.CopyTo(keystream);

            int blockLength = Math.Min(16, input.Length - offset);
            for (int i = 0; i < blockLength; i++)
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);

            IncrementBigEndian(counter);
        }
    }

    /// <summary>
    /// Increments the counter as a single 128-bit big-endian integer, matching BoringSSL's
    /// <c>AES_ctr128_encrypt</c>. Incrementing only the low word would desynchronise the
    /// keystream on frames larger than 4 GB of blocks — academic here, but wrong is wrong.
    /// </summary>
    private static void IncrementBigEndian(Span<byte> counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
            if (++counter[i] != 0) break;
    }

    public void Dispose() => _aes.Dispose();
}
