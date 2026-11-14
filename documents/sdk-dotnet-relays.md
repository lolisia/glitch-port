# .NET SDK relays

A **relay** is a destination for the events captured by the glitch-port .NET SDK.
Every `GlitchPortClient` owns one or more independent relays and copies each
captured event to every relay whose `MinLevel` accepts the event's level. A slow
or failing relay never blocks the game: capture only enqueues the event on a
bounded queue and returns, while delivery happens on the relay's own background
worker.

Relays are useful beyond production reporting:

- an **HTTP relay** forwards events to a glitch-port collector,
- a **terminal relay** renders events in a local console while you play or test,
- a **file relay** writes rotating log files for headless runs, CI jobs and
  crash triage.

The terminal and file relays are local: they need no collector, no network and
no Docker.

## Relay types

| Type       | Destination                                          | Typical use                                      | Network | Writes files |
| ---------- | ---------------------------------------------------- | ------------------------------------------------ | ------- | ------------ |
| `http`     | A glitch-port Relay via `POST /api/{projectId}/messages` | Production crash and error reporting         | Yes     | No           |
| `terminal` | `glitchport-terminal`, a child console renderer      | Interactive development and playtests            | No      | No           |
| `file`     | Rotating NDJSON or text log files                    | Headless runs, CI, QA machines, crash triage     | No      | Yes          |

All three types share the same settings: `Name`, `Enabled`, `MinLevel` and
`QueueCapacity`. The type is fixed when the relay definition is created.

## Configuring relays in code

Add relay definitions to `GlitchPortOptions.Relays`. The definitions are created
with the `Relay` factory:

- `Relay.Http(dsn, configure)` for an HTTP relay,
- `Relay.Terminal(configure)` for the local console renderer,
- `Relay.File(configure)` for local log files.

```csharp
var options = new GlitchPortOptions();

options.Relays.Add(Relay.Http("http://key@host:5080/1"));
options.Relays.Add(Relay.Terminal(o => o.Format = TerminalFormat.Compact));
options.Relays.Add(Relay.File(o => o.Directory = "logs/glitchport"));
```

The order of `Relays` is the fan-out order and the order of the results in a
`DeliveryReport`. A definition with `Enabled = false` is ignored. A client needs
at least one enabled relay; otherwise the constructor throws
`ArgumentException`.

```csharp
using GlitchPort;

var options = new GlitchPortOptions();

// Legacy single DSN: an implicit http relay named "http".
options.Dsn = Dsn.Parse("http://public-key@relay.example.com:5080/1");

// Explicit relays are created after the implicit Dsn relay, in list order.
options.Relays.Add(Relay.Http("http://public-key@relay.example.com:5080/1", http =>
{
    http.Name = "main";
    http.MinLevel = Level.Warning;
}));

options.Relays.Add(Relay.Terminal(terminal =>
{
    terminal.Name = "term";
    terminal.Window = TerminalWindow.New;   // separate console window on Windows
    terminal.Format = TerminalFormat.Compact;
    terminal.MinLevel = Level.Debug;
}));

options.Relays.Add(Relay.File(file =>
{
    file.Name = "file";
    file.Directory = "logs/glitchport";
    file.Format = FileFormat.Ndjson;
    file.MaxFileSizeMb = 10;
    file.MaxAgeDays = 7;
    file.MaxTotalSizeMb = 200;
    file.MaxFiles = 100;
    file.FlushIntervalMs = 1000;
    file.MinLevel = Level.Info;
}));

using var client = new GlitchPortClient(options);
```

### The legacy `Dsn` property

`GlitchPortOptions.Dsn` predates the relay list. When it is set, the client
creates one implicit HTTP relay named `http` in front of the entries in
`Relays`. Both mechanisms may be combined, as in the example above: the
resulting client has two HTTP relays (`Dsn` and `main`) plus the terminal and
file relays. Setting `Dsn` is exactly equivalent to adding
`Relay.Http("<the same DSN>")` as the first entry.

