# Dashboard

The glitch-port dashboard is the web UI for triaging the errors and crashes
collected by the platform. It lists grouped **issues**, shows every event
recorded for an issue and lets you resolve, ignore, label or delete them. A
secondary **messages** screen keeps the raw ingest-queue view that predates
issue grouping.

The dashboard is a React 19 single-page application built with Vite and written
in TypeScript. It adds no runtime dependencies beyond `react` and `react-dom`:
there is no router, UI kit, state manager or chart library. Navigation is a
small `location.hash` router in `src/router.ts`, the 24-hour chart is drawn
with plain elements, and the API client is a thin `fetch` wrapper in
`src/api/client.ts`. The screens only talk to the collector Processor's
Management API.

## Running the dashboard

Prerequisites:

- Node.js 22 with npm.
- A running Processor that exposes the Management API on
  <http://localhost:5090>. The local development Processor is described in the
  repository README; it needs PostgreSQL, which `docker-compose.yml` provides.

From the repository root:

```bash
cd dashboard
npm ci
npm run dev
```

`npm ci` installs the exact versions from `package-lock.json`; run it on a
fresh checkout or after a dependency change. `npm run dev` starts the Vite dev
server on <http://localhost:5173>. The port is pinned in `vite.config.ts`, and
the Processor's development settings allow cross-origin requests from
`http://localhost:5173` through the `Processor:CorsOrigins` setting in
`collector/src/GlitchPort.Processor/appsettings.Development.json`. Keep the two
values in sync if you change the dev-server port.

The other npm scripts are run from the same `dashboard/` directory:

| Command           | Description                                                                 |
| ----------------- | --------------------------------------------------------------------------- |
| `npm test`        | Runs the Vitest suite once (`vitest run`) with Testing Library and jsdom.   |
| `npm run build`   | Type-checks with `tsc -b` and writes the production bundle to `dist/`.       |
| `npm run preview` | Serves the built `dist/` bundle locally (Vite preview, port 4173 by default). |

The tests mock `fetch`, so they do not need a running Processor. `npm run
build` must pass before a change is considered complete. Because routing is
hash-based, `dist/` can be served by any static file server without an SPA
fallback rewrite; the API base and token for a production build are baked in
from the `VITE_*` variables at build time.

## Configuration

The dashboard reads three Vite environment variables. Set them in the shell, in
`dashboard/.env.local` (not committed), or in the environment of the build
pipeline. Values are read when the dev server starts or when the bundle is
built, so restart the dev server after changing them.

| Variable           | Default                 | Meaning                                                                                                                                                                                                         |
| ------------------ | ----------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `VITE_API_BASE`    | `http://localhost:5090` | Base URL of the Processor. The client appends `/api/manage`. A trailing slash is removed. An empty value keeps every request relative to the dashboard origin, which lets a same-origin gateway forward them.     |
| `VITE_ADMIN_TOKEN` | `dev-admin-token`       | Bearer token for the Management API. The client sends `Authorization: Bearer <token>`. An empty value omits the header entirely so a same-origin authenticated gateway can supply authentication.                   |
| `VITE_PROJECT_ID`  | `1`                     | Project id used by the project-scoped endpoints and shown in the top bar. An empty or non-integer value falls back to `1`.                                                                                        |

Example `dashboard/.env.local`:

```dotenv
VITE_API_BASE=http://localhost:5090
VITE_ADMIN_TOKEN=dev-admin-token
VITE_PROJECT_ID=1
```

The local development admin token is `dev-admin-token` and must match
`Processor:AdminToken` in the Processor configuration. When the dashboard is
deployed behind an authenticating gateway, set both `VITE_API_BASE` and
`VITE_ADMIN_TOKEN` to empty strings: requests then go to
`/api/manage/...` on the dashboard's own origin without an `Authorization`
header.

## Routes

The dashboard uses `location.hash`, so every screen has a bookmarkable URL and
the server only ever serves `index.html`.

| Route             | Screen          | Notes                                                                 |
| ----------------- | --------------- | --------------------------------------------------------------------- |
| `#/`              | Issues list     | Redirected to `#/issues` with `history.replaceState`, so it adds no history entry. |
| `#/issues`        | Issues list     | Default screen. Supports filter query parameters (see below).         |
| `#/issues/<id>`   | Issue detail    | `<id>` is the numeric issue id. `#/issues/not-a-number` is not a detail route. |
| `#/messages`      | Messages        | Legacy queue view.                                                    |

