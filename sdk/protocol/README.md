# Event protocol v1

`event.v1.schema.json` is the single source of truth for the JSON body accepted
by the collector's ingest endpoints (`POST /api/{projectId}/events` and the
legacy `POST /api/{projectId}/messages`). SDKs, the Relay and tests all use the
same schema so the event format cannot drift between components.

The schema declares [JSON Schema 2020-12](https://json-schema.org/draft/2020-12/schema)
and uses `snake_case` field names.

## Rules

- At least one of `message` or `exception` is required (`anyOf` at the top
  level). Everything else is optional; the Relay supplies defaults.
- `exception` is ordered **inner to outer**: index `0` is the innermost cause
  and the last item is the exception that was observed by the runtime.
- `exception[*].stacktrace.frames` is ordered **deepest to shallowest**: index
  `0` is the earliest frame in the call chain and the last item is the crash
  site. The `in_app` flag is `true` for frames that belong to the game's own
  code.
- Unknown top-level properties are allowed by the schema and ignored by the
  Relay. The schema deliberately does not set `additionalProperties: false`.
- The limits in the schema are maxima. Over-length strings are truncated by the
  Relay and count limits keep a documented subset of entries instead of
  rejecting the event; only format errors produce `400`.
- `contexts` and `extra` are free-form objects. The Relay enforces a maximum
  serialized size of 64 KB (65536 bytes) for each and omits the value when it
  is larger; the event is still accepted.
- `user.ip_address` may be the literal `{{auto}}`; the Relay replaces it with
  the remote address of the request.

## Limits

| Field | Limit |
| --- | --- |
| `event_id` | 32 hex characters or a UUID string |
| `platform` | 32 characters |
| `logger` | 128 characters |
| `message` | 8192 characters |
| `release` | 200 characters |
| `environment` | 64 characters |
| `server_name` | 200 characters |
| `exception` | 10 items, inner to outer |
| `exception[*].type` | 200 characters (required) |
| `exception[*].value` | 8192 characters |
| `exception[*].module` | 200 characters |
| `exception[*].stacktrace.frames` | 250 items, deepest to shallowest |
| `frame` strings (`function`, `module`, `filename`, `abs_path`, `context_line`) | 1024 characters each |
| `tags` | 50 entries; keys 32 characters; values 200 characters |
| `contexts` | 64 KB serialized |
| `breadcrumbs` | 100 items |
| `fingerprint` | 10 strings of 200 characters |
| `extra` | 64 KB serialized |

The full field table, defaults, truncation rules and response codes are
documented in [documents/http-api-events.md](../../documents/http-api-events.md).

## Examples

| File | Covers |
| --- | --- |
| `examples/message-only.json` | Message event without an exception |
| `examples/exception-unity.json` | Unity/C# stack, `in_app` game frames, contexts, breadcrumbs, tags, user and SDK metadata |
| `examples/exception-dotnet-nested.json` | Three .NET exceptions in inner-to-outer order |
| `examples/custom-fingerprint.json` | Custom grouping fingerprint |

Validate every JSON file in this directory with:

```bash
bash scripts/verify.d/protocol-json.sh
```

The script only parses the files with Node.js; it needs no network or running
collector.
