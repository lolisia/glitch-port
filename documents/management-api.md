# Management API

The Management API lets the dashboard and automation manage projects and public
ingest keys and read and update the issues, events and labels the Processor
stores. Projects and keys live in PostgreSQL. The API is served by the
Processor under `/api/manage`; with the default development settings the base
URL is `http://localhost:5090/api/manage`.

The API is intended for trusted server-side clients. Every request must carry
the Processor admin token (see [Authentication](#authentication)).

## Authentication

| Header | Required | Description |
| --- | --- | --- |
| `Authorization` | Yes | `Bearer <admin-token>`, where the token is `Processor:AdminToken` in the Processor configuration. |

A missing, malformed or wrong token returns `401 Unauthorized` with an empty
body:

```http
HTTP/1.1 401 Unauthorized
```

```bash
curl -s "http://localhost:5090/api/manage/projects/1/issues" \
  -H "Authorization: Bearer dev-admin-token"
```

## Conventions

- Request and response bodies are JSON with `snake_case` field names.
- Timestamps are ISO 8601 strings in UTC, for example
  `"2026-11-15T10:00:00+00:00"`. Date-time values are always written with a
  `+00:00` offset and are never localized.
- Issue, label, project, key and alert rule ids are 64-bit integers. Event ids
  are UUIDs written as lowercase strings, for example
  `"9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e"`.
- Project ids in paths are integers.
  `GET /api/manage/projects/{projectId}/issues` returns `404 Not Found` when
  the project does not exist; a project that exists but has no data simply
  returns empty results.
- Optional query parameters with an empty value are usually treated as absent;
  `GET /api/manage/projects/{projectId}/alert-notifications` rejects an empty
  `limit` with `400`.
- Errors return a JSON body with a single `error` field:

  ```json
  { "error": "limit must be between 1 and 100." }
  ```

### Pagination

List endpoints use keyset pagination with an opaque cursor:

1. Send the first request without `cursor`.
2. The response contains `items` and `next_cursor`.
3. When `next_cursor` is a string, send it back as the `cursor` query parameter
   to fetch the next page. When it is `null`, the list is complete.

Cursors are opaque base64url tokens that encode the sort value and id of the
last row of the page. Clients must not parse or construct them. A cursor is only
valid together with the endpoint, the sort and the filters that produced it.
Any cursor that cannot be decoded returns `400 Bad Request`.

### Status codes

| Status | Meaning |
| --- | --- |
| `200 OK` | The request succeeded. |
| `201 Created` | The project, key, label or alert rule was created. |
| `204 No Content` | The resource was deleted or the key was revoked. The response has no body. |
| `400 Bad Request` | A query parameter, the JSON body or a cursor is invalid. |
| `401 Unauthorized` | The `Authorization` header is missing or wrong. |
| `404 Not Found` | The project, issue, event, key, label or alert rule does not exist. |
| `409 Conflict` | A project slug or a label name already exists. |

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/projects` | List projects with issue and key counts. |
| `POST` | `/projects` | Create a project with its first public key. |
| `GET` | `/projects/{projectId}` | Read one project with counts and DSN. |
| `PATCH` | `/projects/{projectId}` | Change a project name. |
| `DELETE` | `/projects/{projectId}` | Delete a project and all of its data. |
| `GET` | `/projects/{projectId}/keys` | List the public keys of a project. |
| `POST` | `/projects/{projectId}/keys` | Create a public ingest key. |
| `PATCH` | `/keys/{keyId}` | Change a key label, rate limit or active state. |
| `DELETE` | `/keys/{keyId}` | Revoke a key; the row is retained. |
| `GET` | `/projects/{projectId}/issues` | List issues with filters, sorting and cursor paging. |
| `GET` | `/issues/{issueId}` | Read one issue with its labels and latest event. |
| `PATCH` | `/issues/{issueId}` | Change the status and/or replace the label set. |
| `DELETE` | `/issues/{issueId}` | Delete an issue and its events. |
| `POST` | `/issues/bulk` | Apply a bulk action to up to 500 issues. |
| `GET` | `/projects/{projectId}/releases` | List the releases observed in stored events. |
| `GET` | `/issues/{issueId}/tags` | Tag distribution of an issue's sampled events. |
| `GET` | `/issues/{issueId}/events` | List the events of an issue, newest first. |
| `GET` | `/events/{eventId}` | Read one event including its payload. |
| `DELETE` | `/events/{eventId}` | Delete one event and decrement the issue counter. |
| `GET` | `/projects/{projectId}/labels` | List the labels of a project. |
| `POST` | `/projects/{projectId}/labels` | Create a label. |
| `PATCH` | `/labels/{labelId}` | Change a label name and/or color. |
| `DELETE` | `/labels/{labelId}` | Delete a label and its assignments. |
| `GET` | `/projects/{projectId}/stats` | Count events per UTC time bucket and issues and events per platform. |
| `GET` | `/maintenance/retention` | Report the retention window and the counters of the newest retention run. |
| `GET` | `/projects/{projectId}/alert-rules` | List the alert rules of a project. |
| `POST` | `/projects/{projectId}/alert-rules` | Create an alert rule. |
| `PATCH` | `/alert-rules/{ruleId}` | Change an alert rule. |
| `DELETE` | `/alert-rules/{ruleId}` | Delete an alert rule and its queued notifications. |
| `POST` | `/alert-rules/{ruleId}/test` | Send a sample notification without queueing it. |
| `GET` | `/projects/{projectId}/alert-notifications` | List the newest alert notifications of a project. |

The legacy `GET /api/manage/messages` endpoint (limit 1–500) remains available
for clients that only need the message list; it is not part of the issue API.

## Projects and keys

Projects and their public ingest keys are stored in PostgreSQL. Every project
has a `slug` that is unique across the installation. A project owns zero or
more keys; each key is identified by a 32-character lowercase hexadecimal
`public_key` and can be revoked without deleting its row.

### Project object

| Field | Type | Description |
| --- | --- | --- |
| `id` | integer | Project id. |
| `name` | string | Display name. |
| `slug` | string | Unique URL-friendly identifier. |
| `platforms` | array of strings | Platforms of the project's issues. Distinct `issue.platform` values ordered by the total `event_count` of that platform descending, then platform name ascending. Empty when the project has no issues. |
| `created_at` | timestamp | Creation time. |
| `issue_count` | integer | Unresolved issues. Returned by the list and detail endpoints, not by create. |
| `key_count` | integer | Active keys. Revoked keys are not counted. Returned by the list and detail endpoints, not by create. |
| `inbound_filters` | object | Inbound filters of the project as `releases`, `environments` and `message_patterns` string arrays. Returned by the list and detail endpoints, not by create. Empty arrays disable a filter; see [Inbound filters](#inbound-filters). |
| `dsn` | string or null | Ingest DSN of the first active key, or `null` when every key is revoked. Returned by the detail endpoint and by create responses. |

Platforms belong to events, not projects: the SDK reports the platform with
each event and the project's `platforms` are derived from the issues it
contains. `POST` and `PATCH` therefore reject a `platform` member with
`400 Bad Request` and the error
`platform is no longer a project setting; events carry their own platform`.

The DSN has the form:

```text
http(s)://<public_key>@<host>:<port>/<projectId>
```

It is built from the Processor setting `Processor:PublicRelayUrl`, which is
`http://localhost:5080` in development. The scheme (`http` or `https`), host
and port come from that setting, not from the request that created the project.
Production deployments must set `Processor:PublicRelayUrl` to the public Relay
URL when it differs from the request host; otherwise the returned DSN points at
the wrong Relay.

### Key object

| Field | Type | Description |
| --- | --- | --- |
| `id` | integer | Key id. |
| `public_key` | string | 32 lowercase hex characters. Sent as `X-GlitchPort-Key`. |
| `label` | string or null | Optional display label, for example `production`. |
| `is_active` | boolean | `false` for a revoked key. |
| `rate_limit_per_minute` | integer or null | Per-key ingest limit. `null` means no per-key limit; `0` means unlimited. |
| `created_at` | timestamp | Creation time. |
| `revoked_at` | timestamp or null | Time the key was revoked, or `null` while active. |
| `dsn` | string | Ingest DSN built from `Processor:PublicRelayUrl` in the form `http(s)://<public_key>@<host>:<port>/<projectId>`. |

Public keys grant write access only and are safe to embed in game clients.

## List projects

```http
GET /api/manage/projects
```

Returns all projects ordered by id. `issue_count` counts unresolved issues,
`key_count` counts active keys, and `platforms` lists the platforms observed in
the project's issues (ordered by total event count descending, then name):

```json
[
  {
    "id": 1,
    "name": "Default",
    "slug": "default",
    "platforms": ["unity", "dotnet"],
    "created_at": "2026-11-15T10:00:00+00:00",
    "issue_count": 3,
    "key_count": 2,
    "inbound_filters": {
      "releases": [],
      "environments": [],
      "message_patterns": []
    }
  }
]
```

The list item does not include `dsn`; read the project detail or one of its
keys to get a DSN.

## Create a project

```http
POST /api/manage/projects
Content-Type: application/json
```

| Field | Type | Description |
| --- | --- | --- |
| `name` | string | Required. Display name. Must not be blank; whitespace is trimmed. |
| `slug` | string, optional | Unique identifier. When omitted it is derived from `name`: lowercase, every run of non-alphanumeric characters becomes a single `-`, leading and trailing `-` are removed, and an empty result becomes `project`. An explicit slug is used as provided and must not be blank. |

A `platform` member is not accepted. Sending one, whatever its value
(including `null`), returns `400 Bad Request` with the error
`platform is no longer a project setting; events carry their own platform`.

```json
{ "name": "Star Raiders", "slug": "star-raiders" }
```

Creating a project also creates one active public key atomically, so the
response already contains a usable DSN. `platforms` is empty because the new
project has no issues yet:

```json
{
  "id": 2,
  "name": "Star Raiders",
  "slug": "star-raiders",
  "platforms": [],
  "created_at": "2026-11-15T10:00:00+00:00",
  "dsn": "http://a1b2c3d4e5f60718293a4b5c6d7e8f90@localhost:5080/2"
}
```

Returns `201 Created`. The create response omits `issue_count`, `key_count` and
`inbound_filters`; read the project detail for the full object. `400` for a
blank name, a `platform` member or a malformed body. `409 Conflict` when
another project already uses the slug. Use
`GET /api/manage/projects/{projectId}/keys` to read the generated key's
`public_key` and `label`.

```bash
curl -s -X POST "http://localhost:5090/api/manage/projects" \
  -H "Authorization: Bearer dev-admin-token" \
  -H "Content-Type: application/json" \
  --data-binary '{"name":"Star Raiders"}'
```

## Get a project

```http
GET /api/manage/projects/{projectId}
```

Returns the project detail with its counts and the DSN of the first active key
(`null` when no active key remains):

```json
{
  "id": 2,
  "name": "Star Raiders",
  "slug": "star-raiders",
  "platforms": ["unity"],
  "created_at": "2026-11-15T10:00:00+00:00",
  "issue_count": 0,
  "key_count": 1,
  "inbound_filters": {
    "releases": [],
    "environments": [],
    "message_patterns": []
  },
  "dsn": "http://a1b2c3d4e5f60718293a4b5c6d7e8f90@localhost:5080/2"
}
```

`404` when the project does not exist.

## Update a project

```http
PATCH /api/manage/projects/{projectId}
Content-Type: application/json
```

| Field | Type | Description |
| --- | --- | --- |
| `name` | string, optional | New display name. Must not be blank. |
| `inbound_filters` | object, optional | Inbound filter arrays. Each supplied array (`releases`, `environments`, `message_patterns`) replaces the stored list of that kind; omitted arrays are left unchanged and an empty array clears one. See [Inbound filters](#inbound-filters). |

Omitted fields are left unchanged. The slug cannot be changed. A `platform`
member is rejected with `400 Bad Request` and the error
`platform is no longer a project setting; events carry their own platform`.
The response is the updated project detail, in the same shape as
[Get a project](#get-a-project). `400` for a blank name, a `platform` member, a
malformed `inbound_filters` object or a malformed body; `404` when the project
does not exist.

```json
{
  "inbound_filters": {
    "releases": ["star-raiders@1.2.*"],
    "environments": ["staging"],
    "message_patterns": ["*Timeout*"]
  }
}
```

```bash
curl -s -X PATCH "http://localhost:5090/api/manage/projects/2" \
  -H "Authorization: Bearer dev-admin-token" \
  -H "Content-Type: application/json" \
  --data-binary '{"inbound_filters":{"releases":["star-raiders@1.2.*"],"environments":["staging"],"message_patterns":["*Timeout*"]}}'
```

### Inbound filters

`inbound_filters` holds three glob lists that make the Relay drop an event
before it is published:

| Field | Type | Description |
| --- | --- | --- |
| `releases` | array of strings | Release globs. |
| `environments` | array of strings | Environment globs. |
| `message_patterns` | array of strings | Message globs. |

A stored list replaces the previous one only when the array is present in the
PATCH body; omitting an array keeps its stored value and sending `[]` clears it.
The `inbound_filters` member itself must be an object. Every item must be a
string of 1 to 200 characters after trimming; surrounding whitespace is removed
and duplicate values are collapsed (the first occurrence wins). An array holds
at most 50 items. Any other shape returns `400 Bad Request` and leaves the
stored filters unchanged.

Patterns are globs: `*` matches any string (including an empty one) and every
other character is literal. Matching is against the whole value and ignores
case. A `null` release, environment or message never matches. Release and
environment filters do not apply to the legacy `POST
/api/{projectId}/messages` endpoint, which only applies `message_patterns`; the
matching rules and the filtered `202` response are described in the
[Event ingest API](http-api-events.md#inbound-filters).

The Relay caches project configuration and refreshes it every
`Relay:ConfigRefreshSeconds` (default `60` seconds in development), so a filter
change starts applying after the next refresh. The full PATCH response includes
the stored filters:

```json
{
  "id": 2,
  "name": "Star Raiders",
  "slug": "star-raiders",
  "platforms": ["unity"],
  "created_at": "2026-11-15T10:00:00+00:00",
  "issue_count": 0,
  "key_count": 1,
  "inbound_filters": {
    "releases": ["star-raiders@1.2.*"],
    "environments": ["staging"],
    "message_patterns": ["*Timeout*"]
  },
  "dsn": "http://a1b2c3d4e5f60718293a4b5c6d7e8f90@localhost:5080/2"
}
```

## Delete a project

```http
DELETE /api/manage/projects/{projectId}
```

Deletes the project and cascades to its issues, events, labels, label
assignments, keys, alert rules and alert notifications. Legacy rows of
`/api/manage/messages` are not tied to projects and stay in place. Returns
`204 No Content`, or `404` when the project does not exist.

```bash
curl -s -X DELETE "http://localhost:5090/api/manage/projects/2" \
  -H "Authorization: Bearer dev-admin-token"
```

## List keys

```http
GET /api/manage/projects/{projectId}/keys
```

Returns every key of the project ordered by id, including revoked keys:

```json
[
  {
    "id": 5,
    "public_key": "a1b2c3d4e5f60718293a4b5c6d7e8f90",
    "label": "production",
    "is_active": true,
    "rate_limit_per_minute": null,
    "created_at": "2026-11-15T10:00:00+00:00",
    "revoked_at": null,
    "dsn": "http://a1b2c3d4e5f60718293a4b5c6d7e8f90@localhost:5080/2"
  }
]
```

`404` when the project does not exist.

## Create a key

```http
POST /api/manage/projects/{projectId}/keys
Content-Type: application/json
```

| Field | Type | Description |
| --- | --- | --- |
| `label` | string, optional | Display label, for example `production`. |
| `rate_limit_per_minute` | integer or null, optional | Per-key ingest limit. `null` (the default) means no per-key limit; `0` means unlimited. Negative values return `400`. |

```json
{ "label": "staging", "rate_limit_per_minute": 600 }
```

Returns `201 Created` with the [key object](#key-object), including its
`public_key` and `dsn`. `400` for a negative limit or a malformed body; `404`
when the project does not exist.

```bash
curl -s -X POST "http://localhost:5090/api/manage/projects/2/keys" \
  -H "Authorization: Bearer dev-admin-token" \
  -H "Content-Type: application/json" \
  --data-binary '{"label":"staging","rate_limit_per_minute":600}'
```

## Update a key

```http
PATCH /api/manage/keys/{keyId}
Content-Type: application/json
```

| Field | Type | Description |
| --- | --- | --- |
| `label` | string, optional | New display label. |
| `rate_limit_per_minute` | integer or null, optional | New per-key limit. An explicit `null` clears the limit, `0` means unlimited and a negative value returns `400`. |
| `is_active` | boolean, optional | `false` revokes the key and sets `revoked_at`; `true` re-enables it and clears `revoked_at`. |

Omitted fields are left unchanged. Send `rate_limit_per_minute: null`
explicitly to clear a configured limit; omitting the field keeps the current
value.

```json
{ "rate_limit_per_minute": null }
```

Returns `200 OK` with the updated [key object](#key-object). `400` for a
negative limit or a malformed body; `404` when the key does not exist.

## Revoke a key

```http
DELETE /api/manage/keys/{keyId}
```

Revokes the key without deleting its row: `is_active` becomes `false` and
`revoked_at` is set to the current time. The key stays visible in
[List keys](#list-keys) and its DSN remains readable, but the Relay rejects it
with `401 Unauthorized` after its next configuration refresh (every 60 seconds
by default, `Relay:ConfigRefreshSeconds`). Returns `204 No Content`,
or `404` when the key does not exist.

To re-enable a revoked key, send `PATCH /api/manage/keys/{keyId}` with
`{"is_active": true}`.

```bash
curl -s -X DELETE "http://localhost:5090/api/manage/keys/5" \
  -H "Authorization: Bearer dev-admin-token"
```

## Issue object

Most issue endpoints return this object:

| Field | Type | Description |
| --- | --- | --- |
| `id` | integer | Issue id. |
| `title` | string | Issue title derived from the exception or message. |
| `culprit` | string or null | In-app frame that caused the issue, for example `Assembly-CSharp.Weapon.Fire`. |
| `level` | string | Highest severity seen: `debug`, `info`, `warning`, `error` or `fatal`. |
| `platform` | string | Platform of the first event, for example `unity`. |
| `status` | string | `unresolved`, `resolved` or `ignored`. |
| `first_seen` | timestamp | Occurrence time of the earliest event. |
| `last_seen` | timestamp | Occurrence time of the latest event. |
| `event_count` | integer | Number of stored events. |
| `first_release` | string or null | Release in which the issue was first seen: the release of its earliest event (for issues that predate release tracking, the last release when the earliest event has none). `null` when no release is known. |
| `last_release` | string or null | Release of the most recent event. |
| `last_environment` | string or null | Environment of the most recent event. |
| `resolved_at` | timestamp or null | Time of the current resolution, or `null` while the issue is not resolved. Cleared when the issue is unresolved, ignored or regresses. |
| `resolved_in_release` | string or null | Release the current resolution is pinned to (`resolve_in: next_release`), or `null` for an immediate resolution. |
| `regressed_at` | timestamp or null | Time the issue last reopened after being resolved, while it is an unresolved regression; `null` otherwise. |
| `regression_count` | integer | Number of times a resolved issue reopened after its resolution. |
| `labels` | array | Attached labels as `{"id", "name", "color"}` objects, ordered by name. |

```json
{
  "id": 42,
  "title": "System.NullReferenceException: Object reference not set to an instance of an object",
  "culprit": "Assembly-CSharp.Weapon.Fire",
  "level": "error",
  "platform": "unity",
  "status": "unresolved",
  "first_seen": "2026-11-14T12:00:00+00:00",
  "last_seen": "2026-11-14T12:05:00+00:00",
  "event_count": 2,
  "first_release": "star-raiders@1.0.0",
  "last_release": "star-raiders@1.2.3",
  "last_environment": "production",
  "resolved_at": null,
  "resolved_in_release": null,
  "regressed_at": null,
  "regression_count": 0,
  "labels": [
    { "id": 7, "name": "regression", "color": "#888888" }
  ]
}
```

## List issues

```http
GET /api/manage/projects/{projectId}/issues
```

### Query parameters

| Parameter | Default | Description |
| --- | --- | --- |
| `status` | `unresolved` | `unresolved`, `resolved`, `ignored` or `all`. `all` disables the status filter. |
| `level` | none | Exact level: `debug`, `info`, `warning`, `error` or `fatal`. |
| `environment` | none | Only issues with at least one event in this environment. |
| `release` | none | Only issues with at least one event in this release. |
| `first_release` | none | Exact value of the issue's `first_release`. |
| `regressed` | none | Only `true` is accepted: returns only issues with a non-null `regressed_at`, that is, issues that reopened after being resolved and have not been triaged since. Any other value returns `400`. |
| `label` | none | Only issues carrying the label with this exact name. |
| `platform` | none | One or more comma-separated platform names, for example `unity,dotnet`. Each value is trimmed and lowercased, blank entries are ignored, and an issue matches when its `platform` equals one of the values exactly. |
| `q` | none | Case-insensitive substring match on the issue title. `%`, `_` and `\` are matched literally. |
| `sort` | `last_seen` | `last_seen`, `first_seen` or `count`, all descending with `id` as the tie-breaker. |
| `limit` | `25` | Page size from 1 to 100. |
| `cursor` | none | `next_cursor` of the previous page. |

Unknown `status`, `level` or `sort` values, a `regressed` value other than
`true`, a `limit` outside 1–100 and a malformed cursor return `400`.

### Response

```json
{
  "items": [
    {
      "id": 42,
      "title": "System.NullReferenceException: Object reference not set to an instance of an object",
      "culprit": "Assembly-CSharp.Weapon.Fire",
      "level": "error",
      "platform": "unity",
      "status": "unresolved",
      "first_seen": "2026-11-14T12:00:00+00:00",
      "last_seen": "2026-11-14T12:05:00+00:00",
      "event_count": 2,
      "first_release": "star-raiders@1.0.0",
      "last_release": "star-raiders@1.2.3",
      "last_environment": "production",
      "resolved_at": null,
      "resolved_in_release": null,
      "regressed_at": null,
      "regression_count": 0,
      "labels": []
    }
  ],
  "next_cursor": "WyIyMDI2LTExLTE0VDEyOjA1OjAwLjAwMDAwMDArMDA6MDAiLCI0MiJd"
}
```

`next_cursor` is `null` when the page is the last one. A project that exists
but has no issues returns `{"items": [], "next_cursor": null}`. An unknown
project returns `404 Not Found` with the standard error body.

```bash
curl -s "http://localhost:5090/api/manage/projects/1/issues?status=all&sort=count&limit=25" \
  -H "Authorization: Bearer dev-admin-token"
```

## List releases

```http
GET /api/manage/projects/{projectId}/releases
```

Returns the releases observed in the project's stored events, ordered by
`last_seen` descending and then by release name ascending. Only events with a
non-null `release` are part of the aggregate.

### Query parameters

| Parameter | Default | Description |
| --- | --- | --- |
| `environment` | none | Only events of this environment are counted. The value is trimmed; a blank value means no filter. |
| `limit` | `50` | Page size from 1 to 100. |
| `cursor` | none | `next_cursor` of the previous page. |

An unknown project returns `404 Not Found`. A `limit` outside 1–100, a
non-numeric limit or a malformed cursor returns `400 Bad Request`.

### Release item

| Field | Type | Description |
| --- | --- | --- |
| `release` | string | Release value carried by the events. |
| `first_seen` | timestamp | Occurrence time of the earliest stored event of the release. |
| `last_seen` | timestamp | Occurrence time of the newest stored event of the release. |
| `event_count` | integer | Number of stored events of the release. |
| `issue_count` | integer | Number of distinct issues with at least one stored event of the release. |
| `new_issue_count` | integer | Number of issues whose `first_release` is this release. |
| `unresolved_issue_count` | integer | Number of distinct issues with an event of the release whose current status is `unresolved`. |

```json
{
  "items": [
    {
      "release": "star-raiders@1.2.3",
      "first_seen": "2026-11-14T10:00:00+00:00",
      "last_seen": "2026-11-14T12:05:00+00:00",
      "event_count": 128,
      "issue_count": 9,
      "new_issue_count": 4,
      "unresolved_issue_count": 3
    },
    {
      "release": "star-raiders@1.2.2",
      "first_seen": "2026-11-13T08:00:00+00:00",
      "last_seen": "2026-11-13T20:00:00+00:00",
      "event_count": 64,
      "issue_count": 6,
      "new_issue_count": 1,
      "unresolved_issue_count": 0
    }
  ],
  "next_cursor": null
}
```

`next_cursor` is `null` on the last page. A project that exists but has no
released events returns `{"items": [], "next_cursor": null}`.

**Retention caveat.** The aggregate is computed from the `events` table, so it
covers only events still inside the retention window
(`Processor:Retention:Days`). A release whose events have all expired
disappears from the list, and the counters of a partially expired release
describe only the events that remain. `new_issue_count` is computed from the
surviving `issues` rows, so it can outlive the events that produced it.

```bash
curl -s "http://localhost:5090/api/manage/projects/1/releases?environment=production&limit=50" \
  -H "Authorization: Bearer dev-admin-token"
```

To fetch the next page, pass the `next_cursor` value back unchanged:

```bash
curl -s "http://localhost:5090/api/manage/projects/1/releases?environment=production&limit=50&cursor=WyIyMDI2LTExLTE0VDEyOjA1OjAwLjAwMDAwMDArMDA6MDAiLCJzdGFyLXJhaWRlcnNAMX4yfjMiXQ" \
  -H "Authorization: Bearer dev-admin-token"
```

## Get an issue

```http
GET /api/manage/issues/{issueId}
```

Returns the [issue object](#issue-object) plus:

| Field | Type | Description |
| --- | --- | --- |
| `latest_event` | object or null | The most recent event, including `payload`. `null` when the issue has no events. |

```json
{
  "id": 42,
  "title": "System.NullReferenceException: Object reference not set to an instance of an object",
  "culprit": "Assembly-CSharp.Weapon.Fire",
  "level": "error",
  "platform": "unity",
  "status": "unresolved",
  "first_seen": "2026-11-14T12:00:00+00:00",
  "last_seen": "2026-11-14T12:05:00+00:00",
  "event_count": 2,
  "first_release": "star-raiders@1.0.0",
  "last_release": "star-raiders@1.2.3",
  "last_environment": "production",
  "resolved_at": null,
  "resolved_in_release": null,
  "regressed_at": null,
  "regression_count": 0,
  "labels": [
    { "id": 7, "name": "regression", "color": "#888888" }
  ],
  "latest_event": {
    "id": "9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e",
    "project_id": 1,
    "issue_id": 42,
    "timestamp": "2026-11-14T12:05:00+00:00",
    "received_at": "2026-11-14T12:05:00+00:00",
    "level": "error",
    "platform": "unity",
    "environment": "production",
    "release": "star-raiders@1.2.3",
    "user_id": "player-123",
    "endpoint": "POST /api/1/events",
    "message": "NullReferenceException: Object reference not set to an instance of an object",
    "payload": {
      "level": "error",
      "platform": "unity",
      "message": "NullReferenceException: Object reference not set to an instance of an object",
      "exception": [
        {
          "type": "System.NullReferenceException",
          "value": "Object reference not set to an instance of an object"
        }
      ]
    }
  }
}
```

`404` when no issue with that id exists.

## Issue tag distribution

```http
GET /api/manage/issues/{issueId}/tags
```

Returns the distribution of tag values over the newest events of an issue. The
Processor samples at most `Processor:Management:TagSampleLimit` events
(default `10000`) ordered by `timestamp` and id descending, and aggregates the
facets in PostgreSQL; `sample_size` reports how many events were actually
sampled. The JSONB payload is expanded and aggregated inside PostgreSQL rather
than materialized in the application.

| Parameter | Default | Description |
| --- | --- | --- |
| `limit` | `10` | Maximum number of values returned per facet, from 1 to 25. |

The response is:

| Field | Type | Description |
| --- | --- | --- |
| `issue_id` | integer | Issue the distribution was computed for. |
| `sample_size` | integer | Number of newest events that were sampled. |
| `user_count` | integer | Number of distinct non-null `user.id` values among the sampled events. |
| `tags` | array | One facet per key, in the order described below. |

A facet is:

| Field | Type | Description |
| --- | --- | --- |
| `key` | string | Facet key, for example `release` or `tags.map`. |
| `total` | integer | Number of sampled events with a non-empty value of the key. |
| `distinct` | integer | Number of distinct non-empty values of the key. |
| `top` | array | Up to `limit` most frequent values as `{"value", "count"}`, ordered by count descending and value ascending. |

The eight fixed facets are always returned, in this order, even when they have
no values: `release`, `environment`, `platform`, `os.name`, `device.model`,
`gpu.name`, `gpu.api` and `runtime.version`. They read the event's release,
environment and platform columns and the payload contexts
`contexts.os.name`, `contexts.device.model`, `contexts.gpu.name`,
`contexts.gpu.api` and `contexts.runtime.version`.

After the fixed facets, up to 20 payload tag keys get a facet named
`tags.<key>`, for example `tags.map` for a payload `"tags": {"map": "dust"}`.
They are ordered by their `total` descending and then by key ascending. A tag
key whose values are all empty or `null` does not get a facet, and empty or
`null` values never contribute to `total`, `distinct` or `top`.

```json
{
  "issue_id": 42,
  "sample_size": 120,
  "user_count": 7,
  "tags": [
    {
      "key": "release",
      "total": 120,
      "distinct": 3,
      "top": [
        { "value": "star-raiders@1.2.3", "count": 100 },
        { "value": "star-raiders@1.2.2", "count": 15 }
      ]
    },
    {
      "key": "environment",
      "total": 120,
      "distinct": 2,
      "top": [
        { "value": "production", "count": 110 },
        { "value": "staging", "count": 10 }
      ]
    },
    {
      "key": "platform",
      "total": 120,
      "distinct": 1,
      "top": [
        { "value": "unity", "count": 120 }
      ]
    },
    {
      "key": "tags.map",
      "total": 80,
      "distinct": 2,
      "top": [
        { "value": "dust", "count": 60 },
        { "value": "inferno", "count": 20 }
      ]
    }
  ]
}
```

Only the first few fixed facets are shown above; a real response contains all
eight fixed facets. `404` when the issue does not exist. A `limit` outside 1–25
or a non-numeric limit returns `400`.

```bash
curl -s "http://localhost:5090/api/manage/issues/42/tags?limit=25" \
  -H "Authorization: Bearer dev-admin-token"
```

## Update an issue

```http
PATCH /api/manage/issues/{issueId}
Content-Type: application/json
```

| Field | Type | Description |
| --- | --- | --- |
| `status` | string, optional | New status: `unresolved`, `resolved` or `ignored`. |
| `resolve_in` | string, optional | Only allowed together with `status: resolved`. `now` (the default) resolves immediately; `next_release` pins the issue's most recent release as `resolved_in_release`. Any other value, or sending `resolve_in` without `status: resolved`, returns `400`. |
| `labels` | array of strings, optional | Replaces the complete label set of the issue. Names that do not exist in the project are created with the default color `#888888`. An empty array clears all labels. |

All fields are optional; omitted fields are left unchanged. Whitespace around
statuses and label names is trimmed, and duplicate label names are collapsed.
Blank label names, unknown statuses, an invalid `resolve_in` and the invalid
`resolve_in` combinations return `400`.

A status change maintains the resolution bookkeeping:

- `resolved` sets `resolved_at`. With `resolve_in: now` (or no `resolve_in`)
  it leaves `resolved_in_release` `null`; with `next_release` it copies the
  issue's `last_release`, which may itself be `null` when no event carried a
  release.
- `unresolved` and `ignored` clear `resolved_at` and `resolved_in_release`.
- Every status change clears `regressed_at`.

The bookkeeping drives regression detection. A resolved issue reopens when a
later event arrives and either `resolved_in_release` is `null` (any event
reopens it) or the event's release differs from `resolved_in_release`. A new
event in the pinned release, or an event without a release, does not reopen a
pinned resolution. Reopening sets `status` to `unresolved`, sets
`regressed_at`, increments `regression_count` and clears `resolved_at` and
`resolved_in_release`.

```json
{ "status": "resolved", "resolve_in": "next_release", "labels": ["regression", "crash"] }
```

The response is the updated issue object (without `latest_event`):

```json
{
  "id": 42,
  "title": "System.NullReferenceException: Object reference not set to an instance of an object",
  "culprit": "Assembly-CSharp.Weapon.Fire",
  "level": "error",
  "platform": "unity",
  "status": "resolved",
  "first_seen": "2026-11-14T12:00:00+00:00",
  "last_seen": "2026-11-14T12:05:00+00:00",
  "event_count": 2,
  "first_release": "star-raiders@1.0.0",
  "last_release": "star-raiders@1.2.3",
  "last_environment": "production",
  "resolved_at": "2026-11-14T12:10:00+00:00",
  "resolved_in_release": "star-raiders@1.2.3",
  "regressed_at": null,
  "regression_count": 0,
  "labels": [
    { "id": 8, "name": "crash", "color": "#888888" },
    { "id": 7, "name": "regression", "color": "#888888" }
  ]
}
```

`404` when the issue does not exist.

```bash
curl -s -X PATCH "http://localhost:5090/api/manage/issues/42" \
  -H "Authorization: Bearer dev-admin-token" \
  -H "Content-Type: application/json" \
  --data-binary '{"status":"resolved","resolve_in":"next_release"}'
```

## Delete an issue

```http
DELETE /api/manage/issues/{issueId}
```

Deletes the issue together with all of its events and label assignments and
returns `204 No Content`. Returns `404` when the issue does not exist.

```bash
curl -s -X DELETE "http://localhost:5090/api/manage/issues/42" \
  -H "Authorization: Bearer dev-admin-token"
```

## Bulk update issues

```http
POST /api/manage/issues/bulk
Content-Type: application/json
```

| Field | Type | Description |
| --- | --- | --- |
| `ids` | array of integers | Issue ids. At least 1 and at most 500 entries. Unknown ids are ignored. |
| `action` | string | `resolve`, `resolve_next_release`, `unresolve`, `ignore`, `delete`, `add_labels` or `remove_labels`. |
| `labels` | array of strings | Required for `add_labels` and `remove_labels`; must contain at least one non-blank name. Ignored for the other actions. |

```json
{ "ids": [42, 43, 44], "action": "add_labels", "labels": ["regression"] }
```

The response reports how many issues the action modified:

```json
{ "affected": 3 }
```

| Action | Behaviour | `affected` |
| --- | --- | --- |
| `resolve` | Sets `status` to `resolved`, records `resolved_at` and leaves `resolved_in_release` `null`. Clears `regressed_at`. | Number of issues updated. |
| `resolve_next_release` | Sets `status` to `resolved`, records `resolved_at` and pins each issue's own `last_release` as `resolved_in_release` (`null` when the issue has no release). Clears `regressed_at`. | Number of issues updated. |
| `unresolve` | Sets `status` to `unresolved` and clears `resolved_at`, `resolved_in_release` and `regressed_at`. | Number of issues updated. |
| `ignore` | Sets `status` to `ignored` and clears `resolved_at`, `resolved_in_release` and `regressed_at`. | Number of issues updated. |
| `delete` | Deletes the issues with their events and label assignments. | Number of issues deleted. |
| `add_labels` | Creates missing labels per project and attaches them. Already attached labels are kept. | Number of existing issues in `ids`. |
| `remove_labels` | Detaches the named labels. The label rows stay in place. | Number of issues that lost at least one label. |

`400` for an empty `ids` array, more than 500 ids, an unknown action, missing or
blank label names, or a malformed body.

```bash
curl -s -X POST "http://localhost:5090/api/manage/issues/bulk" \
  -H "Authorization: Bearer dev-admin-token" \
  -H "Content-Type: application/json" \
  --data-binary '{"ids":[42,43],"action":"resolve"}'
```

```bash
curl -s -X POST "http://localhost:5090/api/manage/issues/bulk" \
  -H "Authorization: Bearer dev-admin-token" \
  -H "Content-Type: application/json" \
  --data-binary '{"ids":[42,43],"action":"resolve_next_release"}'
```

## List the events of an issue

```http
GET /api/manage/issues/{issueId}/events
```

| Parameter | Default | Description |
| --- | --- | --- |
| `limit` | `25` | Page size from 1 to 100. |
| `cursor` | none | `next_cursor` of the previous page. |

Events are ordered newest first by `timestamp` and `id`. The response is:

```json
{
  "items": [
    {
      "id": "9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e",
      "timestamp": "2026-11-14T12:05:00+00:00",
      "received_at": "2026-11-14T12:05:00+00:00",
      "level": "error",
      "environment": "production",
      "release": "star-raiders@1.2.3",
      "user_id": "player-123",
      "message": "NullReferenceException: Object reference not set to an instance of an object"
    }
  ],
  "next_cursor": null
}
```

`environment`, `release`, `user_id` and `message` are `null` when the event did
not carry them. Returns `404` when the issue does not exist; an issue without
events returns an empty `items` array.

```bash
curl -s "http://localhost:5090/api/manage/issues/42/events?limit=25" \
  -H "Authorization: Bearer dev-admin-token"
```

## Get an event

```http
GET /api/manage/events/{eventId}
```

Returns the event with its complete payload exactly as the Processor stored it:

| Field | Type | Description |
| --- | --- | --- |
| `id` | string | Event UUID. |
| `project_id` | integer | Project the event belongs to. |
| `issue_id` | integer | Issue the event was grouped into. |
| `timestamp` | timestamp | Time at which the event occurred. |
| `received_at` | timestamp | Time at which the Relay received the event. |
| `level` | string | Event severity. |
| `platform` | string | Event platform. |
| `environment` | string or null | Event environment. |
| `release` | string or null | Event release. |
| `user_id` | string or null | `user.id` of the event. |
| `endpoint` | string | Ingest endpoint that received the event, for example `POST /api/1/events`. |
| `message` | string or null | Event message. |
| `payload` | object or array | Parsed event JSON as accepted by the Relay. |

```json
{
  "id": "9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e",
  "project_id": 1,
  "issue_id": 42,
  "timestamp": "2026-11-14T12:00:00+00:00",
  "received_at": "2026-11-14T12:00:00+00:00",
  "level": "error",
  "platform": "unity",
  "environment": "production",
  "release": "star-raiders@1.2.3",
  "user_id": "player-123",
  "endpoint": "POST /api/1/events",
  "message": "NullReferenceException: Object reference not set to an instance of an object",
  "payload": {
    "level": "error",
    "platform": "unity",
    "exception": [
      {
        "type": "System.NullReferenceException",
        "value": "Object reference not set to an instance of an object",
        "stacktrace": { "frames": [] }
      }
    ]
  }
}
```

Returns `404` when no event with that id exists or the id is not a UUID.

## Delete an event

```http
DELETE /api/manage/events/{eventId}
```

Deletes the event, decrements the `event_count` of its issue by one and keeps
the issue itself. The counter never goes below zero. Returns `204 No Content`,
or `404` when the event does not exist.

```bash
curl -s -X DELETE \
  "http://localhost:5090/api/manage/events/9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e" \
  -H "Authorization: Bearer dev-admin-token"
```

## List labels

```http
GET /api/manage/projects/{projectId}/labels
```

Returns all labels of the project ordered by name:

```json
[
  { "id": 7, "name": "crash", "color": "#ff0000" },
  { "id": 8, "name": "regression", "color": "#888888" }
]
```

An unknown project returns an empty array.

## Create a label

```http
POST /api/manage/projects/{projectId}/labels
Content-Type: application/json
```

| Field | Type | Description |
| --- | --- | --- |
| `name` | string | Required. Unique within the project. Leading and trailing whitespace is trimmed. |
| `color` | string, optional | Display color. Defaults to `#888888` and must not be blank. |

```bash
curl -s -X POST "http://localhost:5090/api/manage/projects/1/labels" \
  -H "Authorization: Bearer dev-admin-token" \
  -H "Content-Type: application/json" \
  --data-binary '{"name":"regression","color":"#888888"}'
```

Returns `201 Created` with the created label:

```json
{ "id": 8, "name": "regression", "color": "#888888" }
```

`400` for a missing or blank name or a blank color. `409 Conflict` when the
project already has a label with that name.

## Update a label

```http
PATCH /api/manage/labels/{labelId}
Content-Type: application/json
```

| Field | Type | Description |
| --- | --- | --- |
| `name` | string, optional | New name. Must not be blank. |
| `color` | string, optional | New color. Must not be blank. |

Omitted fields are left unchanged.

```json
{ "name": "bug", "color": "#00ff00" }
```

Returns `200 OK` with the updated label:

```json
{ "id": 8, "name": "bug", "color": "#00ff00" }
```

`400` for a blank name or color, `404` for an unknown label and `409 Conflict`
when another label of the same project already uses the name.

## Delete a label

```http
DELETE /api/manage/labels/{labelId}
```

Deletes the label and all of its `issue_labels` assignments. Issues stay in
place and lose only that label. Returns `204 No Content`, or `404` when the
label does not exist.

```bash
curl -s -X DELETE "http://localhost:5090/api/manage/labels/8" \
  -H "Authorization: Bearer dev-admin-token"
```

## Event statistics

```http
GET /api/manage/projects/{projectId}/stats?period=24h
```

| Parameter | Default | Description |
| --- | --- | --- |
| `period` | `24h` | `24h` for 24 hourly buckets or `14d` for 14 daily buckets. |

Events are counted by their `timestamp` in UTC. Every bucket of the window is
returned, including buckets with no events, in ascending order:

- `24h` returns 24 hourly buckets; the last bucket is the current UTC hour.
- `14d` returns 14 daily buckets; the last bucket is the current UTC day
  (00:00 UTC).

The response has two parts. `buckets` contains the period's buckets, each
`{ "bucket": <start of the bucket in UTC>, "count": <events> }`. `by_platform`
aggregates all issues of the project regardless of the period: `issue_count`
is the number of issues whose platform is that value, `event_count` is the sum
of their `event_count`. Entries are ordered by `event_count` descending, then
platform name ascending, and the array is empty when the project has no issues.

```json
{
  "buckets": [
    { "bucket": "2026-11-14T10:00:00+00:00", "count": 0 },
    { "bucket": "2026-11-14T11:00:00+00:00", "count": 3 },
    { "bucket": "2026-11-14T12:00:00+00:00", "count": 1 }
  ],
  "by_platform": [
    { "platform": "unity", "issue_count": 4, "event_count": 128 },
    { "platform": "dotnet", "issue_count": 2, "event_count": 31 }
  ]
}
```

Any other `period` value returns `400`.

```bash
curl -s "http://localhost:5090/api/manage/projects/1/stats?period=14d" \
  -H "Authorization: Bearer dev-admin-token"
```

## Retention status

```http
GET /api/manage/maintenance/retention
```

The Processor deletes events and legacy messages that are older than the
configured retention window in a background job. This endpoint reports that
window together with the counters of the most recently finished run. It is
protected by the same `Authorization: Bearer <admin-token>` header as every
other management endpoint and returns `401 Unauthorized` when the header is
missing or wrong.

| Field | Type | Description |
| --- | --- | --- |
| `days` | integer | Configured retention window in days (`Processor:Retention:Days`, default `30`). The value `0` means retention is disabled and no row is ever deleted. |
| `last_run_at` | timestamp or null | Finish time of the newest retention run, or `null` when no run has finished yet. |
| `last_deleted_events` | integer | Events deleted by the newest run. `0` when no run exists. |
| `last_deleted_issues` | integer | Issues deleted by the newest run. `0` when no run exists. |

```json
{
  "days": 30,
  "last_run_at": "2026-11-15T10:00:00+00:00",
  "last_deleted_events": 128,
  "last_deleted_issues": 4
}
```

The background job waits one full `Processor:Retention:IntervalMinutes`
(default `60`) before its first run and then repeats with that interval. It
deletes at most `Processor:Retention:BatchSize` events or messages per
transaction (default `10000`). The `last_*` values describe only the newest
run; they are not cumulative totals. A run that cannot acquire the internal
retention advisory lock leaves the data untouched and does not change these
values.

```bash
curl -s "http://localhost:5090/api/manage/maintenance/retention" \
  -H "Authorization: Bearer dev-admin-token"
```

## Alert rules

Alert rules notify an HTTP target when a project's issues change. A rule is
evaluated by the Processor; the Management API only creates and edits the
rules and reads the notifications they produced. The delivery configuration
(`Processor:Alerts`) is described in
[Operations](operations.md#alerts).

### Alert rule object

| Field | Type | Description |
| --- | --- | --- |
| `id` | integer | Rule id. |
| `project_id` | integer | Project the rule belongs to. |
| `name` | string | Human-readable rule name, unique only by convention. |
| `trigger` | string | `new_issue`, `regression` or `event_frequency`. |
| `min_level` | string or null | Lowest issue level that matches: `debug`, `info`, `warning`, `error` or `fatal`, or `null` for any level. |
| `environment` | string or null | Environment the rule is limited to, or `null` for any environment. |
| `threshold` | integer or null | Event count that triggers an `event_frequency` rule. `null` for every other trigger. |
| `window_minutes` | integer or null | Window of an `event_frequency` rule in minutes. `null` for every other trigger. |
| `cooldown_minutes` | integer | Cooldown window of `event_frequency` notifications for the same issue. The other triggers ignore it, but it is stored and validated for every rule. Default `60`. |
| `channel` | string | Delivery channel: `webhook`, `discord` or `slack`. |
| `target_url` | string | Absolute http(s) URL the notification is posted to. Treat it as a secret. |
| `is_enabled` | boolean | Whether the rule is enabled. Disabled rules do not match new changes and are skipped by the frequency evaluator; notifications already queued are still delivered. |
| `created_at` | timestamp | Creation time. |
| `updated_at` | timestamp | Time of the last change. |

The trigger decides when a rule fires:

- `new_issue` fires when an event batch creates an issue. The notification is
  queued once per rule and issue.
- `regression` fires when a resolved issue reopens, once per rule, issue and
  regression count.
- `event_frequency` is evaluated periodically: when an issue accumulates at
  least `threshold` events inside the last `window_minutes`, a notification is
  queued. The cooldown dedupe key allows at most one notification per issue
  per `cooldown_minutes` window.

`min_level` and `environment` filter all three triggers. `new_issue` and
`regression` notifications are queued in the same database transaction that
stores the events, so a rolled-back or retried batch never leaves a
notification behind. `event_frequency` notifications are queued by the
evaluation worker.

### List alert rules

```http
GET /api/manage/projects/{projectId}/alert-rules
```

Returns every rule of the project ordered by id:

```json
{
  "items": [
    {
      "id": 3,
      "project_id": 2,
      "name": "errors in production",
      "trigger": "new_issue",
      "min_level": "error",
      "environment": "production",
      "threshold": null,
      "window_minutes": null,
      "cooldown_minutes": 60,
      "channel": "webhook",
      "target_url": "https://alerts.example.com/hook",
      "is_enabled": true,
      "created_at": "2026-11-15T10:00:00+00:00",
      "updated_at": "2026-11-15T10:00:00+00:00"
    }
  ]
}
```

`404` when the project does not exist; a project without rules returns an
empty `items` array.

### Create an alert rule

```http
POST /api/manage/projects/{projectId}/alert-rules
Content-Type: application/json
```

| Field | Type | Description |
| --- | --- | --- |
| `name` | string | Required. Trimmed; 1 to 100 characters. |
| `trigger` | string | Required. `new_issue`, `regression` or `event_frequency`. Trimmed and lowercased. |
| `min_level` | string or null, optional | `debug`, `info`, `warning`, `error` or `fatal`. A missing or blank value means any level. |
| `environment` | string or null, optional | Environment to match. A missing or blank value means any environment. |
| `threshold` | integer or null, optional | Required for `event_frequency`, at least `1`. Must be omitted or `null` for every other trigger. |
| `window_minutes` | integer or null, optional | Required for `event_frequency`, from `1` to `1440`. Must be omitted or `null` for every other trigger. |
| `cooldown_minutes` | integer, optional | From `1` to `10080`; defaults to `60`. |
| `channel` | string | Required. `webhook`, `discord` or `slack`. Trimmed and lowercased. |
| `target_url` | string | Required. Absolute `http` or `https` URL of at most 2048 characters. |
| `is_enabled` | boolean, optional | Defaults to `true`. |

```json
{
  "name": "frequent errors",
  "trigger": "event_frequency",
  "min_level": "error",
  "environment": "production",
  "threshold": 25,
  "window_minutes": 10,
  "cooldown_minutes": 120,
  "channel": "slack",
  "target_url": "https://hooks.slack.com/services/example"
}
```

Returns `201 Created` with the [alert rule object](#alert-rule-object).
`400 Bad Request` for a blank or over-long name, an unknown trigger, channel or
level, a missing or non-http(s) target URL, a target URL over 2048 characters,
a `cooldown_minutes` outside 1–10080, an `event_frequency` rule without a
threshold or window, a `threshold` below 1, a `window_minutes` outside 1–1440,
or a non-frequency rule that sends a threshold or window. `404` when the
project does not exist.

```bash
curl -s -X POST "http://localhost:5090/api/manage/projects/2/alert-rules" \
  -H "Authorization: Bearer dev-admin-token" \
  -H "Content-Type: application/json" \
  --data-binary '{"name":"new issues","trigger":"new_issue","channel":"webhook","target_url":"https://alerts.example.com/hook"}'
```

### Update an alert rule

```http
PATCH /api/manage/alert-rules/{ruleId}
Content-Type: application/json
```

The body accepts the same fields as create, all optional. Omitted fields keep
their stored value; an explicit `null` clears `min_level`, `environment`,
`threshold` or `window_minutes`. The merged rule is validated with the rules
above, so changing the trigger to `event_frequency` requires `threshold` and
`window_minutes`, and changing it away from `event_frequency` requires setting
both to `null`. `updated_at` always moves, even when the patch repeats the
stored values.

```json
{ "name": "frequent errors", "cooldown_minutes": 30, "is_enabled": false }
```

Returns `200 OK` with the updated [alert rule object](#alert-rule-object).
`400` for an invalid merged rule or a malformed body; `404` when the rule does
not exist.

```bash
curl -s -X PATCH "http://localhost:5090/api/manage/alert-rules/3" \
  -H "Authorization: Bearer dev-admin-token" \
  -H "Content-Type: application/json" \
  --data-binary '{"is_enabled":false}'
```

### Delete an alert rule

```http
DELETE /api/manage/alert-rules/{ruleId}
```

Deletes the rule and, by cascade, the notifications queued for it. Returns
`204 No Content`, or `404` when the rule does not exist.

```bash
curl -s -X DELETE "http://localhost:5090/api/manage/alert-rules/3" \
  -H "Authorization: Bearer dev-admin-token"
```

### Test an alert rule

```http
POST /api/manage/alert-rules/{ruleId}/test
```

Sends one sample notification to the rule's `channel` and `target_url`
immediately. The sample uses the trigger value `test`, a title of
`Test alert from glitch-port`, the rule's `environment` and its `min_level`
(defaulting to `error` when the rule has no minimum). It is built in memory and
never queued, so it does not appear in
[alert notifications](#list-alert-notifications) and does not affect the
rule's cooldown.

The response is always `200 OK`, even when delivery fails, because the endpoint
reports the attempt:

```json
{ "delivered": true, "status_code": 200, "error": null }
```

| Field | Type | Description |
| --- | --- | --- |
| `delivered` | boolean | Whether the target answered with a 2xx status code. |
| `status_code` | integer or null | HTTP status code, or `null` when the request failed before a response. |
| `error` | string or null | Failure text, or `null` when the notification was delivered. An HTTP failure stores the status code as text. |

`404` when the rule or its project does not exist.

```bash
curl -s -X POST "http://localhost:5090/api/manage/alert-rules/3/test" \
  -H "Authorization: Bearer dev-admin-token"
```

### List alert notifications

```http
GET /api/manage/projects/{projectId}/alert-notifications
```

Returns the newest queued, delivered and failed notifications of the project,
ordered by `created_at` and id descending:

| Parameter | Default | Description |
| --- | --- | --- |
| `limit` | `50` | Page size from 1 to 100. |

```json
{
  "items": [
    {
      "id": 17,
      "rule_id": 3,
      "rule_name": "errors in production",
      "issue_id": 42,
      "trigger": "new_issue",
      "status": "sent",
      "attempts": 1,
      "last_error": null,
      "created_at": "2026-11-14T12:05:00+00:00",
      "sent_at": "2026-11-14T12:05:01+00:00"
    }
  ]
}
```

| Field | Type | Description |
| --- | --- | --- |
| `id` | integer | Notification id. |
| `rule_id` | integer | Rule that matched. |
| `rule_name` | string | Name of that rule. |
| `issue_id` | integer or null | Issue of the notification, or `null` after the issue was deleted. |
| `trigger` | string | Trigger that produced it: `new_issue`, `regression` or `event_frequency`. Test deliveries are never listed. |
| `status` | string | `pending`, `sending`, `sent` or `failed`. |
| `attempts` | integer | Delivery attempts so far. |
| `last_error` | string or null | Failure text of the last attempt, at most 500 characters, or `null`. |
| `created_at` | timestamp | Queue time. |
| `sent_at` | timestamp or null | Time the target accepted the notification, or `null` while it is unsent. |

A 2xx response marks a notification `sent`. Any other result, including a
network failure, retries it with exponential backoff while `attempts` is below
`Processor:Alerts:MaxAttempts` (a `429` `Retry-After` is honoured) and marks it
`failed` afterwards. The webhook body is the stored payload, for example:

```json
{
  "trigger": "new_issue",
  "project": { "id": 2, "name": "Star Raiders", "slug": "star-raiders" },
  "issue": {
    "id": 42,
    "title": "System.NullReferenceException: Object reference not set to an instance of an object",
    "culprit": "Assembly-CSharp.Weapon.Fire",
    "level": "error",
    "status": "unresolved",
    "event_count": 2,
    "first_seen": "2026-11-14T12:00:00+00:00",
    "last_seen": "2026-11-14T12:05:00+00:00",
    "first_release": "star-raiders@1.0.0",
    "last_release": "star-raiders@1.2.3",
    "environment": "production"
  },
  "rule": { "id": 3, "name": "errors in production" },
  "url": "http://localhost:5173#/issues/42"
}
```

Discord and Slack use the same payload to build the request body of those
services; `url` is `null` when `Processor:Alerts:DashboardBaseUrl` is empty.
Keep `target_url` secret: Slack and Discord webhook URLs embed their
credentials.

`404` when the project does not exist; `400` for a `limit` outside 1–100.

```bash
curl -s "http://localhost:5090/api/manage/projects/2/alert-notifications?limit=50" \
  -H "Authorization: Bearer dev-admin-token"
```

## Error reference

| Status | When |
| --- | --- |
| `400 Bad Request` | Unknown `status`, `level` or `sort`; `limit` outside 1–100 (1–25 for issue tags and 1–100 for releases and alert notifications); malformed `cursor`; `period` other than `24h` or `14d`; `regressed` other than `true`; invalid `resolve_in`; empty or oversized bulk `ids`; unknown bulk `action`; missing or blank label names; blank label `name`/`color`; blank project `name` or `slug`; a `platform` member in a project create or update; an invalid `inbound_filters` object, array item or item count; negative `rate_limit_per_minute`; invalid `is_active`; invalid alert rule fields or trigger/threshold combinations; malformed JSON. |
| `401 Unauthorized` | Missing, malformed or wrong `Authorization: Bearer` token. |
| `404 Not Found` | Unknown project, issue, event, key, label or alert rule id, including a non-numeric project, issue, key, label or rule id path and a non-UUID event id path. |
| `409 Conflict` | A project `slug` that is already in use, creating a label whose name already exists in the project, or renaming a label to an existing name. |

## See also

- [Event ingest API](http-api-events.md)
