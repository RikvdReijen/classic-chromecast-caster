# Architecture

## Pipeline

```
 Windows desktop
      |
      |  Windows.Graphics.Capture (DXGI, zero-copy)      ~5-17ms
      v
 [ BGRA -> NV12 GPU shader ]                             ~1-2ms
      |
      v
 [ H.264 encode: NVENC / QuickSync / AMF ]               ~3-8ms
 [ VP8 (libvpx) software fallback          ]
      |                                    WASAPI loopback -> Opus
      v                                             |
 [ Cast Streaming: RTP packetise + AES-128-CTR ] <---+
      |
      |  UDP, port from ANSWER
      v
 Chromecast built-in "Chrome Mirroring" receiver (0F5096E8)
      |
      v
 Hardware H.264 decode -> HDMI -> TV
```

Control runs on a separate, long-lived TLS connection; only media goes over UDP.

## Protocol layers

### 1. Discovery
mDNS/DNS-SD for `_googlecast._tcp.local`. The TXT record carries the fields we surface in
the UI: `fn` (friendly name), `md` (model, which is how we tell a Gen 1 from an Ultra),
`id` (stable device UUID) and `ca` (capability bitmask).

### 2. Control channel — CASTV2
TLS to port 8009. The device presents a self-signed certificate, so standard chain
validation must be bypassed deliberately and only for this connection.

Framing is a 4-byte big-endian length prefix followed by a protobuf `CastMessage`
(`source_id`, `destination_id`, `namespace`, `payload_utf8` / `payload_binary`).

| Namespace | Use |
| --- | --- |
| `…cast.tp.connection` | `CONNECT` / `CLOSE` |
| `…cast.tp.heartbeat` | `PING` / `PONG` — must be kept up or the device drops us |
| `…cast.receiver` | `GET_STATUS`, `LAUNCH`, `STOP` |
| `…cast.webrtc` | `OFFER` / `ANSWER` — Cast Streaming negotiation |

Sequence: connect to `receiver-0` → `LAUNCH` app `0F5096E8` → read the resulting
`transportId` from the receiver status → open a second virtual connection to that
transport → negotiate on the `webrtc` namespace.

Because `0F5096E8` is a **built-in** receiver app, there is no Cast Developer Console
registration, no hosted receiver page, and no `$5` fee. This is the whole reason the
project is viable on classic hardware.

### 3. Media negotiation — OFFER / ANSWER
We send an `OFFER` describing the streams we can produce: codec, RTP payload type, SSRC,
time base, frame rate, resolutions, max bitrate, the AES key and IV mask, and the target
playout delay. The receiver replies with an `ANSWER` naming the UDP port it is listening
on and which of our offered stream indexes it accepted.

Field names are taken from Open Screen's `cast/streaming/message_fields.h`,
`public/offer_messages.cc` and `public/answer_messages.cc`, and pinned by tests against
the reference sample in `impl/offer_messages_unittest.cc`.

Two things worth knowing, both learned the hard way:

- **This half of the protocol numbers messages with `seqNum`, not `requestId`.** The two
  families share a TLS channel but not a numbering scheme, so they need separate
  correlation tables.
- **RTP payload types have a legacy quirk.** `rtp_defines.h` specifies 101 for H.264 and
  96 for Opus, but notes that some receivers demand 96 for video and 127 for audio
  regardless of codec, and that Chrome's sender *always* sends those. We default to
  matching Chrome, with `--legacy-payload-types` to test the spec values.

### 4. Media transport — Cast Streaming
A Cast-specific RTP profile, not stock RTP: its own header layout, its own RTCP feedback
messages, and AES-128-CTR payload encryption keyed by the `aesKey` / `aesIvMask` from the
offer. Open Screen implements this from scratch rather than reusing a WebRTC stack, and so
will we.

RTCP receiver reports drive bitrate adaptation and keyframe requests. Treat them as the
control loop for latency, not just for quality — see [latency.md](latency.md).

### Milestone 5 — system audio

WASAPI loopback capture, Opus encoding, and a second RTP stream alongside the video.

Negotiation yields **one** UDP port for the whole session, so audio and video share a
socket and are told apart by SSRC. That drove a refactor: `CastStreamingTransport` owns the
socket, serialises writes so one stream's frame cannot interleave with another's mid-burst,
and routes the receiver's feedback by the media SSRC it names. `CastRtpStream` holds
everything that is per-stream — frame numbering, encryption, the retransmission buffer and
Sender Reports.

Measured while mirroring with a tone playing, on the same Gen 2/3 puck:

