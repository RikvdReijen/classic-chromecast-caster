#!/usr/bin/env bash
# Generates the H.264 elementary stream used by the spike's --send-test-pattern mode.
#
# Constraints that matter for Cast Streaming:
#   - Annex-B, with SPS/PPS repeated on every IDR. The Cast RTP profile carries no
#     out-of-band codec config, so a receiver joining mid-stream needs in-band headers.
#   - No B-frames. They reorder output and add latency, and the Cast frame model assumes
#     each frame references an earlier one.
#   - Main profile, level 4.0, matching what the OFFER advertises.
#   - Capped well under the bitrate the OFFER advertises. testsrc2 is high-detail noise
#     and will happily blow past 5 Mbps unconstrained.
set -euo pipefail
cd "$(dirname "$0")/.."

OUT=ProjectFiles/assets/testpattern-1280x720.h264

ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=2" \
  -c:v libx264 -profile:v main -level 4.0 -preset veryfast -tune zerolatency \
  -pix_fmt yuv420p -bf 0 -g 15   -b:v 2M -maxrate 2500k -bufsize 1M \
  -x264-params "keyint=15:min-keyint=15:scenecut=0:repeat-headers=1" \
  -f h264 "$OUT"

echo "wrote $OUT ($(du -h "$OUT" | cut -f1))"
