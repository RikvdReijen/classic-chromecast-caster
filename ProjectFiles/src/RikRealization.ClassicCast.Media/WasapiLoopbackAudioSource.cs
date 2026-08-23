using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RikRealization.ClassicCast.Media;

/// <summary>A source of fixed-length PCM frames.</summary>
public interface IAudioSource : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>Samples per channel in one frame.</summary>
    int FrameSamples { get; }

    string Description { get; }

    void Start();

    /// <summary>
    /// Returns exactly one frame of interleaved 16-bit PCM, padded with silence when the
    /// system has not produced enough audio.
    /// </summary>
    short[] ReadFrame();

    /// <summary>Frames returned so far that were silence because no audio was playing.</summary>
    long SilentFrames { get; }
}

/// <summary>
/// Captures whatever Windows is playing, via WASAPI loopback on the default render device.
///
/// Two things make this less simple than it looks. The device's mix format is whatever the
/// user's hardware happens to use — commonly 32-bit float, sometimes 44.1 kHz — while Opus
/// wants 48 kHz, so everything runs through a resampler. And loopback capture delivers
/// nothing at all while the system is silent, which would stall the stream; a steady
/// cadence of frames is produced regardless, padded with silence when there is no sound.
/// </summary>
public sealed class WasapiLoopbackAudioSource : IAudioSource
{
    private const int FrameMilliseconds = 20;   // Opus's natural frame, and Cast's cadence

    private readonly WasapiLoopbackCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly MediaFoundationResampler _resampler;
    private readonly WaveFormat _outputFormat;
    private readonly byte[] _frameBytes;
    private readonly short[] _frameSamples;

    private long _silentFrames;
    private bool _started;
    private bool _disposed;

    public WasapiLoopbackAudioSource(int sampleRate = 48_000, int channels = 2)
    {
        SampleRate = sampleRate;
        Channels = channels;
        FrameSamples = sampleRate / 1000 * FrameMilliseconds;

        _capture = new WasapiLoopbackCapture();
        _outputFormat = new WaveFormat(sampleRate, 16, channels);

        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            // A second of slack. Enough to ride out scheduling hiccups without letting
            // audio latency build up unnoticed.
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
        };

        _resampler = new MediaFoundationResampler(_buffer, _outputFormat)
        {
            ResamplerQuality = 30,
        };

        int frameByteCount = FrameSamples * channels * sizeof(short);
        _frameBytes = new byte[frameByteCount];
        _frameSamples = new short[FrameSamples * channels];

        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0) _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int FrameSamples { get; }
    public long SilentFrames => Interlocked.Read(ref _silentFrames);

    public string Description =>
        $"WASAPI loopback on {SafeDeviceName()} " +
        $"({_capture.WaveFormat.SampleRate} Hz {_capture.WaveFormat.Channels}ch " +
        $"{_capture.WaveFormat.BitsPerSample}-bit -> {SampleRate} Hz {Channels}ch 16-bit)";

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;

        _capture.StartRecording();
        _started = true;
    }

    public short[] ReadFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int read = 0;
        if (_buffer.BufferedBytes > 0)
        {
            try
            {
                read = _resampler.Read(_frameBytes, 0, _frameBytes.Length);
            }
            catch (InvalidOperationException)
            {
                // The resampler can object mid-teardown; treat it as silence.
                read = 0;
            }
        }

        if (read < _frameBytes.Length)
        {
            // Nothing was playing, or not enough of it. Pad rather than skip: a gap in the
            // cadence shows up as an audible click and drifts the audio clock.
            Array.Clear(_frameBytes, read, _frameBytes.Length - read);
            if (read == 0) Interlocked.Increment(ref _silentFrames);
        }

        Buffer.BlockCopy(_frameBytes, 0, _frameSamples, 0, _frameBytes.Length);
        return _frameSamples;
    }

    private string SafeDeviceName()
    {
        try { return _capture.CaptureDeviceName(); }
        catch { return "default output"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { if (_started) _capture.StopRecording(); } catch { }
        try { _resampler.Dispose(); } catch { }
        _capture.Dispose();
    }
}

internal static class WasapiCaptureExtensions
{
    /// <summary>NAudio does not surface the device name directly on the capture object.</summary>
    public static string CaptureDeviceName(this WasapiLoopbackCapture capture)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.FriendlyName;
    }
}
