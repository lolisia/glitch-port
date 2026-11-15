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

## What every relay receives

Every capture is serialized **once** into the protocol v1 event JSON documented
in [Event ingest API](http-api-events.md); all relays of a client forward that
same body. The captured event also exposes the parts of the payload that the
local renderers need:

| Property     | Content                                                                                                                        |
| ------------ | ------------------------------------------------------------------------------------------------------------------------------ |
| `EventId`    | The event id (`event_id`).                                                                                                      |
| `Timestamp`  | The capture time in UTC.                                                                                                        |
| `Level`      | The protocol level used for the relay's `MinLevel` filter and for rendering.                                                    |
| `Message`    | The display title: the event's `message`, or `<type>: <value>` of the outermost exception when the event carries no message.    |
| `Json`       | The complete compact v1 JSON body (HTTP body, offline queue file, NDJSON line and terminal `payload`).                          |
| `Exceptions` | Exception summaries ordered inner cause first, each with up to five application (`in_app`) frames.                              |
| `Tags`       | The merged tags.                                                                                                                |
| `Release`    | The release identifier, when set.                                                                                               |
| `Environment`| The deployment environment, when set.                                                                                           |

A captured exception therefore carries its exception chain, and a capture with
tags, a release or an environment carries all three to the terminal and file
relays as well as to the collector.

## Relay types

| Type       | Destination                                          | Typical use                                      | Network | Writes files                |
| ---------- | ---------------------------------------------------- | ------------------------------------------------ | ------- | --------------------------- |
| `http`     | A glitch-port Relay via `POST /api/{projectId}/events` | Production crash and error reporting           | Yes     | Offline queue (opt-in)      |
| `terminal` | `glitchport-terminal`, a child console renderer      | Interactive development and playtests            | No      | No                          |
| `file`     | Rotating NDJSON or text log files                    | Headless runs, CI, QA machines, crash triage     | No      | Yes                         |

