# HelloClient

Minimal console sample that captures a message through `GlitchPort.Core`. The
end-to-end check runs it against a local collector.

## Usage

```bash
dotnet run --project sdk/dotnet/samples/HelloClient -- [message]
```

- `message` defaults to `hello`.
- The DSN is read from `GLITCHPORT_DSN` and falls back to
  `http://dev-public-key@localhost:5080/1`.

On success the process prints `event_id: <guid>` to stdout, flushes the
relay, and exits with `0`. When the flush reports a delivery failure it prints
a single `error: relay <name> failed: ...` line to stderr and exits with `1`;
any other failure prints a single `error: <message>` line and also exits with
`1`. Ctrl+C cancels the capture.
