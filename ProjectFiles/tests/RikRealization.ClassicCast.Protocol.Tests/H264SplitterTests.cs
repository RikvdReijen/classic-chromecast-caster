using RikRealization.ClassicCast.Protocol.Media;
using Xunit;

namespace RikRealization.ClassicCast.Protocol.Tests;

/// <summary>
/// A pipe delivers arbitrary chunks with no frame boundaries, so the splitter has to
/// reassemble pictures itself. Getting this wrong sends the decoder truncated frames,
/// which shows up as a stalled or torn picture rather than an error.
/// </summary>
public class H264StreamSplitterTests
{
    private const byte FirstSliceOfPicture = 0x80;   // first_mb_in_slice = 0
    private const byte ContinuationSlice = 0x42;     // first_mb_in_slice = 1

    private static byte[] Nal(int type, byte firstPayloadByte = FirstSliceOfPicture) =>
        new byte[] { 0x00, 0x00, 0x00, 0x01, (byte)(type & 0x1F), firstPayloadByte, 0x42, 0x42 };

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] KeyFrame() => Concat(Nal(7), Nal(8), Nal(5));
    private static byte[] DeltaFrame() => Concat(Nal(1));

    [Fact]
    public void Emits_a_frame_only_once_the_next_one_starts()
    {
        var splitter = new H264StreamSplitter();

        // The first picture cannot be known to be complete yet.
        Assert.Empty(splitter.Push(KeyFrame()));

        // The second picture's arrival proves the first one ended.
        var units = splitter.Push(DeltaFrame());
        var unit = Assert.Single(units);
        Assert.True(unit.IsKeyFrame);
        Assert.Equal(KeyFrame(), unit.Data.ToArray());
    }

    [Fact]
    public void Reassembles_a_frame_split_across_many_chunks()
    {
        var splitter = new H264StreamSplitter();
        var stream = Concat(KeyFrame(), DeltaFrame());

        var units = new List<H264AccessUnit>();
        foreach (byte b in stream)
            units.AddRange(splitter.Push(new[] { b }));

        var unit = Assert.Single(units);
        Assert.Equal(KeyFrame(), unit.Data.ToArray());
    }

    [Fact]
    public void Handles_several_frames_arriving_in_one_chunk()
    {
        var splitter = new H264StreamSplitter();

        var units = splitter.Push(Concat(KeyFrame(), DeltaFrame(), DeltaFrame(), DeltaFrame()));

        Assert.Equal(3, units.Count);
        Assert.True(units[0].IsKeyFrame);
        Assert.All(units.Skip(1), u => Assert.False(u.IsKeyFrame));
    }

    [Fact]
    public void FlushPending_releases_the_last_frame_without_waiting_for_the_next()
    {
        // This is what reclaims the frame of latency the boundary rule would otherwise
        // cost when the encoder goes quiet.
        var splitter = new H264StreamSplitter();
        splitter.Push(KeyFrame());

        var flushed = splitter.FlushPending();

        Assert.NotNull(flushed);
        Assert.True(flushed!.IsKeyFrame);
        Assert.Equal(0, splitter.PendingBytes);
    }

    [Fact]
    public void FlushPending_refuses_a_partial_picture()
    {
        // Parameter sets with no slice yet are the start of a frame, not a frame.
        var splitter = new H264StreamSplitter();
        splitter.Push(Concat(Nal(7), Nal(8)));

        Assert.Null(splitter.FlushPending());
        Assert.True(splitter.PendingBytes > 0);
    }

    [Fact]
    public void FlushPending_on_an_empty_splitter_yields_nothing()
    {
        Assert.Null(new H264StreamSplitter().FlushPending());
    }

    [Fact]
    public void A_multi_slice_picture_emerges_as_one_access_unit()
    {
        var splitter = new H264StreamSplitter();

        var picture = Concat(
            Nal(7), Nal(8),
            Nal(5, FirstSliceOfPicture),
            Nal(5, ContinuationSlice),
            Nal(5, ContinuationSlice));

        Assert.Empty(splitter.Push(picture));

        var units = splitter.Push(Nal(1, FirstSliceOfPicture));
        var unit = Assert.Single(units);

        Assert.True(unit.IsKeyFrame);
        Assert.Equal(picture, unit.Data.ToArray());
    }

    [Fact]
    public void Emitted_frames_do_not_alias_the_internal_buffer()
    {
        // The buffer is reused and compacted, so an emitted frame has to own its bytes.
        var splitter = new H264StreamSplitter();
        splitter.Push(KeyFrame());

        var units = splitter.Push(DeltaFrame());
        var snapshot = units[0].Data.ToArray();

        for (int i = 0; i < 20; i++) splitter.Push(DeltaFrame());

        Assert.Equal(snapshot, units[0].Data.ToArray());
    }
}
