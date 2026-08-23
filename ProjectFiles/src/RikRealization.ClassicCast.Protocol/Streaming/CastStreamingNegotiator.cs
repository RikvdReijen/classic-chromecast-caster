using System.Net;
using RikRealization.ClassicCast.Protocol.Channel;

namespace RikRealization.ClassicCast.Protocol.Streaming;

/// <summary>The result of a successful OFFER/ANSWER exchange: everything RTP needs.</summary>
public sealed record NegotiatedStreamingSession
{
    /// <summary>Where to send RTP. The receiver's address with the port it chose.</summary>
    public required IPEndPoint Destination { get; init; }

    public required StreamOffer Offer { get; init; }
    public required StreamAnswer Answer { get; init; }

    /// <summary>The streams the receiver actually accepted, paired with its SSRCs.</summary>
    public required IReadOnlyList<AcceptedStream> Accepted { get; init; }

    public AcceptedStream? Video =>
        Accepted.FirstOrDefault(a => a.Stream is CastVideoStream);

    public AcceptedStream? Audio =>
        Accepted.FirstOrDefault(a => a.Stream is CastAudioStream);
}

/// <summary>One of our offered streams that the receiver took, with its chosen SSRC.</summary>
public sealed record AcceptedStream(CastStream Stream, uint ReceiverSsrc)
{
    public override string ToString()
    {
        string codec = Stream switch
        {
            CastVideoStream v => v.Codec.WireName(),
            CastAudioStream a => a.Codec.WireName(),
            _ => "unknown",
        };
        return $"index {Stream.Index} {codec} " +
               $"pt={Stream.RtpPayloadType} ssrc={Stream.Ssrc} rx-ssrc={ReceiverSsrc}";
    }
}

/// <summary>
/// Milestone 2: negotiate a Cast Streaming session over the webrtc namespace.
/// Sends an <c>OFFER</c> to the mirroring app's transport and resolves the <c>ANSWER</c>
/// into the endpoint and stream set that the RTP sender will use.
/// </summary>
public static class CastStreamingNegotiator
{
    public static async Task<NegotiatedStreamingSession> NegotiateAsync(
        CastSession session,
        ReceiverApplication app,
        IPAddress deviceAddress,
        StreamOffer offer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(app);

        if (!app.SupportsMirroring)
            throw new CastNegotiationException(
                $"App {app.AppId} does not advertise {CastSession.NsWebRtc}; " +
                "it cannot negotiate a mirroring session.");

        // The virtual connection to the app transport must already be open; LaunchAsync
        // does it, but re-asserting is cheap and makes this callable in isolation.
        await session.ConnectToAsync(app.TransportId, ct);

        var reply = await session.SendStreamingRequestAsync(
            app.TransportId, offer.ToJson("%SEQ%"), ct);

        var answer = StreamAnswer.Parse(reply);

        if (answer.SendIndexes.Count == 0)
            throw new CastNegotiationException(
                "The receiver accepted the offer but selected none of the streams.");

        var byIndex = offer.Streams.ToDictionary(s => s.Index);
        var accepted = new List<AcceptedStream>();

        for (int i = 0; i < answer.SendIndexes.Count; i++)
        {
            int index = answer.SendIndexes[i];
            if (!byIndex.TryGetValue(index, out var stream))
                throw new CastNegotiationException(
                    $"The receiver selected stream index {index}, which was never offered.");

            // ssrcs is positional against sendIndexes. A short array is out of spec, so
            // fall back rather than throwing away an otherwise usable session.
            uint receiverSsrc = i < answer.Ssrcs.Count ? answer.Ssrcs[i] : 0;
            accepted.Add(new AcceptedStream(stream, receiverSsrc));
        }

        return new NegotiatedStreamingSession
        {
            Destination = new IPEndPoint(deviceAddress, answer.UdpPort),
            Offer = offer,
            Answer = answer,
            Accepted = accepted,
        };
    }
}
