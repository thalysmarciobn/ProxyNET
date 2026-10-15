# ProxyNET

A lightweight TCP forwarding proxy. ProxyNET listens on one or more local endpoints and relays every accepted
connection to a configured target, which makes it useful for debugging, monitoring or reaching a service through a
fixed local address.

![Proxy diagram](https://github.com/thalysmarciobn/ProxyNET/blob/main/diag.png?raw=true "Diagram")

## Features

- **Multiple listeners**: every entry of the `Forwarding` array becomes an independent listener with its own target
  and timeouts.
- **Fully asynchronous**: connections are accepted and relayed without blocking threads, and both directions of a
  session are forwarded concurrently.
- **Correct half-close handling**: when one side stops sending, the end of stream is propagated to the other side
  while the opposite direction keeps draining, so responses are never truncated.
- **Enforced timeouts**: connect, idle and write timeouts are enforced through cancellation, which is the only way
  they apply to asynchronous socket operations.
- **Host names**: a target can be an IP address or a host name, resolved per connection so DNS changes and
  round-robin records are honoured.
- **Pooled buffers**: each direction rents its buffer from `ArrayPool<byte>`, so a steady stream of connections does
  not allocate per transfer.
- **Graceful shutdown**: `Ctrl+C` and `SIGTERM` stop the listeners, wait for the running sessions and flush the log.
- **Structured logging**: Serilog writes structured events to the console at a configurable level.

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
  "Forwarding": [
    {
      "Address": "127.0.0.1",
      "Port": 25500,
      "ConnectTimeout": 10000,
      "ReadTimeout": 0,
      "WriteTimeout": 15000,
      "BufferSize": 16384,
      "Forward": {
        "Address": "127.0.0.1",
        "Port": 8090
      }
    }
  ]
}
```

| Key                      | Required | Default       | Description                                                                                             |
|--------------------------|----------|---------------|---------------------------------------------------------------------------------------------------------|
| `LogLevel`               | no       | `Information` | Minimum Serilog level: `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`.                 |
| `Forwarding[].Address`   | yes      | —             | Local address to bind. Accepts an IP literal, `*`, `+`, `any` (all interfaces) or `localhost`.           |
| `Forwarding[].Port`      | yes      | —             | Local port to bind. `0` lets the operating system pick a free port.                                      |
| `Forwarding[].ConnectTimeout` | no  | `10000`       | Milliseconds allowed to establish the connection to the target. `0` disables the timeout.                |
| `Forwarding[].ReadTimeout`    | no  | `0`           | Milliseconds a direction may stay idle before the session is closed. `0` keeps idle connections open.    |
| `Forwarding[].WriteTimeout`   | no  | `0`           | Milliseconds a single write may take before the session is closed. `0` waits indefinitely.               |
| `Forwarding[].BufferSize`     | no  | `16384`       | Bytes rented per direction, between `1024` and `1048576`. Larger buffers trade memory for fewer syscalls.|
| `Forwarding[].Forward.Address`| yes | —             | Target IP address or host name.                                                                          |
| `Forwarding[].Forward.Port`   | yes | —             | Target port.                                                                                             |

Every value can also be supplied through environment variables prefixed with `PROXYNET_`, for example
`PROXYNET_LogLevel=Debug` or `PROXYNET_Forwarding__0__Port=26000`.

The configuration is validated at startup. Invalid entries, duplicated listen endpoints and a missing configuration
file are reported and the process exits with code `1`; the same happens when none of the configured listeners could
be bound.

## Running as a service

Because `SIGTERM` is handled, the published binary can be supervised directly, for example with systemd:

```ini
[Service]
ExecStart=/opt/proxynet/ProxyNET
Restart=always
```

## Tests

```bash
dotnet test
```

The suite covers configuration validation and endpoint parsing, and runs the proxy against a local target to assert
round trips, payloads larger than the session buffer, half-closed connections, idle timeouts, unreachable targets
and shutdown behaviour.

## Continuous integration

The GitHub Actions workflow builds and tests the solution, publishes self-contained binaries for `linux-x64` and
`win-x64`, and attaches both archives to a tagged release.

## Technologies

- **C#** and **.NET 9**
- **Serilog** for structured logging
- **Microsoft.Extensions.Configuration** for JSON and environment configuration
- **xUnit** for the test suite
