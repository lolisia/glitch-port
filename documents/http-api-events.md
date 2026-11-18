# Event ingest API (protocol v1)

The Relay exposes the public event-collection API that SDKs and game servers
call. Events are structured JSON documents described by
[`sdk/protocol/event.v1.schema.json`](../sdk/protocol/event.v1.schema.json).
Field names use `snake_case`.

A DSN has the form:

```text
https://<public-key>@<relay-host>/<project-id>
```

The SDK posts to `https://<relay-host>/api/<project-id>/events` and sends the
public key in the `X-GlitchPort-Key` header. The key is write-only: it can
submit events but cannot read them. Projects and their public keys are created
and revoked through the [Management API](management-api.md), which returns the
DSN of a new project.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/api/{projectId}/events` | Submit one structured v1 event. |
| `POST` | `/api/{projectId}/messages` | Legacy message-only endpoint. The Relay converts the body to a v1 event and publishes it through the same pipeline. |

Both endpoints accept one JSON object per request and return the same response
shapes.

## Authentication

Every request must include the project's public key:

| Header | Required | Description |
| --- | --- | --- |
| `X-GlitchPort-Key` | Yes | The public key of the project identified by `{projectId}` in the path. A missing or invalid key returns `401`. |

The path project id and the key must belong together. Keys are safe to embed
in game clients; they grant write access only.

Public keys are managed through the [Management API](management-api.md):
`POST /api/manage/projects` creates a project with its first key and returns a
DSN, `POST /api/manage/projects/{projectId}/keys` adds more keys, and
`DELETE /api/manage/keys/{keyId}` revokes a key. Only active keys are accepted.
A revoked or unknown key returns `401 Unauthorized` with
`{"error": "A valid X-GlitchPort-Key header is required."}`, exactly like a
missing key.

The Relay loads keys from the Processor and refreshes them periodically
(`Relay:ConfigRefreshSeconds`, default 60 seconds). A revoked key stops working
after the next refresh; a newly created key starts working after the next
refresh. Unknown `(project id, key)` pairs are also kept in an in-memory
negative cache for `Relay:UnknownKeyCacheSeconds` (default 30 seconds) so
repeated bad keys do not cause repeated lookups. That cache is an internal
optimization only and does not change the contract: a key that is not valid for
the project always returns `401`, even while it is cached.

The Processor configuration is the only key source; the Relay has no static key
fallback. Until the first successful configuration load, both ingest endpoints
return `503 Service Unavailable` with `Retry-After: 5` and
`{"error":"project configuration not loaded"}`. SDKs retry (or queue) the
event, so nothing is lost during a Relay start or a Processor outage.

## Rate limits

The Relay applies ingest rate limits after authentication and before reading
the request body, so an invalid key always returns `401` even when the project
is over its limit.

| Scope | Configuration | Default | Meaning |
| --- | --- | --- | --- |
| Project | `Relay:RateLimit:PerProjectPerMinute` | `6000` | Requests per minute across all keys of a project. `0` means unlimited. |
| Key | `rate_limit_per_minute` of the key in the Management API | `null` | Independent requests-per-minute limit for one key; the project limit still applies. `null` means no per-key limit; `0` means unlimited. |

Both limits use token buckets that refill continuously. A request must pass
every applicable bucket, and a rejected request does not consume tokens from
any bucket. The counters live in memory in each Relay instance, so with several
Relay instances the effective limit is approximately the configured value
multiplied by the number of instances: counts are approximate and a request can
pass on one instance after another instance rejected it.

When a limit is exceeded the Relay returns `429 Too Many Requests` without
storing the event. The response carries `Retry-After` in whole seconds (at
least `1`) until the next token becomes available, and the body is:

```json
{ "error": "rate limit exceeded" }
```

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 2

{"error":"rate limit exceeded"}
```

Retry the same request after `Retry-After` seconds. Because events are stored
idempotently by `event_id`, the retry is safe.

## Request headers and size limit