| | |
| --- | --- |
| Opus frames | 1352 at 49.6/s against a 50/s target |
| Audio bitrate | 127 kbps against the 128 kbps negotiated |
| Audio checkpoint | advanced 1202 times over 1352 frames |
| Audio retransmits | 35 packets |

The audio checkpoint matters as much as the video one: each stream is acknowledged
separately, so only its own checkpoint proves the receiver is consuming it.

Three things this stage needed that were not obvious:

- **Loopback capture goes silent, not idle.** WASAPI delivers nothing at all while the
  system is playing nothing, which would stall the stream. Frames are produced on a fixed
  cadence regardless and padded with silence, because a gap in the cadence both clicks
  audibly and drifts the audio clock.
- **The device's mix format is whatever the user's hardware uses** — commonly 32-bit float,
  sometimes 44.1 kHz — while Opus wants 48 kHz, so everything runs through a resampler.
- **The audio clock counts samples, not elapsed time.** Deriving RTP timestamps from wall
  time would let rounding drift against the video clock. A/V sync then falls out of the
  two streams' Sender Reports, which map each RTP timeline onto a shared wall clock.

Unlike video, Opus packets carry no inter-frame prediction, so every audio frame is a key
frame referencing itself and a lost one costs 20 ms rather than stalling the stream.

## Solution layout

```
ProjectFiles/
  ClassicChromecastCaster.sln
  assets/                                  generated test media, not versioned
  src/
    RikRealization.ClassicCast.Protocol/   discovery, CASTV2, offer/answer, RTP
    RikRealization.ClassicCast.Media/      capture, encode, MirroringSession
    RikRealization.ClassicCast.App/        WinUI 3 shell
    RikRealization.ClassicCast.App/        WinUI 3 shell          (not started)
    RikRealization.ClassicCast.Spike/      console harness        (in progress)
  tests/
    RikRealization.ClassicCast.Protocol.Tests/
```

`Protocol` targets `net8.0` and stays free of Windows UI dependencies so it can be tested
headlessly. Only `Media` and `App` take a Windows TFM.

### Milestone 7 — the shell

![The app casting](screenshot-casting.png)

A WinUI 3 window keeping the structure of the tool it replaces — active source and target
on top, transport row, "From" list, "To" list — restyled around `#27adef` and the
segmented-disc mark. It discovers devices, enumerates displays, and casts.

`MirroringSession` was extracted so the application does not have to know the protocol.
The spike keeps driving the layers directly because its job is diagnosing them, and it
carries per-stage instrumentation the app has no use for.

Three things worth recording:

- **Windows App SDK 1.6 crashes on startup** here, unpackaged and self-contained, with
  `0xC0000374` (heap corruption). 1.7 works. Nothing in the code differed.
- **An unpackaged WinUI 3 app does not get DPI awareness for free.** Without an
  `app.manifest` declaring PerMonitorV2, Windows virtualises the window: wrong size and
  blurred on any scaled display.
- **The palette had to become theme-aware.** The first version hardcoded light-theme
  colours and was close to unreadable on a dark desktop. Brand blue is the one value that
  stays put, lifted slightly in dark mode.

A false alarm worth remembering too: the window looked badly broken in screenshots — content
wider than the frame, status bar missing — and the layout was fine all along. The capture
process was DPI-unaware while the app was not, so every coordinate was in a different
space. Verify the measuring instrument before fixing the thing being measured.

### Milestone 8 — source modes

**A single application** is captured by `WindowFrameSource`, which carries two mechanisms
because neither works everywhere. `PrintWindow` asks the window to draw itself, so it
captures even when covered, but a GPU-composited window — browsers, Electron apps — can
answer with a blank frame. Blitting the screen region always produces pixels but picks up
anything overlapping. The first frame decides which to use by checking whether PrintWindow
actually drew anything.

**Audio only** negotiates an offer with no video stream at all. The mirroring receiver
accepts it; the screen stays on the receiver's idle backdrop while sound plays.

**Extend Desktop is not implemented, and cannot be from here.** Presenting Windows with a
monitor that does not exist requires an indirect display driver — a signed kernel-mode
component, a separate project with its own signing story. It is listed in the interface
with that explanation rather than silently missing, because "why is this greyed out" is a
better question to answer than "why is this absent".

### Milestone 9 — signage detection

`SignageDetector` scores a window on how much it looks like something meant to be watched
rather than worked in, and the interface offers those first, badged, with the reasoning
visible. A source classified as signage gets `MirroringOptions.Signage`: a 400 ms jitter
buffer instead of 60 ms, 15 fps, and a longer key-frame interval. Latency stops mattering
for a board on a wall; smoothness starts to.