### HTTP relay

`Relay.Http` parses its argument with `Dsn.Parse` and posts every event to:

```text
POST {scheme}://{host}:{port}/api/{projectId}/messages
X-GlitchPort-Key: {publicKey}
Content-Type: application/json
```

The public key is the DSN's URI userinfo and the project id is the last path
segment, so `http://dev-public-key@localhost:5080/1` posts to
`http://localhost:5080/api/1/messages`. A failed request is retried once after
200 ms before it is counted as failed. All HTTP relays of a client share one
`HttpClient` (an instance may be injected through the `GlitchPortClient`
constructor).

### Terminal relay

`Relay.Terminal` starts the `glitchport-terminal` renderer as a child process on
the first event that passes the relay's `MinLevel` and streams events to its
standard input. The relay never starts a second renderer: if the child exits or
its pipe breaks, later events are reported as failed.

| Option           | Default                                                                 | Meaning                                                        |
| ---------------- | ----------------------------------------------------------------------- | -------------------------------------------------------------- |
| `Name`           | `terminal`                                                              | Relay name in delivery reports.                                |
| `MinLevel`       | `Level.Debug`                                                           | Lowest level the renderer receives.                            |
| `QueueCapacity`  | `1000`                                                                  | Maximum number of events waiting for the renderer.             |
| `Window`         | `TerminalWindow.Inherit`                                                | `New` allocates a separate console window (Windows only; stdin is preserved). |
| `Format`         | `TerminalFormat.Pretty`                                                 | `Pretty`, `Compact` or `Json` rendering.                       |
| `AppName`        | The current process name                                                | Name shown in the renderer's session header.                   |
| `ExecutablePath` | `null`                                                                  | Explicit renderer path; otherwise the discovery order below.   |

The renderer is looked up in this order; the first existing file wins:

1. `TerminalRelayOptions.ExecutablePath` (`path=` in `GLITCHPORT_DSN`),
2. the `GLITCHPORT_TERMINAL_PATH` environment variable,
3. `glitchport-terminal(.exe)` in the application directory
   (`AppContext.BaseDirectory`),
4. `runtimes/<rid>/native/glitchport-terminal(.exe)` in the application
   directory.

The renderer reads a small JSON-lines protocol from standard input:

```json
{"type":"hello","protocol":1,"parent_pid":12345,"app":"MyGame"}
{"type":"event","payload":{"event_id":"…","level":"error","message":"…"}}
```

**Window behaviour.** `TerminalWindow.Inherit` (the default) uses the console
that the game already owns, on every platform. `TerminalWindow.New` detaches
from that console and allocates a fresh window on Windows.

With `TerminalWindow.New` the renderer preserves a relay-provided standard input
across the re-attach. When standard input is redirected — which is always the
case when `glitchport-terminal` is started by the terminal relay — the pipe is
captured before `FreeConsole`/`AllocConsole` and remains the renderer's only
input source; the new console is used for output only and is never read. The
protocol stream therefore stays connected: the hello and event lines continue
to be read and rendered, and a full stdin pipe can never block the relay. A
directly started renderer with no redirected standard input keeps reading the
new console exactly as before.

**Output encoding.** On Windows, when standard output is a real console, both
`TerminalWindow.Inherit` and `TerminalWindow.New` write UTF-16 through
`WriteConsoleW`, so characters such as `═` (U+2550), `·` (U+00B7) and non-ASCII
event messages display correctly regardless of the console's active output code
page. The renderer never calls `SetConsoleOutputCP` and never changes
`Console.OutputEncoding`, so `TerminalWindow.Inherit` does not modify the parent
game or server console. When standard output is redirected to a pipe or a file,
or on platforms other than Windows, output is UTF-8 without a BOM.