| Header | Required | Description |
| --- | --- | --- |
| `X-GlitchPort-Key` | Yes | Public ingest key, see above. |
| `Content-Type` | Yes | `application/json`. |
| `Content-Encoding` | No | Set to `gzip` to send a gzip-compressed body. Any other encoding is rejected. |
| `User-Agent` | No | SDK name and version; the event's `sdk` field is authoritative. |

The decompressed request body is limited to **1 MB (1,048,576 bytes)**. A
larger body is rejected with `413 Payload Too Large`; it is not truncated. A
compressed body is limited by its decompressed size.

## Event body

Every event must be a JSON object with a `platform` and at least one of
`message` or `exception`. Everything else is optional. Unknown top-level
properties are allowed and ignored by the Relay; they are not stored.

### Top-level fields

| Field | Type | Default | Limit | Notes |
| --- | --- | --- | --- | --- |
| `event_id` | string | Generated by the Relay (UUID) | 32 hex characters or a UUID string | Used for idempotency. Re-sending the same id does not create a second event in the Processor. |
| `timestamp` | string | Relay receive time | ISO 8601 / RFC 3339 date-time with timezone | Time at which the event occurred. |
| `level` | string | `error` when `exception` is present, otherwise `info` | One of `debug`, `info`, `warning`, `error`, `fatal` | Event severity. |
| `platform` | string | none (required) | 32 characters, lowercase `[a-z0-9._-]` after normalization | Platform or runtime that sent the event, for example `unity`, `unreal`, `godot`, `dotnet` or `javascript`. A missing, blank or invalid value is rejected with `400`. |
| `logger` | string | none | 128 characters | Logger or subsystem that produced the event. |
| `message` | string | none | 8192 characters | Human-readable message. Required when `exception` is absent. |
| `release` | string | none | 200 characters | Release identifier, for example `mygame@1.2.3`. |
| `environment` | string | none | 64 characters | Deployment environment, for example `production`. |
| `server_name` | string | none | 200 characters | Host or server that produced the event. |
| `sdk` | object | none | `name` and `version` strings | Client SDK that produced the event: `{ "name": "glitchport.unity", "version": "0.1.0" }`. |
| `exception` | array | none | 10 items | Exceptions ordered **inner to outer** (innermost cause first). |
| `tags` | object | none | 50 properties; keys 32 characters; values 200 characters | String key/value pairs used for filtering and grouping. |
| `user` | object | none | `id`, `username`, `email`, `ip_address` strings | Affected user. `ip_address` may be the literal `{{auto}}`. |
| `contexts` | object | none | 64 KB serialized | Free-form context namespaces, for example `os`, `runtime`, `device`, `gpu` and `game`. |
| `breadcrumbs` | array | none | 100 items | Chronological trail of events leading to the error, oldest first. |
| `fingerprint` | array | none | 10 strings of 200 characters | Custom grouping key. Overrides the Processor's default fingerprint when present. |
| `extra` | object | none | 64 KB serialized | Free-form additional data. |

An event without `platform` (or with a blank value) is rejected:

```http
HTTP/1.1 400 Bad Request
Content-Type: application/json

{"error":"platform is required"}
```

### Exception objects

| Field | Type | Required | Limit | Notes |
| --- | --- | --- | --- | --- |
| `type` | string | Yes | 200 characters | Exception type, for example `System.NullReferenceException`. Must not be blank. |
| `value` | string | No | 8192 characters | Exception message. |
| `module` | string | No | 200 characters | Assembly, module or namespace that defines the exception. |
| `stacktrace` | object | No | see below | Stack trace captured for this exception. |

`exception` is ordered **inner to outer**: index `0` is the innermost cause
(the original exception) and the last item is the exception observed by the
runtime. A .NET `A -> B -> C` exception chain is sent as `[A, B, C]`.

### Stack frames

`exception[*].stacktrace.frames` is an array of frame objects:

| Field | Type | Limit | Notes |
| --- | --- | --- | --- |
| `function` | string | 1024 characters | Function or method name. |
| `module` | string | 1024 characters | Module, assembly or namespace that contains the frame. |
| `filename` | string | 1024 characters | Source file name as reported by the runtime. |
| `abs_path` | string | 1024 characters | Absolute path of the source file, when available. |
| `context_line` | string | 1024 characters | Source line at `lineno`, when available. |
| `lineno` | integer | none | 1-based line number. |
| `colno` | integer | none | 1-based column number. |
| `in_app` | boolean | none | `true` for frames that belong to the game's own code, `false` for engine and framework frames. |

Frames are ordered **deepest to shallowest** (oldest call first): index `0` is
the earliest frame in the call chain and the **last frame is the crash site**.
The Processor prefers `in_app` frames when it computes a default fingerprint.

### Breadcrumbs

| Field | Type | Notes |
| --- | --- | --- |
| `timestamp` | string | ISO 8601 / RFC 3339 date-time. |
| `category` | string | Category, for example `scene`, `input` or `http`. |
| `message` | string | Human-readable breadcrumb message. |
| `level` | string | One of `debug`, `info`, `warning`, `error`, `fatal`. |
| `type` | string | Breadcrumb type, for example `default` or `http`. |
| `data` | JSON value | Free-form JSON data attached to the breadcrumb. |

Breadcrumbs are sent in chronological order (oldest first).

### Tags and user

`tags` is an object with at most 50 properties. Property names are limited to
32 characters and every value must be a string of at most 200 characters. A
non-string value is a format error (`400`).

`user` accepts `id`, `username`, `email` and `ip_address`. The `ip_address`
value may be the literal `{{auto}}`; the Relay replaces it with the remote
address of the request before storing the event.

## Defaults and normalization

- Missing `event_id` values are generated by the Relay. The accepted id is
  returned in the `202` response as a canonical lowercase UUID.
- Missing `timestamp` values are replaced with the receive time.
- Missing `level` values default to `error` for exception events and `info`
  for message events.
- `platform` has no default. It is trimmed, truncated to 32 characters and
  lowercased, and must then match `[a-z0-9._-]`; otherwise the Relay returns
  `400` with `platform is required` or `platform must match [a-z0-9._-]`.
- Strings are trimmed. Unknown top-level properties are ignored.
- `{{auto}}` in `user.ip_address` is replaced with the request's remote IP
  address.

## Truncation and count limits

The Relay accepts an event with over-limit values and normalizes it instead of
returning `400`. Only format errors (wrong JSON type, unknown `level`, invalid
`event_id` or `timestamp`, a blank exception `type`, non-string tag values, a
missing or invalid `platform`, or neither `message` nor `exception`) are
rejected.

| Value | Behaviour |
| --- | --- |
| `message`, exception `value` | Truncated to 8192 characters. |
| `platform` | Trimmed, truncated to 32 characters, lowercased and then validated against `[a-z0-9._-]`; an invalid value is a format error. |
| `logger`, `release`, `environment`, `server_name` | Truncated to 128, 200, 64 and 200 characters. |
| `exception[*].type`, `exception[*].module` | Truncated to 200 characters. |
| Frame strings (`function`, `module`, `filename`, `abs_path`, `context_line`) | Truncated to 1024 characters each. |
| More than 10 exceptions | Only the first 10 (innermost causes) are kept. |
| More than 250 frames | Only the last 250 (crash side) are kept. |
| More than 100 breadcrumbs | Only the first 100 are kept. |
| More than 50 tags | Only the first 50 are kept. |
| More than 10 fingerprint entries | Only the first 10 are kept. |
| `contexts` or `extra` larger than 64 KB serialized | The value is omitted; the rest of the event is kept. |

A request body larger than 1 MB after decompression is rejected with `413`;
the body limit is never handled by truncation.

## Legacy messages endpoint

`POST /api/{projectId}/messages` remains available for older SDKs and simple
scripts. Its body is a smaller, message-only shape:

```json
{
  "event_id": "9f1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e",
  "timestamp": "2026-11-14T12:00:00.000Z",
  "level": "error",
  "message": "The matchmaking backend connection was lost",
  "sdk": { "name": "glitchport.dotnet", "version": "0.1.0" }
}
```

| Field | Type | Required | Limit | Default |
| --- | --- | --- | --- | --- |
| `event_id` | string | No | UUID | Generated by the Relay |
| `timestamp` | string | No | ISO 8601 date-time | Relay receive time |
| `level` | string | No | `debug`, `info`, `warning`, `error`, `fatal` | `info` |
| `message` | string | Yes | 8192 characters, not blank | – |
| `sdk` | object | No | `name` and `version` | none |

The Relay converts the request to a v1 event with `platform: "other"`, no
exception, and the submitted `message`, `event_id`, `timestamp`, `level` and
`sdk`, then publishes it through the same pipeline as `/events`. Unlike
over-length values on `/events`, a legacy message longer than 8192 characters
returns `400`. The successful response is `202` with the same
`{"id": "<event_id>"}` body.

## Responses

| Status | Meaning | Body / headers |
| --- | --- | --- |
| `202 Accepted` | The event was validated, normalized and handed to the Processor. | `{"id": "<event_id>"}` with the event id as a lowercase UUID. |
| `400 Bad Request` | The JSON or a field value is invalid. | `{"error": "<reason>"}`. Most format errors use `<field path>: <reason>`, for example `{"error": "exception[0].type: must not be blank"}`; platform errors are exactly `{"error":"platform is required"}` or `{"error":"platform must match [a-z0-9._-]"}`. |
| `401 Unauthorized` | `X-GlitchPort-Key` is missing, unknown, revoked or does not belong to the project. | `{"error": "A valid X-GlitchPort-Key header is required."}` |
| `413 Payload Too Large` | The decompressed body exceeds 1 MB. | `{"error": "<reason>"}` |
| `429 Too Many Requests` | The project or key exceeded its ingest rate limit. The event was not stored. | `{"error": "rate limit exceeded"}` and `Retry-After: <seconds>`. |
| `503 Service Unavailable` | The Relay has not loaded the Processor project configuration yet. Retry after the advertised delay; the event was not stored. | `{"error":"project configuration not loaded"}` and `Retry-After: 5`. |
| `503 Service Unavailable` | The event could not be published (for example the Processor is temporarily unavailable). The event was not acknowledged and can be retried. | `{"error": "<reason>"}` and `Retry-After: 5`. |

Clients should retry `429` and `503` after at least the number of seconds in
`Retry-After` (`5` seconds for `503`). Do not retry other `4xx` responses
without changing the request. Because the Processor stores events idempotently
by `event_id`, retrying with the same id is safe.

## Example

From the repository root, post the Unity example:

```bash
curl -i \
  -X POST "http://localhost:5080/api/1/events" \
  -H "X-GlitchPort-Key: dev-public-key" \
  -H "Content-Type: application/json" \
  --data-binary "@sdk/protocol/examples/exception-unity.json"
```

A successful call returns:

```http
HTTP/1.1 202 Accepted
Content-Type: application/json

{"id":"9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e"}
```

The same body can be sent gzip-compressed:

```bash
gzip -c sdk/protocol/examples/exception-unity.json |
  curl -i \
    -X POST "http://localhost:5080/api/1/events" \
    -H "X-GlitchPort-Key: dev-public-key" \
    -H "Content-Type: application/json" \
    -H "Content-Encoding: gzip" \
    --data-binary @-
```

Replace `localhost:5080`, project id `1` and `dev-public-key` with the values
from your DSN.

## See also

- [Management API](management-api.md)
- [Event protocol v1 schema](../sdk/protocol/event.v1.schema.json)
- [Event protocol README](../sdk/protocol/README.md)
- [Verification hooks](../scripts/verify.d/README.md)