Weighting the signals took a correction that mirrors the one in milestone 6. The first
version leaned on the general signals — fills a screen, nobody typing into it, pixels
barely moving — and on a real desktop those describe an *idle* window just as well as a
dashboard. It labelled File Explorer, a music player and a chat window as signage, which
makes the feature worse than useless: it was meant to shorten the list, not mislabel it.

Now no combination of general signals reaches the threshold alone. Something has to
positively identify the window as presentational — a slideshow window class, or a
recognisable title. On the same desktop it then flagged exactly two: a Google Slides deck
and Docker Desktop's dashboard.

### Milestone 10 — HLS fallback

`HlsBroadcaster` encodes to HLS, serves it over a hand-rolled HTTP server, and
`HlsCastSession` points the Default Media Receiver at the playlist. Every Cast device ships
that receiver, so this works where mirroring negotiation does not — at the cost of seconds
of latency instead of tens of milliseconds. It is the thing that still works, not the thing
to reach for.

The HTTP server is a `TcpListener` rather than `HttpListener`, which on Windows needs an
administrator to reserve a URL prefix before binding to anything but localhost. Serving
four static files does not justify demanding elevation.

**Verified as far as it can be without a device**: the encoder, the HTTP server and the
playlist were exercised offline — 143 frames captured, playlist fetched over HTTP, a
156 KiB segment served. The final step, the Default Media Receiver accepting the playlist,
is untested: the Chromecast went off the network before it could be tried.

## Roadmap

1. **Spike — discovery + control.** Find devices, open the TLS channel, keep the heartbeat
   alive, launch `0F5096E8`, dump the receiver status. Proves the device will talk to a
   non-Chrome sender at all. ✅ *done*
2. **Offer/answer.** Negotiate a video stream and get a UDP port back. ✅ *done*
3. **First pixels.** Static test pattern → RTP → screen. The moment of truth. ✅ *done*
4. **Real capture + hardware encode.** Live desktop, Desktop Duplication into a hardware
   encoder. ✅ *done at 1080p30* — 1080p60 needs an in-process encoder, see above.
5. **Audio.** WASAPI loopback, Opus, A/V sync. ✅ *done*
6. **Latency pass.** Playout delay driven down and the RTCP loop closed. ✅ *mostly done*
   — an in-process encoder is still needed for 1080p60 and for key frames on demand.
7. **WinUI shell.** The branded interface. ✅ *done*
8. **Source modes.** Second display, single application, audio-only. ✅ *done* —
   extend-desktop is not buildable here, see below.
