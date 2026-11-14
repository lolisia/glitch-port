# HelloClient

Minimal console sample that captures messages through `GlitchPort.Core`. The
end-to-end check runs it against a local collector, and it doubles as a manual
playground for the file and terminal relays.

The build also produces `glitchport-terminal` next to `HelloClient.exe`, so a
`terminal:` relay works without any extra setup.

## Usage

```bash
dotnet run --project sdk/dotnet/samples/HelloClient -- [message] [options]
```

Arguments:

- `message` — the text to capture; defaults to `hello`.
- `--level <debug|info|warning|error|fatal>` — event severity, case-insensitive;
  defaults to `info`.
- `--count <n>` — number of events to capture; defaults to `1`, must be `>= 1`.
- `--hold <seconds>` — keep the process alive after the flush, for example to
  read a terminal relay window; defaults to `0`, must be `>= 0`. Ctrl+C stops
  the wait.

## Configuration

- `GLITCHPORT_DSN` — semicolon separated relay configuration read through
  `GlitchPortOptions.FromEnvironment()`. Every relay is used, for example:
  `terminal:?window=inherit&format=compact;file:?dir=logs&format=ndjson;http://key@localhost:5080/1`
- When the variable is missing or blank the sample uses the local collector at
  `http://dev-public-key@localhost:5080/1`.

## Output

For every captured event the sample prints `event_id: <guid>` to stdout. After
the flush it prints:

- `terminal_pid: <pid>` for each terminal relay that started its renderer;
- one `relay <name> (<type>): delivered=<n> failed=<n> dropped=<n>` line per
  relay.

The process exits with `0` only when every relay reports success. Any failure
(including invalid arguments) prints a single `error: ...` line to stderr and
exits with `1`. Ctrl+C cancels capture and the optional hold.