On Linux and macOS `TerminalWindow.New` is not supported: those platforms have
no analogous per-process console. The renderer prints exactly one warning to
standard error:

```text
warning: --window new is not supported on this platform; falling back to --window inherit
```

and otherwise behaves exactly like `TerminalWindow.Inherit`, including reading
redirected standard input.

**Parent lifetime.** The renderer must never outlive the process it reports on:

- On Windows the child is assigned to a job object with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so the operating system terminates it
  when the parent exits, including a crash or a forced kill.
- On every platform the relay closes the child's standard input when the client
  is disposed, which makes the renderer drain its input and exit; after a
  two-second grace period the relay kills the child.
- The renderer also watches `parent_pid` itself and exits when the parent
  process disappears (on Linux it also requests `PR_SET_PDEATHSIG`), so a
  renderer started by a killed parent does not stay around with an idle window.

`TerminalRelay.ProcessId` exposes the native pid of the renderer once it has
been started, and `TerminalRelay.ExecutablePath` exposes the resolved path. The
`HelloClient` sample prints a `terminal_pid: <pid>` line so scripts can verify
the parent/child coupling.

### File relay

`Relay.File` writes every event as one line to rotating local log files. It is
safe in headless environments and creates the target directory (including
parents) when the relay is created.

| Option           | Default           | Meaning                                                              |
| ---------------- | ----------------- | -------------------------------------------------------------------- |
| `Name`           | `file`            | Relay name in delivery reports.                                      |
| `MinLevel`       | `Level.Debug`     | Lowest level written.                                                |
| `QueueCapacity`  | `1000`            | Maximum number of events waiting for the writer.                     |
| `Directory`      | `logs/glitchport` | Target directory; relative paths are resolved when the relay is created. |
| `Format`         | `FileFormat.Ndjson` | `Ndjson` (one JSON payload per line) or `Text`.                    |
| `MaxFileSizeMb`  | `10`              | Rotate once the active file exceeds this size; `0` starts a new file per event. |
| `MaxAgeDays`     | `7`               | Delete own files older than this; `0` disables the age limit.        |
| `MaxTotalSizeMb` | `200`             | Delete the oldest own files above this total; `0` disables the limit. |
| `MaxFiles`       | `100`             | Keep at most this many own files; `0` disables the limit.            |
| `FlushIntervalMs` | `1000`           | Flush buffered writes to disk at this interval.                      |

**File names.** Each relay writes to one file at a time, named with the local
timestamp and the process id:

```text
glitchport-{yyyyMMdd-HHmmss}-{pid}.ndjson   (FileFormat.Ndjson)
glitchport-{yyyyMMdd-HHmmss}-{pid}.log      (FileFormat.Text)
```

If a file with that name already exists (two writers in the same second), a
numeric suffix is appended: `glitchport-20261114-101500-4321-1.ndjson`.

**Formats.** `Ndjson` writes the raw event payload, one JSON object per line.
`Text` writes the same compact line as the terminal renderer:
`2026-11-14 10:15:00.123 ERROR message  (id 1a2b3c4d)`.

**Rotation.** A new file is started when the local date changes or when the
active file has exceeded `MaxFileSizeMb`. The next event continues the same file
after a flush unless a rotation is due.

**Retention.** Retention runs once per relay, triggered by the first event (or
the first explicit flush). Only files matching the naming pattern above are
considered. Files older than `MaxAgeDays` are removed first, then the oldest
files are removed until `MaxFiles` and `MaxTotalSizeMb` are satisfied. The file
that is currently being written is never deleted.

**Permissions.** New files are created with mode `0600` (owner read/write only)
on Linux and macOS. On Windows the normal ACL of the directory applies.

**Durability.** Writes are buffered. The buffer is flushed every
`FlushIntervalMs`, immediately after an `Error` or `Fatal` event, and whenever
`FlushAsync` or `Dispose` runs. A flush also closes the OS file handle, so the
file can be read by another tool while the game keeps running.