9. **Signage-window detection.** Classify presentation/dashboard windows so latency-tolerant
   sources can be offered with a smoothness-first profile. ✅ *done*
   Tracked in [#1](https://github.com/RikvdReijen/classic-chromecast-caster/issues/1).
10. **HLS fallback.** For devices that refuse Cast Streaming. ✅ *done, untested on hardware*

## Verified on hardware

Milestone 1 ran against a real Gen 2/3 puck ("Living Room TV", `md=Chromecast`,
proto `ve=05`) on 2026-08-22:

- mDNS discovery from a non-Chrome sender: **works**. Binding UDP 5353 means we also see
  every other responder on the network, so records are filtered by service suffix.
- CASTV2 over TLS 1.2/1.3 with the self-signed device certificate: **works**.
- `CONNECT` + heartbeat + `GET_STATUS`, with the reply parsed: **works**.
- The device reported `Chrome Mirroring (0F5096E8)` already running, with a live
  `transportId`. Notably the sender at the time was **AirParrot**, not Chrome — good
  evidence that AirParrot reaches classic pucks through this same built-in mirroring
  receiver rather than a custom one.

### Milestone 2 — OFFER / ANSWER

Negotiated against the same device from a cold start (the receiver was idle on Backdrop):

- `LAUNCH` of `0F5096E8`: **works**. The app advertised `webrtc`, `media`, `debug` and
  `remoting`.
- `OFFER` with H.264 + VP8 + Opus, `castMode` mirroring: **accepted**.
- `ANSWER`: `udpPort` 10008, `sendIndexes` `[2, 0]`, matching `ssrcs`.

What the device told us:

| Observation | Consequence |
| --- | --- |
| Chose **H.264** over the VP8 alternative | The hardware-encode path is the live one. VP8 stays as fallback only. |
| Accepted `targetDelay` of **120 ms** without complaint | The 400 ms default really is a conservative choice, not a hardware floor. This is the latency win. |
| Accepted the Chrome-compatible payload types (video 96, audio 127) | No need to fall back to spec values on this hardware. |
| Returned streams **audio first**, not in offered order | `sendIndexes` order is the receiver's; `ssrcs` is positional against it. Never assume offer order. |
| Its SSRC is consistently **our SSRC + 1** | The Cast convention for the reverse direction. |
| Omitted `constraints` and `display` entirely | Both are optional. Absent must mean "unknown", never "unconstrained". |

### Milestone 3 — first pixels

A 1280x720 H.264 test pattern encrypted, packetised and streamed to the negotiated UDP
port. The receiver's own feedback is the evidence:

| | First attempt | After the fixes |
| --- | ---: | ---: |
| Checkpoint advances | 2 | **936** |
| NACKed packets | 13,266 | 111 |
| Whole frames NACKed | 5,526 | 24 |
| RTCP datagrams | 9,743 | 1,245 |

A checkpoint that advances on nearly every frame means the receiver completed and accepted
those frames, which it only does when it is decoding them.

Three things had to be right, and none were obvious:

1. **Frame ids must start at 0.** A receiver's checkpoint begins at `FrameId(-1)`, which
   appears as 255 on the wire. Starting at 1 left it demanding a frame 0 that never came;
   it only recovered 256 frames later when the 8-bit id wrapped around to 0.
2. **NACKs must actually be answered.** Cast has no forward error correction. One lost
   packet stalls the stream permanently: the frame never completes, every later frame
   references it, and the receiver NACKs forever. Recent frames' packets are retained in
   a 256-slot ring — the same width as the wire frame id, so the buffer covers exactly
   the range a NACK can name.
3. **The sender must not outrun the receiver.** With an 8-bit frame id the receiver's
   window is bounded; sending 1,100 frames while its checkpoint sits at 2 means
   everything is discarded. Frames are now held back when we get too far ahead.

Sender Reports matter too: the receiver has no wall-clock timeline without one, so frames
can arrive perfectly and still never be scheduled for display. One goes out before the
first frame and every 250 ms after.

Still to do: pacing packet bursts within a frame, and driving bitrate from the feedback.

### Milestone 4 — live desktop

Screen capture feeding a hardware encoder, replacing the canned file. Measured on the
primary 1920x1080 display, casting to the same Gen 2/3 puck:

| Stage | Cost per frame |
| --- | ---: |
| Capture (GDI BitBlt into a DIB section) | **31.3 ms** |
| Feeding the encoder (BGRA over a pipe) | 3.4 ms |
| Achieved throughput | **27.0 fps at 1080p** |

Then Desktop Duplication replaced GDI, and the bottleneck moved:

| Capture method | Capture | Encode feed | Throughput |
| --- | ---: | ---: | ---: |
| GDI, `GetDIBits` | 40.3 ms | 6.3 ms | 20.8 fps |
| GDI, DIB section | 31.3 ms | 3.4 ms | 27.0 fps |
| **Desktop Duplication** | **10.9 ms** | 8.1 ms | 27.8 fps |
| Desktop Duplication @ 60 fps target | 11.8 ms | **25.6 ms** | 25.9 fps |

Two things this settled:

1. **Capture is solved.** Keeping the frame on the GPU took capture from 31.3 ms to
   10.9 ms, a threefold improvement, and it is no longer the limiting stage.
2. **The raw-frame pipe is now the wall.** Asking for 60 fps pushes 1080p BGRA at roughly
   500 MB/s through an anonymous pipe, and the write alone takes 25.6 ms per frame — about
   324 MB/s in practice. Throughput at a 60 fps target is *worse* than at 30, because the
   pipe cannot keep up and the loop spends its time blocked in a write.

So **1080p30 is comfortable and 1080p60 is not reachable through a subprocess encoder.**
Getting there means encoding in-process — Media Foundation or NVENC directly — so the
captured surface never leaves the GPU and no raw frame is ever serialised. That also
brings on-demand key frames, which an ffmpeg subprocess cannot offer.

Desktop Duplication is more fragile than GDI — it is lost on a resolution change, a UAC
prompt, or a full-screen exclusive application — so it rebuilds itself on
`DXGI_ERROR_ACCESS_LOST`, and GDI remains as an always-available fallback behind `--gdi`.

Encoder selection had a trap worth recording: **being listed by `ffmpeg -encoders` does not
mean a codec works.** NVENC is present in the build here and fails on every preset —
`Cannot get the preset configuration: unsupported param` — because the installed NVIDIA
driver is newer than the API this ffmpeg was built against. It fails late enough that
watching for an early process exit misses it, so each candidate is now proved with a
throwaway encode of synthetic frames before being chosen. QuickSync is the working
hardware path on this machine.

The receiver stayed healthy throughout: 933 checkpoint advances over 1046 frames, 199
packet NACKs, no picture loss, and it again reported 120 ms playout delay.
