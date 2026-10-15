# ProxyNET

A lightweight TCP forwarding proxy. ProxyNET listens on one or more local endpoints and relays every accepted
connection to a configured target, which makes it useful for debugging, monitoring or reaching a service through a
fixed local address.

![Proxy diagram](https://github.com/thalysmarciobn/ProxyNET/blob/main/diag.png?raw=true "Diagram")

## Features

- **Multiple listeners**: every entry of the `Forwarding` array becomes an independent listener with its own target,
  limits and timeouts.
- **Fully asynchronous**: connections are accepted and relayed without blocking threads, and both directions of a
  session are forwarded concurrently.
- **Correct half-close handling**: when one side stops sending, the end of stream is propagated to the other side
  while the opposite direction keeps draining, so responses are never truncated.
- **Memory that follows the traffic**: a session waits for data with a zero byte receive and only then rents a
  pooled buffer, so idle connections hold no buffer at all.
- **No timer per transfer**: the no progress timeout is enforced by one sweep per listener over a timestamp each
  session publishes, instead of arming a deadline around every read and write.
- **Backpressure**: `MaxConnections` closes connections beyond the limit right after accepting them, and accept
  failures caused by exhausted socket resources back off instead of spinning.
- **Host names**: a target can be an IP address or a host name, resolved per connection so DNS changes and
  round-robin records are honoured.
- **Graceful shutdown**: `Ctrl+C` and `SIGTERM` stop the listeners, wait for the running sessions and flush the log.
- **Observability**: per listener counters are logged periodically and published through
  `System.Diagnostics.Metrics`, with per connection events kept at debug level so logging never becomes the
  bottleneck.

## Requirements

- .NET SDK 9.0 or newer

## Getting started

```bash
git clone https://github.com/thalysmarciobn/ProxyNET.git
cd ProxyNET
dotnet run
```

The proxy reads `appsettings.json` from the directory of the executable, so it can be started from anywhere.

## Configuration

```json
{
  "LogLevel": "Information",
  "StatsInterval": 60,
  "Forwarding": [
    {
      "Address": "127.0.0.1",
      "Port": 25500,
      "ConnectTimeout": 10000,
      "IdleTimeout": 300000,
      "WriteTimeout": 0,
      "BufferSize": 65536,
      "MaxConnections": 0,
      "WaitForData": true,
      "NoDelay": true,
      "Forward": {
        "Address": "127.0.0.1",
        "Port": 8090
      }
    }
  ]
}
```

| Key                                | Required | Default       | Description                                                                                                       |
|------------------------------------|----------|---------------|-------------------------------------------------------------------------------------------------------------------|
| `LogLevel`                         | no       | `Information` | Minimum Serilog level: `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`.                           |
| `StatsInterval`                    | no       | `60`          | Seconds between the counter summaries of each listener. `0` turns the summaries off.                               |
| `Forwarding[].Address`             | yes      | —             | Local address to bind. Accepts an IP literal, `*`, `+`, `any` (all interfaces) or `localhost`.                     |
| `Forwarding[].Port`                | yes      | —             | Local port to bind. `0` lets the operating system pick a free port.                                                |
| `Forwarding[].ConnectTimeout`      | no       | `10000`       | Milliseconds allowed to establish the connection to the target. `0` disables the timeout.                          |
| `Forwarding[].IdleTimeout`         | no       | `0`           | Milliseconds a session may make no progress before it is aborted, stalled writes included. `0` disables it.        |
| `Forwarding[].ReadTimeout`         | no       | `0`           | Deprecated name of `IdleTimeout`, honoured when `IdleTimeout` is not set.                                          |
| `Forwarding[].WriteTimeout`        | no       | `0`           | Milliseconds a single write may take. Costs a timer per chunk, prefer `IdleTimeout`. `0` disables it.               |
| `Forwarding[].BufferSize`          | no       | `65536`       | Bytes rented while a transfer is in flight, between `1024` and `1048576`.                                          |
| `Forwarding[].MaxConnections`      | no       | `0`           | Maximum concurrent sessions. `0` accepts as many as the operating system allows.                                   |
| `Forwarding[].WaitForData`         | no       | `true`        | Wait for data with a zero byte receive before renting a buffer. See the tuning notes below.                        |
| `Forwarding[].NoDelay`             | no       | `true`        | Disable Nagle on both sockets of a session.                                                                        |
| `Forwarding[].ReceiveBufferSize`   | no       | `0`           | `SO_RCVBUF` of both sockets. `0` keeps the operating system value, and on Linux its auto tuning.                   |
| `Forwarding[].SendBufferSize`      | no       | `0`           | `SO_SNDBUF` of both sockets. `0` keeps the operating system value, and on Linux its auto tuning.                   |
| `Forwarding[].Forward.Address`     | yes      | —             | Target IP address or host name.                                                                                    |
| `Forwarding[].Forward.Port`        | yes      | —             | Target port.                                                                                                       |

Every value can also be supplied through environment variables prefixed with `PROXYNET_`, for example
`PROXYNET_LogLevel=Debug` or `PROXYNET_Forwarding__0__Port=26000`.

The configuration is validated at startup. Invalid entries, duplicated listen endpoints and a missing configuration
file are reported and the process exits with code `1`; the same happens when none of the configured listeners could
be bound.

## Observability

Each listener logs a summary every `StatsInterval` seconds:

```
127.0.0.1:25500: 48213 active, 51044 accepted (+812), in 3.61 GB/s, out 1.12 GB/s, 0 rejected, 3 unreachable, 11 idle aborts
```

The same counters are published through a `System.Diagnostics.Metrics` meter named `ProxyNET`, tagged by listener
(`proxynet.sessions.active`, `proxynet.sessions.accepted`, `proxynet.sessions.rejected`,
`proxynet.sessions.failed`, `proxynet.sessions.idle_timeouts`, `proxynet.bytes.to_target`,
`proxynet.bytes.to_peer`). Observing them costs nothing while nobody listens:

```bash
dotnet-counters monitor --process-id <pid> --counters ProxyNET
```

Per connection events stay at debug level on purpose. Turning `LogLevel` to `Debug` logs every accepted connection
and every forwarded chunk, which is useful while diagnosing a single session and far too expensive under load.

## Running at scale

### Memory

A session holds two sockets, one state machine per direction and, while `WaitForData` is enabled, a pooled buffer
only for as long as a transfer is actually in flight:

```
memory ~= sessions * ~6 KB + concurrently active transfers * 2 * BufferSize
```

Measured with 2000 idle sessions held open, comparing a build that keeps a buffer per direction for the lifetime of
the session against the current one:

| Build                                               | Working set delta | Per session |
|-----------------------------------------------------|-------------------|-------------|
| Buffer held for the whole session, 16 KB             | +88.8 MB          | ~45 KB      |
| Buffer rented per transfer, 16 KB                    | +14.6 MB          | ~7.5 KB     |
| Buffer rented per transfer, 64 KB (current defaults) | +10.8 MB          | ~5.5 KB     |

Extrapolated to 50000 mostly idle sessions that is roughly 275 MB instead of 2.2 GB, and the buffer size no longer
appears in that figure at all. `MaxConnections` bounds the worst case, and `BufferSize` bounds what a burst of
simultaneously active transfers can add on top.

### Throughput

Forwarding the same payload through the proxy on a single machine, where the load generator and the target compete
for the same cores, the cost per forwarded gigabyte was:

| Configuration                              | CPU per GB |
|--------------------------------------------|------------|
| Buffer per session, 16 KB                   | 5.7 s      |
| Buffer per transfer, 16 KB, `WaitForData`   | 6.4 s      |
| Buffer per transfer, 16 KB, no wait         | 5.5 s      |
| Buffer per transfer, 64 KB, `WaitForData`   | 4.6 s      |

The zero byte receive costs one extra system call per chunk, which a 64 KB buffer more than pays back. Keep the
defaults for many connections; set `WaitForData` to `false` only when the deployment has few but permanently
saturated sessions, where holding a buffer per session is cheaper than the extra receive.

### Operating system limits

These limits bite long before the proxy does:

- **File descriptors**: every session uses two. Give systemd `LimitNOFILE=200000` or raise `ulimit -n` for 50000
  sessions, otherwise accepting fails with `TooManyOpenSockets` and the proxy backs off.
- **Ephemeral ports**: every session also consumes one ephemeral port on the outbound side. One source address can
  only hold about 28000 connections towards the same target on Linux and about 16000 on Windows. Widen the range
  (`net.ipv4.ip_local_port_range`, `MaxUserPort`) and spread the load over several target addresses or ports.
- **Sockets in `TIME_WAIT`**: high churn fills the ephemeral range with closed connections for the duration of the
  timeout, and a reused four tuple is answered with a reset. Lower `TcpTimedWaitDelay` on Windows or keep
  `net.ipv4.tcp_tw_reuse` enabled with timestamps on Linux.
- **Accept queue**: `net.core.somaxconn` (Linux) caps the backlog the listener asks for.
- **Socket buffers**: on high bandwidth-delay links the window, not the proxy, limits a single session. Prefer the
  auto tuning of the operating system (`net.ipv4.tcp_rmem`, `net.ipv4.tcp_wmem`) and only set `ReceiveBufferSize`
  and `SendBufferSize` when it has to be overridden, since a fixed value disables the auto tuning on Linux.
- **Nonpaged pool** on Windows is exhausted by tens of thousands of sockets per process and surfaces as
  `NoBufferSpaceAvailable`.

### Runtime

The project enables the server garbage collector, which keeps up with the allocation rate of many concurrent
sessions. Override it per deployment with `DOTNET_gcServer=0` when memory matters more than throughput. On Linux,
`DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT` controls how many engine threads poll the sockets; raising it helps when a
single engine thread saturates a core.

## Running as a service

Because `SIGTERM` is handled, the published binary can be supervised directly, for example with systemd:

```ini
[Service]
ExecStart=/opt/proxynet/ProxyNET
Restart=always
LimitNOFILE=200000
```

## Tests

```bash
dotnet test
```

The suite covers configuration validation and endpoint parsing, and runs the proxy against a local target to assert
round trips, payloads larger than the session buffer, half-closed connections, the session limit, the no progress
timeout, unreachable targets, the counters and shutdown behaviour.

## Continuous integration

The GitHub Actions workflow builds and tests the solution, publishes self-contained binaries for `linux-x64` and
`win-x64`, and attaches both archives to a tagged release.

## Technologies

- **C#** and **.NET 9**
- **Serilog** for structured logging
- **Microsoft.Extensions.Configuration** for JSON and environment configuration
- **System.Diagnostics.Metrics** for counters
- **xUnit** for the test suite