All three types share the same settings: `Name`, `Enabled`, `MinLevel` and
`QueueCapacity`. The type is fixed when the relay definition is created. The
HTTP relay additionally offers the opt-in [offline queue](#offline-queue).

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

`Relay.Http` parses its argument with `Dsn.Parse` and posts every event to the
v1 ingest endpoint:

```text
POST {scheme}://{host}:{port}/api/{projectId}/events
X-GlitchPort-Key: {publicKey}
Content-Type: application/json; charset=utf-8
Content-Encoding: gzip          (only for bodies of 1024 bytes or more)
```

The public key is the DSN's URI userinfo and the project id is the last path
segment, so `http://dev-public-key@localhost:5080/1` posts to
`http://localhost:5080/api/1/events`.

**Body.** The request body is the protocol v1 event JSON produced by the client
— the same body the [Event ingest API](http-api-events.md) describes. An
exception capture carries the `exception` array, and the body carries `tags`,
`release` and `environment` whenever the event has them:

```json
{"event_id":"9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e","timestamp":"2026-11-15T10:22:31.123Z","level":"error","platform":"dotnet","release":"star-raiders@1.2.3","environment":"production","sdk":{"name":"glitchport.dotnet","version":"0.1.0"},"exception":[{"type":"System.InvalidOperationException","value":"boom"}],"tags":{"scene":"arena_02"}}
```

The serializer has already applied every protocol limit (message length, tag
count, context size, …) before the relay sees the body.

**Compression.** A body of **1024 UTF-8 bytes or more** is gzip-compressed and
sent with `Content-Encoding: gzip`; smaller bodies are sent as they are. The
Relay accepts a gzip-compressed body and rejects any other content encoding.

**Retries and failure classes.** A direct request is tried once and, on a
transient failure, retried once more after 200 ms. Transport exceptions
(including timeouts), `5xx` responses and the throttling responses `429` and
`503` are **transient**. Any other `4xx` response is **permanent**: it is not
retried and not queued, and the attempt counts as `Failed`. The status code and
a truncated response body are kept in the relay's `LastError`.

Without an offline queue a transient failure after the retry is counted as
`Failed`, exactly as before. With an offline queue the event is written to
`{Directory}/{timestamp}-{event_id}.json` and reported as queued instead — see
[Offline queue](#offline-queue).

All HTTP relays of a client share one `HttpClient` (an instance may be injected
through the `GlitchPortClient` constructor).

#### Offline queue

The HTTP relay can persist events that the collector cannot accept right now and
resend them later. The queue is **disabled by default**: it exists only when
`HttpRelayOptions.OfflineQueue.Directory` names a directory. Every HTTP relay
owns an `HttpOfflineQueueOptions` instance:

| Option                 | Default | Validation                                                                 | Meaning                                                                                                     |
| ---------------------- | ------- | -------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| `Directory`            | `null`  | `null` or whitespace disables the queue                                     | Directory that holds the queued event files. The relay creates it, including parents, when it is constructed. |
| `MaxEvents`            | `500`   | Integer `>= 1`; anything else throws `ArgumentOutOfRangeException`          | Maximum number of queued events; the oldest files are deleted first.                                         |
| `MaxAgeHours`          | `72`    | Integer `>= 0`; `0` disables the age limit                                  | Maximum age of a queued event in hours.                                                                     |
| `RetryIntervalSeconds` | `30`    | Integer `>= 1`; anything else throws `ArgumentOutOfRangeException`          | Seconds between background resend passes.                                                                   |

```csharp
using GlitchPort;

var options = new GlitchPortOptions();

options.Relays.Add(Relay.Http("http://public-key@relay.example.com:5080/1", http =>
{
    http.OfflineQueue.Directory = @"C:\games\logs\glitchport-queue";
    http.OfflineQueue.MaxEvents = 500;             // default
    http.OfflineQueue.MaxAgeHours = 72;            // default; 0 disables the age limit
    http.OfflineQueue.RetryIntervalSeconds = 30;   // default
}));

using var client = new GlitchPortClient(options);
```

When a directory is configured, the numeric limits are validated when the HTTP
relay is created. A directory that cannot be created or written never throws:
the relay keeps the reason in `LastError` and behaves as if no queue were
configured, so undeliverable events count as `Failed`. The same happens when
another live process holds the directory lock (see below).

The Unity package's C# configuration (`GlitchPortSettings` and
`GlitchPortUnityConfig`) does not expose the queue yet, so its behaviour is
unchanged: the relay is created without a queue directory and the default keeps
the queue disabled. The Core DLL bundled with the Unity package contains these
types and is refreshed by `scripts/build-unity-plugins.sh`.

**Storage.** Every queued event is one UTF-8 (no BOM) file named after the
event's UTC timestamp and id:

```text
{Directory}/{timestamp}-{event_id}.json
```

```text
C:\games\logs\glitchport-queue\20261115T102231123Z-9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e.json
```

The timestamp uses the `yyyyMMdd'T'HHmmssfff'Z'` format. The file content is the
**exact protocol v1 event JSON** the relay would have posted — the same
`CapturedEvent.Json` body, byte for byte, never the formatted or terminal
rendering. The write is atomic: a sibling temp file is flushed to disk and
renamed into place, so a reader or a restarted process never sees a partially
written event and a crash leaves no truncated `.json` file. A name collision
(the same event id and millisecond) appends `-1`, `-2`, … instead of
overwriting.

**Directory lock.** The queue directory is guarded by a
`.glitchport-offline.lock` file held open with exclusive sharing, so only one
live process uses a directory at a time. A relay that cannot acquire the lock
writes nothing, records the reason in `LastError` and counts its failed
deliveries as `Failed`. The lock file is not deleted on dispose; the OS releases
the handle when a process exits, including a crash, so a leftover lock file from
a dead process is reusable. Only files matching the
`{timestamp}-{event_id}.json` pattern belong to the queue: the lock file, temp
files and any other file in the directory are ignored and never deleted.

The queue persists across process restarts: a new relay that opens the same
directory finds the files and resends them. The Processor stores events
idempotently by `event_id`, so resending an event whose response was lost cannot
create a duplicate.

**Resending.** Queued events go to the same `POST /api/{projectId}/events`
endpoint with the same `X-GlitchPort-Key` and `Content-Type` headers, and the
same gzip compression for bodies of 1024 bytes or more, as a direct send. A
resend pass sends the **oldest event first** and runs:

- when the relay is created, before the first retry interval elapses,
- every `RetryIntervalSeconds`,
- immediately after a successful direct send,
- on `FlushAsync` and `Dispose` — the same hooks that flush the in-memory
  queue — including the client's process-exit flush.

A success deletes the file and counts the event as `Delivered`. A permanent
`4xx` (other than `429`) deletes the file too and counts it as `Failed`, because
retrying it would never succeed. A transient failure — transport error,
timeout, `5xx`, `429` or `503` — stops the pass and keeps the file for the next
cycle; the event is **not** counted as `Failed`, and the relay records the
reason in `LastError`. Resend passes are serialized, so the worker thread, the
retry thread and `FlushAsync` never send the same file at the same time.

**`Retry-After`.** A `429` or `503` response may carry `Retry-After` as
delta-seconds or as an HTTP date. The relay turns it into a hold that applies to
direct sends and resend passes alike: while the hold is active no request
reaches the relay, direct captures are written to the disk queue instead, and
resend passes stop. The retry thread wakes up when the hold expires. A direct
send whose hold is longer than the 200 ms retry delay queues the event
immediately and skips its second attempt; one without a usable header is an
ordinary transient failure that is retried once and then queued. A resend pass
that receives `429` or `503` without a usable `Retry-After` still applies the
default **5-second** hold.

**Retention.** After every write and at the start of every resend pass the queue
applies its limits. Files older than `MaxAgeHours` are deleted first; the
timestamp in the file name, not the file system's modification time, determines
the age. Then the oldest remaining files are deleted until at most `MaxEvents`
files remain. Every such deletion is counted as `Dropped`. `MaxAgeHours = 0`
disables the age limit, and a file that cannot be deleted is left in place
without blocking the queue. A corrupt queued file is removed and counted as
`Dropped` instead of blocking later events.

**Reporting.** `RelayDeliveryResult.Queued` is the number of events waiting in
the on-disk queue at the time of the snapshot; it is `0` when the queue is
disabled or unavailable. A queued event does not increment `Failed`, and
`Succeeded` keeps its existing meaning — `Failed == 0 && Dropped == 0` — so a
backlog safely stored on disk does not mark the relay as failed.

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
{"type":"event","payload":{"event_id":"9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e","timestamp":"2026-11-15T10:22:31.123Z","level":"error","platform":"dotnet","sdk":{"name":"glitchport.dotnet","version":"0.1.0"},"exception":[{"type":"System.InvalidOperationException","value":"boom"}],"tags":{"scene":"arena_02"},"release":"star-raiders@1.2.3","environment":"production"}}
```

`payload` is the complete protocol v1 event: the same JSON the HTTP relay posts
and the file relay writes to NDJSON. It is never a reduced shape, so the
renderer sees the exception chain, tags, release and environment of every
event. `Format` selects how the payload is rendered:

| `TerminalFormat` | Output                                                                                                                                                                                                                    |
| ---------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Pretty`         | Multi-line: local time, level and the title (the outermost exception's `type: value`, or the message for a message-only event). An event with an exception then adds up to five `  at Module.Function (file:line)` frames, one sorted `  tags: key=value, …` line and `  release:` / `  environment:` lines when it carries them; the last line is `  sdk={name}/{version} · id {short id}`. |
| `Compact`        | One line: `{local time} {LEVEL} {title}  (id {short id})`, with the same title rule.                                                                                                                                       |
| `Json`           | The raw `payload` JSON, exactly as received.                                                                                                                                                                              |

When standard output is a real console with ANSI support, `Pretty` and
`Compact` colour only the level token; `Json` is written verbatim.

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
| `Format`         | `FileFormat.Ndjson` | `Ndjson` (one v1 event JSON per line) or `Text` (one compact line per event). |
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

**Formats.** `Ndjson` writes the complete protocol v1 event JSON — the same
body the HTTP relay posts — as one JSON object per line. `Text` writes the same
compact line as the terminal renderer:
`2026-11-14 10:15:00.123 ERROR message  (id 1a2b3c4d)`. For an event with an
exception the title is the outermost exception's `type: value`, for example
`2026-11-14 10:15:00.123 ERROR System.InvalidOperationException: boom  (id 1a2b3c4d)`;
a message-only event keeps its message as the title.

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
http://key@host:port/projectId?min_level=error&name=main&offline_dir=<path>&offline_max_events=500&offline_max_age_hours=72&offline_retry_interval_seconds=30
terminal:?window=new|inherit&format=pretty|compact|json&min_level=debug&name=term&path=<exe>
file:?dir=<path>&format=ndjson|text&max_file_size_mb=10&max_age_days=7&max_total_size_mb=200&max_files=100&flush_interval_ms=1000&min_level=info&name=file
```

An item without parameters may omit the question mark (`terminal` and `file`
are valid on their own). Parameter values are URI-decoded, so percent-encode
characters that would otherwise be structural: a space is `%20`, an ampersand
inside a value is `%26`, and a semicolon is `%3B`. For the `file` relay's `dir`
and the HTTP relay's `offline_dir`, `~` and environment variables in `%NAME%`,
`$NAME` or `${NAME}` form are expanded after decoding.

An invalid item makes `FromEnvironment()` throw a `FormatException` whose
message contains the 1-based item index and the reason. Unknown relay types,
unknown parameters and malformed values are all rejected instead of ignored.

### HTTP parameters

```text
http://key@host:port/projectId?min_level=error&name=main&offline_dir=<path>&offline_max_events=500&offline_max_age_hours=72&offline_retry_interval_seconds=30
```

| Parameter                      | Values                                          | Default  | Meaning                                                                                       |
| ------------------------------ | ----------------------------------------------- | -------- | --------------------------------------------------------------------------------------------- |
| `min_level`                    | `debug`, `info`, `warning`, `error`, `fatal`     | `debug`  | Lowest level sent to the collector.                                                           |
| `name`                         | Any text                                        | `http`   | Relay name in delivery reports.                                                               |
| `offline_dir`                  | Directory path                                  | unset    | Enables the offline queue in this directory; absent disables it. `~` and environment variables are expanded. |
| `offline_max_events`           | Integer `>= 1`                                  | `500`    | Maximum number of queued events.                                                              |
| `offline_max_age_hours`        | Integer `>= 0`                                  | `72`     | Delete queued events older than this; `0` disables the age limit.                             |
| `offline_retry_interval_seconds` | Integer `>= 1`                                | `30`     | Seconds between background resend passes.                                                     |

`key`, `host` and `projectId` are required; `port` is optional and defaults to
the scheme's port. `http` and `https` are supported. `offline_dir` must not be
blank; omit it to keep the queue disabled. The numeric parameters keep their
defaults when omitted and are rejected when out of range.

For example, this item queues events under `C:\games\logs/glitchport-queue`
(the percent-encoded `C%3A%5Cgames%5Clogs%2Fglitchport-queue`) and keeps at most
500 of them:

```text
http://public-key@relay.example.com:5080/1?offline_dir=C%3A%5Cgames%5Clogs%2Fglitchport-queue&offline_max_events=500
```

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

- **Capture never blocks and never throws because of a relay.**
  `CaptureMessageAsync`, `CaptureExceptionAsync` and `CaptureEventAsync` build
  the event once, enqueue it into every relay that accepts the level and return
  the event id immediately. Argument, disposal and cancellation problems are
  reported as faulted tasks before enqueueing; delivery failures are not thrown
  at the capture site.
- **Queues are bounded.** Each relay accepts at most `QueueCapacity` events
  (default `1000`). When the queue is full the event is counted as `Dropped` for
  that relay; capture does not wait. Events below a relay's `MinLevel` are not
  sent to that relay at all and are not counted as failed or dropped.
- **Delivery is per relay.** A failure in one relay never affects the others.
  HTTP failures are retried once and, when the offline queue is enabled, kept on
  disk instead of being lost; a terminal or file relay that cannot write
  disables itself and reports the failure through its counters and `LastError`.
- **Flushing is explicit.** `FlushAsync(TimeSpan? timeout = null)` waits for
  every relay to drain the events queued before the call and returns a
  `DeliveryReport`. The default timeout is **5 seconds**; relays are flushed
  concurrently, so this is the overall budget. Pass
  `TimeSpan.InfiniteTimeSpan` to wait without a timeout. `Dispose` performs a
  best-effort flush of up to two seconds before releasing the relays, and the
  client also flushes at process exit.
- **Offline queued events are pending, not failed.** When the HTTP relay has an
  offline queue, a transiently undeliverable event is written to disk instead of
  being counted as `Failed`. `RelayDeliveryResult.Queued` reports how many
  events are waiting in that on-disk queue at the time of the snapshot (`0` when
  the queue is disabled or unavailable); it does not change `Succeeded`, which
  keeps its existing meaning (`Failed == 0 && Dropped == 0`).
- **Reports are snapshots.** Each `RelayDeliveryResult` exposes `Name`, `Type`,
  `Delivered`, `Failed`, `Dropped`, `Queued`, `LastError` and `Succeeded`
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
        $"delivered={relay.Delivered} failed={relay.Failed} dropped={relay.Dropped} " +
        $"queued={relay.Queued}");

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
relay term (terminal): delivered=1 failed=0 dropped=0 queued=0
relay file (file): delivered=1 failed=0 dropped=0 queued=0
terminal_pid: 12345
```

With an offline queue and a collector that is temporarily down, the HTTP relay
keeps the events on disk and reports them as queued rather than failed:

```text
relay http (http): delivered=1 failed=0 dropped=0 queued=2
```
