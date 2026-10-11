# Native iperf3 TCP protocol implementation

`src/EmbeddedDebugger.Infrastructure/Networking/Iperf3Client.cs` implements the
iperf3 TCP wire protocol directly using .NET sockets. It does not ship, launch,
or require an iperf executable, and does not silently substitute raw TCP traffic.

Pinned upstream: [ESnet iperf 3.17.1](https://github.com/esnet/iperf/tree/3.17.1),
BSD-style license. The complete upstream [LICENSE](iperf3-LICENSE.txt) is retained
and embedded in the single EXE. Copyright (c) 2014–2024, The Regents of the
University of California, through Lawrence Berkeley National Laboratory.

The following protocol and measurement logic is adapted to C#/.NET sockets:

- [iperf_api.h](https://github.com/esnet/iperf/blob/3.17.1/src/iperf_api.h): control states.
- [iperf_api.c](https://github.com/esnet/iperf/blob/3.17.1/src/iperf_api.c):
  parameter/result JSON, network-order framing, `iperf_stats_callback` interval
  byte deltas and monotonic durations. The managed timer runs independently of
  socket I/O, so stalled streams still report zero-rate intervals; final partial
  intervals are included.
- [iperf_tcp.c](https://github.com/esnet/iperf/blob/3.17.1/src/iperf_tcp.c):
  37-byte cookie validation and `iperf_tcp_recv` accounting only in TEST_RUNNING.
- [iperf_client_api.c](https://github.com/esnet/iperf/blob/3.17.1/src/iperf_client_api.c)
  and [iperf_server_api.c](https://github.com/esnet/iperf/blob/3.17.1/src/iperf_server_api.c):
  test lifecycle and result exchange. Client statistics are finalized before
  TEST_END; reverse streams keep draining until result exchange but late bytes
  and control-response latency are excluded from the completed measurement.

This is a managed port of the supported logic, not the full upstream program.
POSIX threads/timers and cJSON are replaced with .NET tasks, cancellation,
PeriodicTimer/Stopwatch and System.Text.Json. No native library or executable is
bundled. The retained upstream LICENSE also includes notices for upstream
components such as cJSON; those components are not used here.

Implemented subset: TCP, one stream, timed unlimited-rate transfer, forward and
reverse, local/peer byte and elapsed-time statistics, interval throughput,
IPv4/IPv6 addresses, bounded control JSON, cancellation and timeout, persistent
server with one test at a time. Standard peer invocation is `iperf3 -s` or
`iperf3 -c HOST -P 1 -O 0 -t SECONDS [-R]`.

Not implemented: UDP/SCTP, simultaneous bidirectional mode, parallel streams,
byte/block-count termination, warmup omission, rate/pacing and socket/MSS tuning,
authentication, CPU-use/retransmission measurement. Unsupported transfer options
are rejected instead of reporting a misleading measurement. Required wire-level
CPU fields are sent as zero; the application does not present them as measurements.

Validation: independent wire fixture, native loopback forward and reverse,
parameter and frame-length rejection, cancellation and server recovery tests,
idle intervals, final partial intervals and delayed reverse-result accounting.
Standard executable interoperability has not been completed in this environment;
there is no claim that those external binary tests passed.
