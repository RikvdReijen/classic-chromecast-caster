using Concentus;
using Concentus.Enums;

namespace RikRealization.ClassicCast.Media;

/// <summary>
/// Opus encoding for the Cast audio stream.
///
/// Concentus is a managed Opus implementation, so there is no native library to ship
/// alongside the app. Opus is the codec Cast mirroring negotiates, and 20 ms frames are
/// both its natural unit and what the receiver expects.
/// </summary>
public sealed class OpusAudioEncoder : IDisposable
{
    private readonly IOpusEncoder _encoder;
    private readonly byte[] _packet = new byte[4000];   // well above any 20 ms frame
    private readonly int _frameSamples;

    private bool _disposed;

    public OpusAudioEncoder(int sampleRate, int channels, int bitRate, int frameSamples)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _frameSamples = frameSamples;

        _encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels,
            OpusApplication.OPUS_APPLICATION_AUDIO);

        _encoder.Bitrate = bitRate;

        // Mirroring is a live stream: there is no going back for a lost packet beyond the
        // NACK window, and any encoder-side lookahead is latency we cannot recover.
        _encoder.UseVBR = false;
        _encoder.Complexity = 5;

        // Discontinuous transmission would stop sending during silence. That saves
        // bandwidth but breaks the steady frame cadence the receiver syncs against.
        _encoder.UseDTX = false;
    }

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Encodes one frame of interleaved 16-bit PCM into a single Opus packet.</summary>
    public byte[] Encode(short[] pcm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // frame_size is samples *per channel*, not the interleaved length.
        int length = _encoder.Encode(pcm.AsSpan(), _frameSamples, _packet.AsSpan(), _packet.Length);
        return _packet.AsSpan(0, length).ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.Dispose();
    }
}