## Configuring relays with `GLITCHPORT_DSN`

`GlitchPortOptions.FromEnvironment()` reads the `GLITCHPORT_DSN` environment
variable and turns it into a client configuration. The variable holds a
semicolon-separated list of relay specifications; whitespace around an item and
empty items are ignored. Every item creates one entry in `Relays` (it never sets
`Dsn`).

```bash
export GLITCHPORT_DSN="http://public-key@relay.example.com:5080/1?min_level=error&name=main;terminal:?window=new&format=compact&min_level=debug&name=term;file:?dir=logs/glitchport&format=ndjson&max_file_size_mb=10&max_age_days=7&max_total_size_mb=200&max_files=100&flush_interval_ms=1000&min_level=info&name=file"
```

```csharp
using GlitchPort;

var options = GlitchPortOptions.FromEnvironment();
options.SdkName = "my-game";

using var client = new GlitchPortClient(options);
```

The grammar of one item is:

```text
http://key@host:port/projectId?min_level=error&name=main
terminal:?window=new|inherit&format=pretty|compact|json&min_level=debug&name=term&path=<exe>
file:?dir=<path>&format=ndjson|text&max_file_size_mb=10&max_age_days=7&max_total_size_mb=200&max_files=100&flush_interval_ms=1000&min_level=info&name=file
```

An item without parameters may omit the question mark (`terminal` and `file`
are valid on their own). Parameter values are URI-decoded, so percent-encode
characters that would otherwise be structural: a space is `%20`, an ampersand
inside a value is `%26`, and a semicolon is `%3B`. For the `file` relay's `dir`,
`~` and environment variables in `%NAME%`, `$NAME` or `${NAME}` form are
expanded after decoding.

An invalid item makes `FromEnvironment()` throw a `FormatException` whose
message contains the 1-based item index and the reason. Unknown relay types,
unknown parameters and malformed values are all rejected instead of ignored.

### HTTP parameters

```text
http://key@host:port/projectId?min_level=error&name=main
```

| Parameter   | Values                                          | Default  | Meaning                                  |
| ----------- | ----------------------------------------------- | -------- | ---------------------------------------- |
| `min_level` | `debug`, `info`, `warning`, `error`, `fatal`     | `debug`  | Lowest level sent to the collector.      |
| `name`      | Any text                                        | `http`   | Relay name in delivery reports.          |

`key`, `host` and `projectId` are required; `port` is optional and defaults to
the scheme's port. `http` and `https` are supported.

### Terminal parameters

```text
terminal:?window=new|inherit&format=pretty|compact|json&min_level=debug&name=term&path=<exe>
```

| Parameter   | Values                                    | Default    | Meaning                                                 |
| ----------- | ----------------------------------------- | ---------- | ------------------------------------------------------- |
| `window`    | `new`, `inherit`                          | `inherit`  | `new` opens a separate window on Windows; other platforms warn once and inherit. |
| `format`    | `pretty`, `compact`, `json`               | `pretty`   | Renderer output format.                                 |
| `min_level` | `debug`, `info`, `warning`, `error`, `fatal` | `debug` | Lowest level rendered.                                  |
| `name`      | Any text                                  | `terminal` | Relay name in delivery reports.                         |
| `path`      | Path to `glitchport-terminal(.exe)`       | unset      | Overrides the executable discovery described above.     |

### File parameters

```text
file:?dir=<path>&format=ndjson|text&max_file_size_mb=10&max_age_days=7&max_total_size_mb=200&max_files=100&flush_interval_ms=1000&min_level=info&name=file
```

