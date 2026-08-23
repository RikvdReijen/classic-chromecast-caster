namespace RikRealization.ClassicCast.Protocol.Rtp;

/// <summary>
/// Chooses the receiver's playout delay from how the link is actually behaving.
///
/// The delay is a literal addition to end-to-end latency, so the goal is to run as low as
/// the network will tolerate. A thin jitter buffer is fragile, though: on a congested link
/// a delay that was comfortable a moment ago starts dropping frames. So this opens low,
/// backs off quickly when the receiver starts complaining, and creeps back down only after
/// a sustained clean run.
///
/// Backing off is deliberately faster than recovering. Stutter is immediately obvious to a
/// viewer; a few tens of milliseconds of extra latency is not.
/// </summary>
public sealed class PlayoutDelayController
{
    // Calibrated against a real link rather than guessed. A healthy Wi-Fi stream NACKs
    // continuously — measured around 9% of frames losing a packet, with 1.5% of frames
    // needing a full resend — and recovers from all of it. Treating that as congestion
    // drove the delay straight to its ceiling on a stream that was working perfectly.
    // What actually signals trouble is the receiver failing to recover: frames abandoned
    // wholesale, a checkpoint that stops moving, or an outright picture loss.

    /// <summary>Fraction of frames the receiver gives up on entirely before we react.</summary>
    private const double WholeFrameLossThreshold = 0.05;

    /// <summary>Packet NACKs per frame that indicate more than ordinary link noise.</summary>
    private const double PacketNackThreshold = 0.5;

    /// <summary>Clean intervals required before trying a lower delay again.</summary>
    private const int CleanIntervalsBeforeRecovery = 4;

    /// <summary>
    /// Bad intervals in a row before backing off. One is not enough: a single burst of
    /// interference is transient, and because a raise costs four times what a recovery
    /// earns back, reacting to isolated bad intervals ratchets the delay upward on a link
    /// that is basically fine.
    /// </summary>
    private const int BadIntervalsBeforeBackoff = 2;

    private readonly TimeSpan _minimum;
    private readonly TimeSpan _maximum;
    private readonly TimeSpan _increaseStep;
    private readonly TimeSpan _decreaseStep;

    private readonly object _lock = new();

    private TimeSpan _current;
    private int _nackedPackets;
    private int _wholeFrameNacks;
    private int _pictureLosses;
    private int _framesSent;
    private int _checkpointAdvances;
    private int _cleanIntervals;
    private int _badIntervals;

    public PlayoutDelayController(
        TimeSpan initial,
        TimeSpan? minimum = null,
        TimeSpan? maximum = null,
        TimeSpan? increaseStep = null,
        TimeSpan? decreaseStep = null)
    {
        _minimum = minimum ?? TimeSpan.FromMilliseconds(40);
        _maximum = maximum ?? TimeSpan.FromMilliseconds(400);
        _increaseStep = increaseStep ?? TimeSpan.FromMilliseconds(40);
        _decreaseStep = decreaseStep ?? TimeSpan.FromMilliseconds(10);

        _current = Clamp(initial);
    }

    public TimeSpan Current { get { lock (_lock) return _current; } }

    /// <summary>How many times the delay has been changed since the session began.</summary>
    public int Adjustments { get; private set; }

    public void RecordFeedback(CastReceiverFeedback feedback)
    {
        lock (_lock)
        {
            _nackedPackets += feedback.NackedPacketCount;
            _wholeFrameNacks += feedback.FullFrameNackCount;
            if (feedback.PictureLossIndicated) _pictureLosses++;
        }
    }

    public void RecordFrameSent()
    {
        lock (_lock) _framesSent++;
    }

    /// <summary>
    /// The receiver completed another frame. A checkpoint that stops advancing while we
    /// are still sending is the clearest sign the link is genuinely failing, as opposed to
    /// merely losing packets it can recover.
    /// </summary>
    public void RecordCheckpointAdvanced()
    {
        lock (_lock) _checkpointAdvances++;
    }

    /// <summary>
    /// Closes one control interval. Returns the new delay when it changed, otherwise null.
    /// Call roughly once a second: often enough to react, rarely enough that a single
    /// unlucky burst does not move the target.
    /// </summary>
    public TimeSpan? Tick()
    {
        lock (_lock)
        {
            int frames = Math.Max(_framesSent, 1);

            // Only count a stall once enough frames have gone out for silence to mean
            // something; a couple of frames with no acknowledgement proves nothing.
            bool stalled = _framesSent >= 10 && _checkpointAdvances == 0;

            bool struggling =
                _pictureLosses > 0 ||
                stalled ||
                _wholeFrameNacks > frames * WholeFrameLossThreshold ||
                _nackedPackets > frames * PacketNackThreshold;

            _nackedPackets = 0;
            _wholeFrameNacks = 0;
            _pictureLosses = 0;
            _framesSent = 0;
            _checkpointAdvances = 0;

            if (struggling)
            {
                _cleanIntervals = 0;
                if (++_badIntervals < BadIntervalsBeforeBackoff) return null;

                _badIntervals = 0;

                var raised = Clamp(_current + _increaseStep);
                if (raised == _current) return null;

                _current = raised;
                Adjustments++;
                return _current;
            }

            _badIntervals = 0;
            if (++_cleanIntervals < CleanIntervalsBeforeRecovery) return null;

            _cleanIntervals = 0;

            var lowered = Clamp(_current - _decreaseStep);
            if (lowered == _current) return null;

            _current = lowered;
            Adjustments++;
            return _current;
        }
    }

    private TimeSpan Clamp(TimeSpan value) =>
        value < _minimum ? _minimum : value > _maximum ? _maximum : value;
}