Unknown hashes (for example `#/projects`) also fall back to the issues list
and are replaced with `#/issues`. The Issues and Messages tabs in the top bar
change the hash, so browser back and forward navigation work normally.

### Issue-list filters in the URL

The issue list keeps its filter state in the hash query string. A refresh, a
bookmark or a shared link therefore restores the same view. For example:

```text
#/issues?status=resolved&q=null
```

The recognised keys are `status`, `q`, `level`, `environment`, `release`,
`label` and `sort`. Empty values and the defaults (`status=unresolved` and
`sort=last_seen`) are omitted to keep URLs short, so a clean list is simply
`#/issues`. Changing a filter replaces the current history entry instead of
pushing a new one; the pagination cursor is never stored in the URL, so a
refresh restarts at the first page while keeping the filters.

## Screens

### Issues list (`#/issues`)

The main screen lists issue groups matching the current filters.

- **Status tabs** — Unresolved (default), Resolved, Ignored and All map to the
  `status` query parameter.
- **Search** — the title search box is debounced by 300 ms and sends `q`. The
  value is trimmed before it reaches the URL and the API.
- **Filters** — level (debug, info, warning, error, fatal or all),
  environment, release and label name. The environment and release options are
  collected from the currently loaded rows (the active value is always kept);
  the label options come from `GET /api/manage/projects/{projectId}/labels`.
  If that request fails, the list still works with an "All labels" option.
- **Sorting** — Last seen (default), First seen or Event count, sent as
  `last_seen`, `first_seen` or `count`.
- **24-hour summary** — a bar chart of `GET
  /api/manage/projects/{projectId}/stats?period=24h` plus the total event count
  for the window. Bars are scaled relative to the busiest bucket. The summary
  is decorative: when it fails to load it is simply not rendered and the list
  continues to work.
- **Issue table** — one row per issue with a selection checkbox, a title link
  to `#/issues/<id>`, culprit, level badge, label chips, event count, last-seen
  and first-seen relative times (the absolute local time is in the tooltip) and
  the status badge. The page size is 25.
- **Load more** — a cursor-based button that appends the next page using the
  `next_cursor` value returned by the API. It is hidden when there is no next
  page. A failed subsequent page shows an error without discarding the rows
  already loaded.
- **Bulk actions** — selecting one or more rows adds a bulk action bar with
  **Resolve**, **Unresolve**, **Ignore**, **Add label**, **Remove label** and
  **Delete**. Add and Remove label use the entered label name and are disabled
  while it is empty. Delete asks for confirmation first
  (`Delete N selected issues?`). Actions call
  `POST /api/manage/issues/bulk`, clear the selection and refetch the first
  page. Select all applies to the rows currently loaded.

### Issue detail (`#/issues/<id>`)

The detail screen loads the issue, its first page of events and the project
label list in parallel.

- **Header** — title and a "Back to issues" link, culprit, level, status, first
  and last seen times, event count, last release and last environment.
- **Status actions** — **Resolve**, **Unresolve** and **Ignore** call
  `PATCH /api/manage/issues/{id}` with the new `status`. The buttons stay
  available regardless of the current status so a stale header can never trap
  the user in one state.
- **Labels** — current labels are shown as chips. Every project label has a
  checkbox, and the "New label names" field accepts comma-separated names that
  do not exist yet. **Save labels** sends the complete selected name list in a
  single `PATCH`; the Management API creates missing label names and returns
  all labels attached to the issue. The response updates the chips and the
  checkbox source.
- **Delete issue** — asks for confirmation (`Delete this issue and all of its
  events?`), calls `DELETE /api/manage/issues/{id}` and navigates back to
  `#/issues`.
- **Initial event** — the selected event starts as the issue's `latest_event`
  from `GET /api/manage/issues/{id}`. When it is `null`, the panel shows "No
  events recorded."