| Parameter           | Values                                    | Default           | Meaning                                                       |
| ------------------- | ----------------------------------------- | ----------------- | ------------------------------------------------------------- |
| `dir`               | Directory path                            | `logs/glitchport` | Target directory; `~` and environment variables are expanded. |
| `format`            | `ndjson`, `text`                          | `ndjson`          | File format.                                                  |
| `max_file_size_mb`  | Integer `>= 1`                            | `10`              | Rotation threshold in MB.                                     |
| `max_age_days`      | Integer `>= 0`                            | `7`               | Delete own files older than this; `0` disables.               |
| `max_total_size_mb` | Integer `>= 0`                            | `200`             | Total size budget in MB; `0` disables.                        |
| `max_files`         | Integer `>= 0`                            | `100`             | Maximum number of own files; `0` disables.                    |
| `flush_interval_ms` | Integer `>= 1`                            | `1000`            | Flush interval in milliseconds.                               |
| `min_level`         | `debug`, `info`, `warning`, `error`, `fatal` | `debug`         | Lowest level written.                                         |
| `name`              | Any text                                  | `file`            | Relay name in delivery reports.                               |

The configuration examples above show `min_level=info` for the file relay; that
is an example value, not the default. Without a `min_level` parameter every
relay accepts all levels from `debug` up.

## Delivery semantics and reporting

- **Capture never blocks and never throws because of a relay.** `CaptureMessageAsync`
  builds the event once, enqueues it into every relay that accepts the level and
  returns the event id immediately. Argument, disposal and cancellation problems
  are reported as faulted tasks before enqueueing; delivery failures are not
  thrown at the capture site.
- **Queues are bounded.** Each relay accepts at most `QueueCapacity` events
  (default `1000`). When the queue is full the event is counted as `Dropped` for
  that relay; capture does not wait. Events below a relay's `MinLevel` are not
  sent to that relay at all and are not counted as failed or dropped.
- **Delivery is per relay.** A failure in one relay never affects the others.
  HTTP failures are retried once; a terminal or file relay that cannot write
  disables itself and reports the failure through its counters and `LastError`.
- **Flushing is explicit.** `FlushAsync(TimeSpan? timeout = null)` waits for
  every relay to drain the events queued before the call and returns a
  `DeliveryReport`. The default timeout is **5 seconds**; relays are flushed
  concurrently, so this is the overall budget. Pass
  `TimeSpan.InfiniteTimeSpan` to wait without a timeout. `Dispose` performs a
  best-effort flush of up to two seconds before releasing the relays, and the
  client also flushes at process exit.
- **Reports are snapshots.** Each `RelayDeliveryResult` exposes `Name`, `Type`,
  `Delivered`, `Failed`, `Dropped`, `LastError` and `Succeeded`
  (`Failed == 0 && Dropped == 0`). `DeliveryReport.AllSucceeded` combines all
  relays. A relay whose flush exceeds the timeout is reported as failed with an
  explanatory `LastError`.

```csharp
using GlitchPort;

var options = GlitchPortOptions.FromEnvironment();

using var client = new GlitchPortClient(options);

Guid eventId = await client.CaptureMessageAsync("level 'arena_02' failed to load", Level.Error);

DeliveryReport report = await client.FlushAsync(); // default timeout: 5 seconds

foreach (RelayDeliveryResult relay in report.Relays)
{
    Console.WriteLine(
        $"relay {relay.Name} ({relay.Type}): " +
        $"delivered={relay.Delivered} failed={relay.Failed} dropped={relay.Dropped}");

    if (!string.IsNullOrEmpty(relay.LastError))
    {
        Console.Error.WriteLine($"{relay.Name}: {relay.LastError}");
    }
}

if (!report.AllSucceeded)
{
    Environment.ExitCode = 1;
}

foreach (IRelay relay in client.Relays)
{
    if (relay is TerminalRelay terminal && terminal.ProcessId is int processId)
    {
        Console.WriteLine($"terminal_pid: {processId}");
    }
}
```

Typical output for a terminal and a file relay:

```text
relay term (terminal): delivered=1 failed=0 dropped=0
relay file (file): delivered=1 failed=0 dropped=0
terminal_pid: 12345
```
