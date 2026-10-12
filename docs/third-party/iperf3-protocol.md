# Managed iperf3 TCP/UDP implementation

`Iperf3Client.cs`, `Iperf3DataPump.cs` and `Iperf3Options.cs` implement TCP control
and TCP/UDP data streams directly using .NET sockets. No external executable or
native library is shipped or launched.

Pinned upstream: [ESnet iperf 3.17.1](https://github.com/esnet/iperf/tree/3.17.1),
BSD-style license. The complete upstream [LICENSE](iperf3-LICENSE.txt) is retained
and embedded in the single EXE. Copyright (c) 2014–2024, The Regents of the
University of California, through Lawrence Berkeley National Laboratory.

The following protocol and measurement logic is adapted to C#:

- [iperf_api.h](https://github.com/esnet/iperf/blob/3.17.1/src/iperf_api.h): control states.
- [iperf_api.c](https://github.com/esnet/iperf/blob/3.17.1/src/iperf_api.c):
  parameter/result JSON, network-order framing, interval byte deltas and
  monotonic durations, application pacing based on sent bits / elapsed time.
- [iperf_tcp.c](https://github.com/esnet/iperf/blob/3.17.1/src/iperf_tcp.c):
  37-byte cookie validation and counting data only during TEST_RUNNING.
- [iperf_udp.c](https://github.com/esnet/iperf/blob/3.17.1/src/iperf_udp.c) and
  [iperf.h](https://github.com/esnet/iperf/blob/3.17.1/src/iperf.h): UDP connect/reply
  datagrams including legacy peers, big-endian timestamps and 32/64-bit packet
  sequence headers, sequence-gap loss, reordered-gap repair and RFC 1889 jitter
  smoothing. Like upstream, duplicate packets can affect the loss estimate.
- [iperf_client_api.c](https://github.com/esnet/iperf/blob/3.17.1/src/iperf_client_api.c)
  and [iperf_server_api.c](https://github.com/esnet/iperf/blob/3.17.1/src/iperf_server_api.c):
  test lifecycle and result exchange. Client statistics finalize before TEST_END;
  reverse streams keep draining but exclude late bytes and control-response time.

POSIX threads/timers and cJSON are replaced with .NET tasks, cancellation,
PeriodicTimer/Stopwatch and System.Text.Json. Statistics continue during idle
socket I/O and include final partial intervals. Unlimited UDP sends yield in
batches so timers and cancellation remain responsive. This is a managed port
of the supported logic, not the full upstream program. The retained upstream
LICENSE includes notices for upstream components such as cJSON not used here.

## Supported options

TCP and UDP, one stream (`-P 1`), no warmup (`-O 0`), 1–3600 seconds (`-t`),
forward/reverse (`-R`), IPv4/IPv6, client and persistent server, two-end byte/time
statistics, UDP loss/jitter, 32/64-bit counters, bounded JSON and cancellation.

Size (`-l`): TCP blocks 1 byte–1 MiB; UDP datagrams 16–65507 bytes with 64-bit
counters (12 minimum for incoming 32-bit clients). UDP size includes the iperf
header, excluding UDP/IP/link headers. TCP blocks do not set wire packet size.
UDP above path MTU may fragment. The client defaults to 1200-byte UDP datagrams.

Pacing (`-b`): 1 bit/s–100 Gbit/s, or 0 unlimited. UDP packets/s in the UI converts
to bandwidth using size × 8. Reverse tests negotiate the same rate with the
remote sender. Windows scheduling and buffering can cause short bursts; this
is average application throughput, not a hardware line-rate guarantee. The UI
defaults to 20 Mbit/s for 10 seconds. Missing upstream out-of-order result fields
are shown as unavailable, not zero.

Standard server: `iperf3 -s -p 5201`. For a standard client against the built-in
server, the supported command subset is:

```sh
iperf3 -c HOST -p 5201 -P 1 -O 0 -t 10 -l 131072 -b 20M
iperf3 -c HOST -p 5201 -P 1 -O 0 -u -t 10 -l 1200 -b 20M
iperf3 -c HOST -p 5201 -P 1 -O 0 -u -t 10 -l 1200 -b 20M -R --udp-counters-64bit
```

UDP still needs TCP on the same port for control. This is iperf3, not iperf2.
Not implemented: SCTP, simultaneous bidirectional mode, parallel streams,
byte/block termination, warmup omission, burst grouping, kernel pacing/socket
buffer/MSS tuning, authentication, CPU-use/retransmission measurement.
Unsupported transfer options are rejected. Required CPU fields are sent as zero;
the application does not present these as measurements.

## Verification

Automated tests cover independent wire fixtures, native forward/reverse,
malformed frames, server recovery, idle/final intervals, delayed reverse-result
accounting, TCP/UDP pacing, malformed datagrams, loss/reordering/jitter,
32/64-bit headers, IPv6 UDP, unlimited-rate timer responsiveness and cancellation.
UI tests verify packets/s conversion and rendering at minimum window dimensions.

### Public peer verification (2026-10-11)

The managed client completed TCP forward/reverse and UDP forward/reverse tests
against each public standard iperf3 server below: 2 seconds at 2 Mbit/s per case,
TCP blocks 8192 bytes and UDP payloads 1200 bytes.

| Provider | Verified endpoint | Published port range |
| --- | --- | --- |
| Scaleway, France | `ping.online.net:5202` | 5200–5209 |
| Moji, France | `iperf3.moji.fr:5203` | 5200–5240 |
| Uztelecom, Uzbekistan | `speedtest.uztelecom.uz:5203` | 5200–5209 |
| Hurricane Electric, USA | `iperf.he.net:5201` | 5201 |

Source: https://iperf.fr/iperf-servers.php. Presets use the tested port and show
the source, date and alternate port range. Opening/selecting a preset generates
no traffic. Current availability is not guaranteed. Serverius port 5002 passed
TCP but UDP timed out from this network, so it is excluded from presets.

The built-in server is covered by independent fixtures and managed loopback;
a stock CLI client against it has not been exercised here. Public tests do not
establish all-version compatibility. An opt-in interoperability check generates
traffic only when explicitly invoked:

```sh
dotnet run --project tests/EmbedDesk.App.SmokeTests -- --iperf3-peer-check HOST PORT
```
