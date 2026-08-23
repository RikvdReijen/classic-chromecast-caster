# Latency budget

> Short answer to "can we get the latency down?" — **yes, substantially**, and the single
> biggest win is not the encoder. It is the negotiated playout delay, which Chrome sets
> conservatively and which we control outright.

## Where the milliseconds actually go

Glass-to-glass, 1080p60, Gen 2/3 puck on 5 GHz Wi-Fi. Engineering estimates from the
protocol and hardware characteristics — **not yet measured on real hardware.** Confirming
these is the first job of the spike.

| Stage | Budget | Notes |
| --- | ---: | --- |
| Frame capture | 5–17 ms | `Windows.Graphics.Capture` delivers on vsync. Up to one frame interval of waiting. **Measured 31.3 ms with GDI** — see below. |
| BGRA → NV12 | 1–2 ms | GPU compute shader. Never do this on the CPU. |
| H.264 encode | 3–8 ms | NVENC/QSV low-latency preset, no B-frames, infinite GOP. |
| Packetise + AES-CTR | < 1 ms | Negligible. |
| Network | 5–30 ms | Wi-Fi jitter dominates, not bandwidth. |
| **Receiver playout delay** | **100–400 ms** | **Negotiated by us. The whole ball game.** |
| Hardware decode | 5–15 ms | Gen 1/2/3 all have hardware H.264. |
| TV panel processing | 10–120 ms | Outside our control. Game Mode is worth more than everything above combined. |

## What milestone 4 measured

The estimates above held up everywhere except capture, and there they were badly wrong for
the method actually used:

| Stage | Budgeted | GDI | Desktop Duplication |
| --- | ---: | ---: | ---: |
| Capture | 5–17 ms | 31.3 ms | **10.9 ms** |
| Handing a frame to the encoder | 1–2 ms | 3.4 ms | 8.1 ms |

The capture budget was right for the method that keeps the frame on the GPU and badly
wrong for GDI, where the blit alone exceeded a whole 30 fps frame interval. Desktop
Duplication brought it back in line.

The surprise is the second row. Handing a frame to an ffmpeg subprocess means serialising
1080p BGRA — 8.3 MB — down an anonymous pipe. At a 30 fps target that costs 8 ms; at
60 fps the write alone takes **25.6 ms**, and total throughput drops *below* what a 30 fps
target achieves. The pipe tops out near 324 MB/s while 1080p60 needs about 500 MB/s.

**The process hop is now the biggest remaining cost in the pipeline.** An in-process
encoder removes it entirely — the captured surface would go straight from Desktop
Duplication to the encoder without ever becoming a raw frame in system memory — and it is
also the only way to get a key frame on demand when the receiver signals picture loss.

## What milestone 6 measured and changed

### How low will a real receiver go?

Swept against the Gen 2/3 puck with the deterministic test pattern. The receiver echoes
back the delay it is applying, so this is its own account, not ours:

| Offered | Receiver reported | Checkpoint | Picture loss |
| ---: | ---: | ---: | ---: |
| 120 ms | 120 ms | healthy | none |
| 80 ms | 80 ms | healthy | none |
| 50 ms | 50 ms | healthy | none |
| 30 ms | 30 ms | healthy | none |
| **20 ms** | **20 ms** | healthy | none |

It honoured every value down to 20 ms. Against Open Screen's 400 ms default that is a
**380 ms** saving on the single largest term in the budget. The default is now 60 ms —
not 20, because a jitter buffer that thin has no margin on a worse link than this one.

An earlier run made 120 ms look bad and 50 ms look good. Re-running it showed that was
variance, not a trend. Single runs over Wi-Fi are not evidence.

### Feeding the encoder no longer blocks capture

Capture and the pipe write are independent, but they were running in series, so every
frame waited on an 8 MB write it did not depend on. Overlapping them:

| | Before | After |
| --- | ---: | ---: |
| Encode feed, 60 fps target | 25.6 ms | **2.1 ms** |

That is the whole of the process-hop cost removed from the critical path, without removing
the process. What it did *not* do is reach 1080p60: ffmpeg still cannot ingest and encode
1080p at 60 fps, and now says so by dropping frames rather than by blocking us. An
in-process encoder remains the answer.

### The adaptation loop, and a threshold that was simply wrong

