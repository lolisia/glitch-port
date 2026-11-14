# Management API

The Management API lets the dashboard and automation read and update the issues,
events and labels the Processor stores. It is served by the Processor under
`/api/manage`; with the default development settings the base URL is
`http://localhost:5090/api/manage`.

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
- Issue and label ids are 64-bit integers. Event ids are UUIDs written as
  lowercase strings, for example `"9f1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e"`.
- Project ids in paths are integers. A project that has no data yet simply
  returns empty results; the API does not validate projects.
- Optional query parameters with an empty value are treated as absent.
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
| `201 Created` | The label was created. |
| `204 No Content` | The resource was deleted. The response has no body. |
| `400 Bad Request` | A query parameter, the JSON body or a cursor is invalid. |
| `401 Unauthorized` | The `Authorization` header is missing or wrong. |
| `404 Not Found` | The issue, event or label does not exist. |
| `409 Conflict` | A label with the same name already exists in the project. |

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/projects/{projectId}/issues` | List issues with filters, sorting and cursor paging. |
| `GET` | `/issues/{issueId}` | Read one issue with its labels and latest event. |
| `PATCH` | `/issues/{issueId}` | Change the status and/or replace the label set. |
| `DELETE` | `/issues/{issueId}` | Delete an issue and its events. |
| `POST` | `/issues/bulk` | Apply a bulk action to up to 500 issues. |
| `GET` | `/issues/{issueId}/events` | List the events of an issue, newest first. |
| `GET` | `/events/{eventId}` | Read one event including its payload. |
| `DELETE` | `/events/{eventId}` | Delete one event and decrement the issue counter. |
| `GET` | `/projects/{projectId}/labels` | List the labels of a project. |
| `POST` | `/projects/{projectId}/labels` | Create a label. |
| `PATCH` | `/labels/{labelId}` | Change a label name and/or color. |
| `DELETE` | `/labels/{labelId}` | Delete a label and its assignments. |
| `GET` | `/projects/{projectId}/stats` | Count events per UTC time bucket. |

The legacy `GET /api/manage/messages` endpoint (limit 1–500) remains available
for clients that only need the message list; it is not part of the issue API.

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
| `last_release` | string or null | Release of the most recent event. |
| `last_environment` | string or null | Environment of the most recent event. |
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
  "last_release": "star-raiders@1.2.3",
  "last_environment": "production",
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
| `label` | none | Only issues carrying the label with this exact name. |
| `q` | none | Case-insensitive substring match on the issue title. `%`, `_` and `\` are matched literally. |
| `sort` | `last_seen` | `last_seen`, `first_seen` or `count`, all descending with `id` as the tie-breaker. |
| `limit` | `25` | Page size from 1 to 100. |
| `cursor` | none | `next_cursor` of the previous page. |

Unknown `status`, `level` or `sort` values, a `limit` outside 1–100 and a
malformed cursor return `400`.

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
      "last_release": "star-raiders@1.2.3",
      "last_environment": "production",
      "labels": []
    }
  ],
  "next_cursor": "WyIyMDI2LTExLTE0VDEyOjA1OjAwLjAwMDAwMDArMDA6MDAiLCI0MiJd"
}
```

`next_cursor` is `null` when the page is the last one. An empty project returns
`{"items": [], "next_cursor": null}`.

```bash
curl -s "http://localhost:5090/api/manage/projects/1/issues?status=all&sort=count&limit=25" \
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
  "last_release": "star-raiders@1.2.3",
  "last_environment": "production",
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

## Update an issue

```http
PATCH /api/manage/issues/{issueId}
Content-Type: application/json
```

| Field | Type | Description |
| --- | --- | --- |
| `status` | string, optional | New status: `unresolved`, `resolved` or `ignored`. |
| `labels` | array of strings, optional | Replaces the complete label set of the issue. Names that do not exist in the project are created with the default color `#888888`. An empty array clears all labels. |

Both fields are optional; omitted fields are left unchanged. Whitespace around
statuses and label names is trimmed, and duplicate label names are collapsed.
Blank label names and unknown statuses return `400`.

```json
{ "status": "resolved", "labels": ["regression", "crash"] }
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
  "last_release": "star-raiders@1.2.3",
  "last_environment": "production",
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
  --data-binary '{"status":"resolved"}'
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
| `action` | string | `resolve`, `unresolve`, `ignore`, `delete`, `add_labels` or `remove_labels`. |
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
| `resolve` | Sets `status` to `resolved`. | Number of issues updated. |
| `unresolve` | Sets `status` to `unresolved`. | Number of issues updated. |
| `ignore` | Sets `status` to `ignored`. | Number of issues updated. |
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

Each bucket is `{ "bucket": <start of the bucket in UTC>, "count": <events> }`:

```json
[
  { "bucket": "2026-11-14T10:00:00+00:00", "count": 0 },
  { "bucket": "2026-11-14T11:00:00+00:00", "count": 3 },
  { "bucket": "2026-11-14T12:00:00+00:00", "count": 1 }
]
```

Any other `period` value returns `400`.

```bash
curl -s "http://localhost:5090/api/manage/projects/1/stats?period=14d" \
  -H "Authorization: Bearer dev-admin-token"
```

## Error reference

| Status | When |
| --- | --- |
| `400 Bad Request` | Unknown `status`, `level` or `sort`; `limit` outside 1–100; malformed `cursor`; `period` other than `24h` or `14d`; empty or oversized bulk `ids`; unknown bulk `action`; missing or blank label names; blank label `name`/`color`; malformed JSON. |
| `401 Unauthorized` | Missing, malformed or wrong `Authorization: Bearer` token. |
| `404 Not Found` | Unknown issue id, event id or label id, including a non-numeric issue or label id path and a non-UUID event id path. |
| `409 Conflict` | Creating a label whose name already exists in the project, or renaming a label to an existing name. |

## See also

- [Event ingest API](http-api-events.md)