- **Event list** — events are listed newest first (page size 25), each with its
  message (or `<no message>`), relative time, level, environment and release.
  Selecting an event fetches `GET /api/manage/events/{eventId}` and replaces
  the panel. The **More events** button (the event list's Load more) appends
  the next page with the returned `next_cursor`. **Newer** and **Older** move
  the selection through the events already loaded and are disabled at either
  end.
- **Delete event** — asks for confirmation, calls
  `DELETE /api/manage/events/{eventId}`, then reloads the issue and the first
  event page and selects the new `latest_event`.

The selected event body is rendered in this order:

1. **Exceptions** — one block per exception, displayed **outermost first**:
   the API returns the `exception` array from the innermost cause to the
   outermost exception, and the dashboard reverses it. Each block shows
   `type: value`.
2. **Stack frames** — displayed **throw site first**: the API stores frames
   from the outermost caller to the deepest callee (the last frame is where the
   exception was thrown), and the dashboard reverses them. Frames with
   `in_app: true` are visually emphasised. Frames with `in_app: false` are
   collapsed by default behind a `Show N system frames` toggle (and expanded
   with `Hide N system frames`). A frame without an `in_app` flag is shown as a
   normal application frame without the emphasis. Each frame shows
   `module.function`, `filename:lineno` and its `context_line` when present.
3. **Message** — the payload `message`, shown whenever it is non-empty (also
   when exceptions are present).
4. **Tags** — a key/value table.
5. **User** — ID, username, email and IP address rows that are present.
6. **Contexts** — one key/value table per context name; nested values are
   shown as pretty-printed JSON.
7. **Breadcrumbs** — a timeline with timestamp, category, level, message and an
   expandable `data` object.
8. **SDK** — `name@version` (or the part that is available).
9. **Extra** — a key/value table.
10. **Raw JSON** — a collapsed `<details>` element with the pretty-printed
    `payload`; it is always available.

Sections with no data are omitted.

### Messages (`#/messages`)

The messages screen is the original queue view. It loads
`GET /api/manage/messages?limit=100` and renders a table with **Received at**
(absolute local time, `YYYY-MM-DD HH:mm:ss`), **Endpoint** and **Message**. A
**Refresh** button reloads the list; an empty response shows "No messages yet."

## Error handling

Every Management API call goes through the shared client in
`src/api/client.ts`. A non-2xx response throws an `ApiError` with:

| Field     | Meaning                                                                    |
| --------- | -------------------------------------------------------------------------- |
| `status`  | HTTP status code, for example `401` or `404`.                              |
| `message` | `HTTP <status>`, extended to `HTTP <status>: <error>` when the JSON body contains a string `error`. |
| `body`    | Parsed JSON error body, or `undefined` for an empty/204 response.          |

Network failures are not wrapped and propagate unchanged (for example the
browser's `TypeError: Failed to fetch`). Screens render a loading state, an
empty state or an error state with the failed action as the prefix, such as
`Failed to load issues: HTTP 401: unauthorized`.

A **401** error also renders this hint:

```text
Set VITE_ADMIN_TOKEN to an admin token accepted by the collector.
```

Set `VITE_ADMIN_TOKEN` to the value of the Processor's `Processor:AdminToken`
and restart the dev server (or rebuild for production). If the dashboard is
served behind a same-origin authenticated gateway, leave `VITE_ADMIN_TOKEN`
empty instead so the gateway can add its own authentication. The 24-hour stats
and the optional label list are best-effort: their failures do not produce an
error state on the issues list.

## Management API

The dashboard consumes the Processor's Management API:

- **Base path** — `<VITE_API_BASE>/api/manage`. With the default configuration
  that is `http://localhost:5090/api/manage`.
- **Authentication** — every request carries
  `Authorization: Bearer <VITE_ADMIN_TOKEN>` unless the token is empty. Write
  requests add `Content-Type: application/json`.
- **Encoding** — JSON with `snake_case` field names. Timestamps are UTC in
  ISO 8601 format (for example `2026-01-02T03:04:05Z`). Issue and label ids are
  numbers; event ids are UUID strings.
- **Errors** — a JSON body of the shape `{ "error": "<message>" }`, with status
  `400` for an invalid request, `401` for a missing or wrong token, `404` for
  an unknown issue, event or label and `409` for a duplicate label name.

For example, to list unresolved issues directly:

```bash
curl -H "Authorization: Bearer dev-admin-token" \
  "http://localhost:5090/api/manage/projects/1/issues?status=unresolved&limit=25"
```

### Endpoints

Paths below are relative to `/api/manage`; `{projectId}` is `VITE_PROJECT_ID`.

| Method   | Path                                | Request                                      | Response                                      |
| -------- | ----------------------------------- | -------------------------------------------- | --------------------------------------------- |
| `GET`    | `/projects/{projectId}/issues`      | Issue list query parameters (below)          | `{ items: Issue[], next_cursor: string\|null }` |
| `GET`    | `/issues/{id}`                      | –                                            | `IssueDetail` (`Issue` plus `latest_event`)    |
| `PATCH`  | `/issues/{id}`                      | `{ status?, labels? }`                       | `Issue`                                       |
| `DELETE` | `/issues/{id}`                      | –                                            | `204 No Content`                              |
| `POST`   | `/issues/bulk`                      | `{ ids, action, labels? }`                   | `{ affected: number }`                        |
| `GET`    | `/issues/{id}/events`               | `limit`, `cursor`                            | `{ items: EventSummary[], next_cursor }`      |
| `GET`    | `/events/{eventId}`                 | –                                            | `EventDetail`                                 |
| `DELETE` | `/events/{eventId}`                 | –                                            | `204 No Content`                              |
| `GET`    | `/projects/{projectId}/labels`      | –                                            | `Label[]`                                     |
| `POST`   | `/projects/{projectId}/labels`      | `{ name, color? }`                           | `Label` (`409` when the name already exists)  |
| `PATCH`  | `/labels/{id}`                      | `{ name?, color? }`                          | `Label`                                       |
| `DELETE` | `/labels/{id}`                      | –                                            | `204 No Content`                              |
| `GET`    | `/projects/{projectId}/stats`       | `period=24h` (dashboard) or `14d`            | `StatsBucket[]`                               |
| `GET`    | `/messages`                         | `limit` (dashboard sends `100`)              | `Message[]`                                   |

Statistics use `GET /projects/{projectId}/stats?period=24h|14d`. The dashboard
only requests the 24-hour period; `14d` is available to other clients.

The `PATCH /issues/{id}` body accepts `status` (one of `unresolved`, `resolved`
or `ignored`) and `labels` (the complete list of label **names** to attach).
Names that do not exist are created. The bulk action is one of `resolve`,
`unresolve`, `ignore`, `delete`, `add_labels` or `remove_labels`; the `labels`
array is required for the two label actions.

#### Issue list query parameters

| Parameter     | Default      | Meaning                                                                 |
| ------------- | ------------ | ----------------------------------------------------------------------- |
| `status`      | `unresolved` | `unresolved`, `resolved`, `ignored` or `all`.                           |
| `level`       | –            | `debug`, `info`, `warning`, `error` or `fatal`.                          |
| `environment` | –            | Environment name.                                                        |
| `release`     | –            | Release name.                                                            |
| `label`       | –            | Label name.                                                              |
| `q`           | –            | Title search text.                                                       |
| `sort`        | `last_seen`  | `last_seen`, `first_seen` or `count`.                                    |
| `limit`       | `25`         | Page size from 1 to 100; the dashboard sends 25.                         |
| `cursor`      | –            | Opaque value from the previous response's `next_cursor`.                 |

### Response shapes

Issue list responses wrap their rows as `{ "items": [...], "next_cursor":
... }`. `next_cursor` is `null` on the last page; pass it back unchanged as
`cursor` to fetch the next one. Event lists use the same wrapper. Statistics
are an array of `{ "bucket": "<ISO 8601 timestamp>", "count": <number> }`
objects. A message row from `/messages` is
`{ "id": string, "received_at": string, "endpoint": string, "message": string }`.

#### Issue

| Field              | Type             | Description                                                 |
| ------------------ | ---------------- | ----------------------------------------------------------- |
| `id`               | number           | Numeric issue id.                                           |
| `title`            | string           | Grouping title.                                             |
| `culprit`          | string \| null   | Function, module or location that caused the issue.         |
| `level`            | string           | `debug`, `info`, `warning`, `error` or `fatal`.             |
| `platform`         | string           | Reporting platform, for example `unreal`, `unity` or `dotnet`. |
| `status`           | string           | `unresolved`, `resolved` or `ignored`.                      |
| `first_seen`       | string           | UTC ISO 8601 timestamp of the first event.                  |
| `last_seen`        | string           | UTC ISO 8601 timestamp of the most recent event.            |
| `event_count`      | number           | Number of events grouped into the issue.                    |
| `last_release`     | string \| null   | Release of the most recent event.                           |
| `last_environment` | string \| null   | Environment of the most recent event.                       |
| `labels`           | `Label[]`        | Labels attached to the issue.                               |

`IssueDetail`, returned by `GET /issues/{id}`, is an `Issue` with one extra
field:

| Field          | Type                 | Description                                              |
| -------------- | -------------------- | -------------------------------------------------------- |
| `latest_event` | `EventDetail` \| null | Most recent event of the issue, used as the initial selection. |

#### Label

| Field   | Type   | Description                                      |
| ------- | ------ | ------------------------------------------------ |
| `id`    | number | Numeric label id.                                |
| `name`  | string | Label name, unique within the project.           |
| `color` | string | CSS colour (usually a hex value such as `#ff0000`). |

#### EventSummary

| Field         | Type           | Description                                                 |
| ------------- | -------------- | ----------------------------------------------------------- |
| `id`          | string         | Event UUID.                                                 |
| `timestamp`   | string         | UTC ISO 8601 time the event occurred.                       |
| `received_at` | string         | UTC ISO 8601 time the Processor received the event.         |
| `level`       | string         | `debug`, `info`, `warning`, `error` or `fatal`.             |
| `environment` | string \| null | Environment name.                                           |
| `release`     | string \| null | Release name.                                               |
| `user_id`     | string \| null | User identifier, when the event carried one.                |
| `message`     | string \| null | Event message, shown as `<no message>` when absent.         |

#### EventDetail

| Field         | Type           | Description                                                 |
| ------------- | -------------- | ----------------------------------------------------------- |
| `id`          | string         | Event UUID.                                                 |
| `issue_id`    | number         | Numeric id of the owning issue.                             |
| `timestamp`   | string         | UTC ISO 8601 time the event occurred.                       |
| `received_at` | string         | UTC ISO 8601 time the Processor received the event.         |
| `level`       | string         | `debug`, `info`, `warning`, `error` or `fatal`.             |
| `platform`    | string         | Reporting platform.                                         |
| `environment` | string \| null | Environment name.                                           |
| `release`     | string \| null | Release name.                                               |
| `user_id`     | string \| null | User identifier, when the event carried one.                |
| `endpoint`    | string         | Ingest endpoint that accepted the event.                    |
| `payload`     | object         | Event v1 payload (see below).                               |

#### Event payload

`payload` is the event v1 JSON object. All members are optional; unknown
SDK-specific keys are preserved and shown under Raw JSON.

| Field         | Type                                | Description                                                                    |
| ------------- | ----------------------------------- | ------------------------------------------------------------------------------ |
| `message`     | string \| null                      | Free-form message.                                                             |
| `exception`   | `ExceptionValue[]` \| null          | Exceptions ordered from the innermost cause to the outermost exception.        |
| `tags`        | object (string → string) \| null    | Event tags.                                                                    |
| `user`        | object \| null                      | `{ id, username, email, ip_address }`, each nullable.                          |
| `contexts`    | object \| null                      | Named context objects, for example `runtime`, `device` or `game`.              |
| `breadcrumbs` | `Breadcrumb[]` \| null              | `{ timestamp, category, message, level, type, data }` entries.                 |
| `sdk`         | object \| null                      | `{ name, version }`.                                                           |
| `extra`       | object \| null                      | Additional key/value data.                                                     |

An `ExceptionValue` is `{ type, value?, module?, stacktrace? }` where
`stacktrace.frames` is a `StackFrame[]` ordered from the outermost caller to
the deepest callee; the last frame is the throw site. A `StackFrame` is
`{ function, module, filename, abs_path, lineno, colno, in_app, context_line }`
with nullable members. The dashboard reverses both arrays for display, as
described in [Issue detail](#issue-detail-issuesid).