The first controller drove the delay from 100 ms to its 400 ms ceiling on a stream that
was working perfectly — 1147 checkpoint advances, zero picture loss. The threshold treated
ordinary link noise as congestion.

Measured from a healthy link: **about 9% of frames lose a packet and 1.5% need a full
resend**, and retransmission repairs all of it. That is the system working. What actually
signals trouble is the receiver failing to *recover*: frames abandoned wholesale, a
checkpoint that stops moving, or a picture loss.

A second bias needed fixing too. A raise costs 40 ms and a recovery earns back 10, so
reacting to isolated bad intervals ratcheted upward even when the link was fine — four bad
intervals out of forty-six were enough to drift +80 ms. Backing off now needs two bad
intervals in a row. On a healthy link the delay drifts **down** and settles, as intended.

### Adaptive latency: implemented, not confirmed

The RTP header extension that changes playout delay mid-session is implemented to Open
Screen's layout and covered by tests. **The device never visibly honoured it**: across
twelve changes its reported delay stayed at the negotiated value. Either this firmware
ignores mid-stream changes or it reports only what was negotiated, and the feedback gives
no way to tell which. Changing the delay in a way that is certain to take effect means
renegotiating the session.

## The one knob that matters

Cast Streaming negotiates a **target playout delay** in the `OFFER`. The receiver buffers
that much before showing anything, so it is a direct, literal addition to end-to-end
latency. Chrome picks a conservative value for tab casting because it is optimising for
smoothness on an unknown network.

We are not Chrome. We know the user is on a LAN, we know they are mirroring a desktop, and
we can let them choose. Plan:

1. **Negotiate low, adapt up.** Open at ~120 ms. Watch RTCP receiver reports for loss and
   late frames; only raise the delay if the link genuinely cannot hold it.
   **Confirmed in milestone 2:** a real Gen 2/3 puck accepted a 120 ms `targetDelay`
   without objection and did not report a `maxDelay` constraint. Open Screen's 400 ms
   default is a conservative choice, not a hardware limit — so this ~280 ms is genuinely
   ours to take. **Milestone 3 confirmed it end to end:** while streaming, the receiver
   reported its own playout delay back as 120 ms, so it really did adopt the figure
   rather than quietly substituting its default.
2. **Let the mode decide.** A "Presentation" or "Signage" mode can happily sit at 400 ms
   and buy rock-solid smoothness, while a "Desktop / interactive" mode fights for every
   millisecond. This is exactly the split that motivates
   [the signage-window detection issue](../docs/architecture.md#roadmap).
3. **Never let the buffer grow silently.** Drive bitrate from RTCP feedback so queues drain
   instead of accumulating. An encoder that overshoots the link turns into latency, not
   into artefacts.

## Everything else worth doing

- **No B-frames, infinite GOP.** Request keyframes on demand via RTCP PLI rather than
  emitting them on a timer. A periodic IDR at 1080p is a bitrate spike, and a bitrate
  spike on Wi-Fi is a latency spike.
- **Slice-based output.** Emit and packetise NAL slices as the encoder produces them
  instead of waiting for the whole frame.
- **Pace packets.** Bursting a large frame onto Wi-Fi invites collisions. Spread it across
  the frame interval.
- **5 GHz, and pick the channel.** Wi-Fi jitter is the least predictable term in the whole
  table. Gen 1/2/3 have no Ethernet; only the Ultra does.
- **Drop, do not queue.** If a frame is already late, skip it. Showing a stale frame on
  time beats showing a fresh frame late.
- **Answer NACKs promptly, but bound the window.** Milestone 3 showed both halves matter:
  without retransmission a single lost packet stalls the stream permanently, and without
  an in-flight limit the sender races past what the 8-bit frame id lets the receiver
  track. Retransmission is also latency work, not just reliability work — a stalled frame
  is unbounded latency.
- **Tell the user about Game Mode.** On a lot of TVs this is 40–120 ms sitting in the path
  that no amount of protocol work will recover. Surfacing it in the UI is cheap and is
  probably the highest-value latency feature we can ship.

## Target

**~110–260 ms** glass-to-glass excluding TV panel processing, on Gen 2/3 over decent
5 GHz Wi-Fi. Chrome tab mirroring for comparison typically lands well above that, mostly
because of its conservative playout delay.

Gen 1 is the outlier: a weak SoC that realistically caps out around 720p mirroring. Treat
it as a compatibility target, not a performance one.
