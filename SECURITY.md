# Security

## Reporting a vulnerability

Please report privately through GitHub's
[security advisory form](https://github.com/RikvdReijen/classic-chromecast-caster/security/advisories/new)
rather than opening a public issue. This is a spare-time project, so expect a reply in
days rather than hours.

## What this software does on your network

Two things that look like weaknesses are properties of the Cast protocol rather than
oversights, and are worth stating plainly so nobody has to read the source to find them.

**Device certificates are not verified.** Chromecasts present self-signed certificates and
the protocol provides no way to establish a chain of trust to them. The control channel is
encrypted with TLS, but the identity of the device on the other end is not checked, so an
attacker who is already positioned on your network could impersonate a Chromecast. Every
Cast sender, Chrome included, is in this position.

**The HLS fallback is unauthenticated.** That path opens an HTTP server on your LAN
address, on a random port, serving live segments of whatever you are casting, with no
access control — the Default Media Receiver cannot present credentials. Anyone on the same
network who finds the port can watch. It is a fallback for devices that refuse to mirror;
the default mirroring path sends encrypted RTP to the single device you selected and opens
no server.

Both are limited to the local network. Neither is reachable from the internet unless you
have deliberately forwarded a port.

## What it does not do

No telemetry, no analytics, no crash reporting, no update check. Nothing is written to
disk beyond temporary HLS segments, which are deleted when the session ends. The only
outbound connections are to the Cast device you select.
