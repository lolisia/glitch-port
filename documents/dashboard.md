# Dashboard

The glitch-port dashboard is the web UI for triaging the errors and crashes
collected by the platform. It manages projects and their public ingest keys,
lists grouped **issues**, shows every event recorded for an issue and lets you
resolve, ignore, label or delete them. A secondary **messages** screen keeps the
raw ingest-queue view that predates issue grouping.

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
| `VITE_PROJECT_ID`  | `1`                     | Initial active project id when the browser has no stored selection. The top-bar **Project** selector persists the chosen id in `localStorage` under `gp.projectId`, and that stored value wins on later visits. An empty, missing or invalid value falls back to the first project (project 1 in a fresh database). |

The active project is a per-browser setting, not a build-time one. On startup
the dashboard resolves it in this order: the id stored in `localStorage` under
`gp.projectId`, then `VITE_PROJECT_ID`, then the first project returned by
`GET /api/manage/projects`. Choosing another project in the top bar writes
`gp.projectId` and reloads the project-scoped screens. When the active project
is deleted, the next project-list reload drops the stale id and falls back to
the first remaining project, or to a "No project selected" state when none is
left.

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

## Theme

The **Theme** select in the top bar chooses how the dashboard is coloured:

| Preference        | Meaning                                                                 |
| ----------------- | ----------------------------------------------------------------------- |
| System (default)  | Follow the operating system/browser `prefers-color-scheme` setting.      |
| Light             | Always use the light palette.                                            |
| Dark              | Always use the dark palette.                                             |

The preference is a per-browser setting stored in `localStorage` under
`glitchport.theme`. Only `system`, `light` and `dark` are accepted; a missing
or invalid value falls back to `system`. `src/theme/ThemeProvider.tsx` applies
the resolved theme by setting `data-theme="light"` or `data-theme="dark"` and
`color-scheme` on the `<html>` element. While the preference is `system` it
subscribes to `matchMedia('(prefers-color-scheme: dark)')` change events and
removes the listener when an explicit preference is selected. When
`matchMedia` is unavailable the dashboard resolves to light.

A small inline script in `dashboard/index.html` reads the same key and applies
the theme before the bundle loads, so reloading in dark mode does not flash the
light theme.

All colours are defined as CSS custom properties in `src/index.css`: `:root`
holds the light tokens (`--color-bg`, `--color-bg-subtle`, `--color-surface`,
`--color-fg`, `--color-fg-muted`, `--color-border`, `--color-accent`,
`--color-danger`, `--color-warning`, `--color-success`, `--color-code-bg`,
`--color-stack-bg` and supporting tokens) and `:root[data-theme='dark']`
overrides them for dark mode. The screen styles only reference the variables,
so a new colour must be added as a token instead of being hard-coded. Label
chips compute black or white text from the label colour's relative luminance,
which keeps them readable against any label colour in either theme.

## Language

The **Language** select in the top bar switches the dashboard between English
and Korean:

| Language | Code | Notes                                                          |
| -------- | ---- | -------------------------------------------------------------- |
| English  | `en` | Default and source language. Timestamps read `YYYY-MM-DD HH:mm:ss`. |
| Korean   | `ko` | Translated shell, labels and screens; Korean date layout.      |

The choice is a per-browser setting stored in `localStorage` under
`glitchport.locale`. Only `en` and `ko` are accepted. On startup the dashboard
resolves the active locale in this order:

1. a valid `glitchport.locale` value,
2. `navigator.language` starting with `ko` (for example `ko` or `ko-KR`), which
   selects Korean,
3. English otherwise.

Choosing a language persists it, re-renders the shell and the visible screen
without a reload, and sets `document.documentElement.lang` to the active locale.
A missing or invalid stored value falls back to the browser language, so a
Korean browser starts in Korean until the user explicitly picks a language from
the selector.

All dashboard-owned text comes from the message dictionaries in
`src/i18n/messages/`: `en.ts` is the English source object and `ko.ts` must
define the same keys, which the `Messages` type and a key-parity test enforce.
The dictionaries are split into per-screen modules (`messages/en/issues.ts`,
`messages/en/event.ts` and `messages/en/projects.ts` with their `ko`
counterparts) and merged into `en.ts`/`ko.ts`, so a screen only owns the keys
it renders. The navigation tabs, project picker states, loading/empty/error
states, level and status badges and the 401 token guidance follow the active
locale, and so do the issues list and detail screens, the event body and the
project screens: their headings, filter labels and options, table headers,
buttons, confirmations, count summaries and aria-labels/titles are translated.
Counter messages use `_one`/`_other` plural keys with a `{count}` placeholder.
Dates, relative times, clock values and formatted numbers go through
`Intl.DateTimeFormat`, `Intl.RelativeTimeFormat` and `Intl.NumberFormat`, so an
English timestamp stays `2026-01-02 03:04:05` and a Korean one reads
`2026. 01. 02. 03:04:05`. Raw data values (issue ids, project slugs, platform
names, label names, release and environment names) and server-provided API
error text are displayed verbatim and are never translated.

## Routes

The dashboard uses `location.hash`, so every screen has a bookmarkable URL and
the server only ever serves `index.html`.

| Route             | Screen          | Notes                                                                 |
| ----------------- | --------------- | --------------------------------------------------------------------- |
| `#/`              | Issues list     | Redirected to `#/issues` with `history.replaceState`, so it adds no history entry. |
| `#/issues`        | Issues list     | Default screen. Supports filter query parameters (see below).         |
| `#/issues/<id>`   | Issue detail    | `<id>` is the numeric issue id. `#/issues/not-a-number` is not a detail route. |
| `#/messages`      | Messages        | Legacy queue view.                                                    |
| `#/projects`      | Projects list   | Project and key management entry point; hosts the create form.        |
| `#/projects/<id>` | Project detail  | `<id>` is the numeric project id; `<id>` must be a positive integer.  |

Unknown hashes (for example `#/nope`) fall back to the issues list and are
replaced with `#/issues`. The Issues, Messages and Projects tabs in the top bar
change the hash, so browser back and forward navigation work normally.

### Issue-list filters in the URL

The issue list keeps its filter state in the hash query string. A refresh, a
bookmark or a shared link therefore restores the same view. For example:

```text
#/issues?status=resolved&q=null
```

The recognised keys are `status`, `q`, `level`, `environment`, `release`,
`label`, `platform` and `sort`. Empty values and the defaults
(`status=unresolved` and `sort=last_seen`) are omitted to keep URLs short, so a
clean list is simply `#/issues`. An empty platform selection is omitted too; a
non-empty one is stored as a single comma-separated value, for example
`#/issues?platform=unity%2Cdotnet` (the comma is percent-encoded in the raw
hash and read back as `unity,dotnet`). Changing a filter replaces the current
history entry instead of pushing a new one; the pagination cursor is never
stored in the URL, so a refresh restarts at the first page while keeping the
filters.

## Screens

### Issues list (`#/issues`)

The main screen lists issue groups matching the current filters. Every label on
this screen follows the selected language: the status tabs, filter names and
options, table headers, the 24-hour summary, the bulk bar and its confirmation,
and the empty/error prefixes. Issue titles, culprits, labels, platform names and
the counts themselves stay as returned by the API.

- **Status tabs** — Unresolved (default), Resolved, Ignored and All map to the
  `status` query parameter.
- **Search** — the title search box is debounced by 300 ms and sends `q`. The
  value is trimmed before it reaches the URL and the API.
- **Filters** — level (debug, info, warning, error, fatal or all),
  environment, release and label name. The environment and release options are
  collected from the currently loaded rows (the active value is always kept);
  the label options come from `GET /api/manage/projects/{projectId}/labels`.
  If that request fails, the list still works with an "All labels" option.
- **Platform filter** — a multi-select whose options are the active project's
  observed `platforms`, loaded with `GET /api/manage/projects/{projectId}`.
  Selecting one or more platforms writes a single comma-separated `platform`
  hash value and sends `platform=<names>` to the API. The control is hidden
  when the project has not observed any platform yet or when that request
  fails.
- **Sorting** — Last seen (default), First seen or Event count, sent as
  `last_seen`, `first_seen` or `count`.
- **24-hour summary** — a bar chart of the `buckets` returned by `GET
  /api/manage/projects/{projectId}/stats?period=24h` plus the total event count
  for the window. Bars are scaled relative to the busiest bucket, and a
  breakdown lists the all-time issue count of every platform in `by_platform`
  (hidden when the array is empty). The summary is decorative: when it fails
  to load it is simply not rendered and the list continues to work.
- **Issue table** — one row per issue with a selection checkbox, a title link
  to `#/issues/<id>`, culprit, level badge, platform chip, label chips, event
  count, last-seen and first-seen relative times (the absolute local time is in
  the tooltip) and the status badge. The page size is 25.
- **Load more** — a cursor-based button that appends the next page using the
  `next_cursor` value returned by the API. It is hidden when there is no next
  page. A failed subsequent page shows an error without discarding the rows
  already loaded.
- **Bulk actions** — selecting one or more rows adds a bulk action bar with
  **Resolve**, **Unresolve**, **Ignore**, **Add label**, **Remove label** and
  **Delete**. Add and Remove label use the entered label name and are disabled
  while it is empty. Delete asks for confirmation first
  (`Delete N selected issues?`, localized in Korean). Actions call
  `POST /api/manage/issues/bulk`, clear the selection and refetch the first
  page. Select all applies to the rows currently loaded.

### Issue detail (`#/issues/<id>`)

The detail screen loads the issue, its first page of events and the project
label list in parallel. Headings, metadata labels, action buttons,
confirmation prompts, label controls and the event list/panel headings follow
the selected language; raw issue fields and event payload values are shown
unchanged.

- **Header** — title and a "Back to issues" link, culprit, level, platform,
  status, first and last seen times, event count, last release and last
  environment.
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

The selected event body starts with a small facts row showing the event's
platform, followed by these sections (their headings, the key/value table
headers, the system-frame toggle, the `data` summary and the raw JSON summary
are translated; payload values are not):

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

### Projects (`#/projects`)

The project list is the entry point for project and key management. Screen
headings, table headers, buttons, copy feedback and the create form follow the
selected language; project names, slugs and platform names stay as stored.

- **Project table** — one row per project with a link to `#/projects/<id>`, the
  slug, chips for the platforms observed in its issues (an em dash when none
  has been seen yet), the unresolved issue count and the active key count.
  **Refresh** reloads the list; an empty installation shows "No projects yet."
- **Create project** — Name is required. Projects no longer have a platform
  setting, so there is no platform input: platforms are recorded by the events
  themselves. `POST /api/manage/projects` creates the project together with its
  first active key. The page then shows the generated DSN in a read-only field
  with **Copy DSN** and an **Open project** link, and reloads the table.

### Project detail (`#/projects/<id>`)

The detail screen loads `GET /api/manage/projects/{id}` and
`GET /api/manage/projects/{id}/keys` in parallel. Its facts labels, key table
headers, form labels and hints, status labels, buttons, copy feedback and the
danger-zone confirmation follow the selected language; the project name, slug,
key values, DSNs and the created/revoked times are shown as stored/returned.

- **Header and facts** — the project name, a "Back to projects" link, the slug,
  a **Platforms seen** chip list built from the project's `platforms` (an em
  dash when empty), created time, unresolved issue count and active key count.
  When the project does not exist the screen shows "Project not found" with a
  link back to `#/projects`.
- **Edit project** — Name (required). **Save changes** trims the value and
  sends `PATCH /api/manage/projects/{id}` with `name` only when it changed;
  `platform` is not a project setting and is never sent. The API error text is
  shown with the form; a successful save updates the screen and reloads the
  top-bar project list.
- **Keys table** — one row per key, including revoked keys: the public key and
  DSN in monospace, the label, the status (Active, or Revoked with the
  revocation time), an inline rate limit editor, **Copy DSN** and **Revoke**
  (active keys only). The rate limit editor sends `PATCH
  /api/manage/keys/{keyId}` with `{ "rate_limit_per_minute": <value> }`: a
  blank field sends `null` (no per-key limit), `0` means unlimited, and
  negative or non-integer values are rejected client-side without a request.
  **Revoke** calls `DELETE /api/manage/keys/{keyId}`, reloads the list and
  leaves the row in place as revoked. The active key count follows the
  refreshed list.
- **Create key** — an optional label and an optional rate limit. `POST
  /api/manage/projects/{id}/keys` creates the key, then the new DSN is shown
  with a **Copy new key DSN** button and the key list is reloaded.
- **Danger zone** — deleting a project requires typing its exact name; the
  **Delete project** button stays disabled until the text matches. `DELETE
  /api/manage/projects/{id}` removes the project with its issues, events,
  labels and keys, navigates to `#/projects` and reloads the project list. When
  the deleted project was the active one, the selector falls back to the first
  remaining project.

### Messages (`#/messages`)

The messages screen is the original queue view. It loads
`GET /api/manage/messages?limit=100` and renders a table with **Received at**
(absolute local time in the active locale: `YYYY-MM-DD HH:mm:ss` in English,
`YYYY. MM. DD. HH:mm:ss` in Korean), **Endpoint** and **Message**. A
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
  ISO 8601 format (for example `2026-01-02T03:04:05Z`). Issue, label, project
  and key ids are numbers; event ids are UUID strings.
- **Errors** — a JSON body of the shape `{ "error": "<message>" }`, with status
  `400` for an invalid request, `401` for a missing or wrong token, `404` for
  an unknown project, issue, event, key or label and `409` for a duplicate
  project slug or label name.

For example, to list unresolved issues directly:

```bash
curl -H "Authorization: Bearer dev-admin-token" \
  "http://localhost:5090/api/manage/projects/1/issues?status=unresolved&limit=25"
```

### Endpoints

Paths below are relative to `/api/manage`. `{projectId}` is the project being
viewed: the active project selected in the top bar for issue, label and stats
requests, and the project id from the URL for the project and key endpoints.
The dashboard never sends `X-GlitchPort-Key`; game clients use that header with
the public keys listed here.

| Method   | Path                                | Request                                      | Response                                      |
| -------- | ----------------------------------- | -------------------------------------------- | --------------------------------------------- |
| `GET`    | `/projects`                         | –                                            | `ProjectListItem[]`                           |
| `POST`   | `/projects`                         | `{ name, slug? }`                            | `CreatedProject` (`201`)                      |
| `GET`    | `/projects/{projectId}`             | –                                            | `ProjectDetail`                               |
| `PATCH`  | `/projects/{projectId}`             | `{ name? }`                                  | `ProjectDetail`                               |
| `DELETE` | `/projects/{projectId}`             | –                                            | `204 No Content`                              |
| `GET`    | `/projects/{projectId}/keys`        | –                                            | `ProjectKey[]` (includes revoked keys)        |
| `POST`   | `/projects/{projectId}/keys`        | `{ label?, rate_limit_per_minute? }`         | `ProjectKey` (`201`)                          |
| `PATCH`  | `/keys/{keyId}`                     | `{ label?, rate_limit_per_minute?, is_active? }` | `ProjectKey`                              |
| `DELETE` | `/keys/{keyId}`                     | –                                            | `204 No Content` (revokes, keeps the row)     |
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
| `GET`    | `/projects/{projectId}/stats`       | `period=24h` (dashboard) or `14d`            | `{ buckets: StatsBucket[], by_platform: PlatformStats[] }` |
| `GET`    | `/messages`                         | `limit` (dashboard sends `100`)              | `Message[]`                                   |

`PATCH /projects/{projectId}` accepts `name` and leaves omitted fields
unchanged; a blank value returns `400`. Projects have no platform setting:
`platforms` is derived from the project's issues, and sending a `platform`
member in `POST` or `PATCH` returns `400`. The slug cannot be changed. `DELETE
/projects/{projectId}` cascades to issues, events, labels, label assignments
and keys.

`POST /projects/{projectId}/keys` creates an active key with an optional
`label` and `rate_limit_per_minute` (`null`, the default, means no per-key
limit; `0` means unlimited; negative values return `400`). `PATCH
/keys/{keyId}` changes `label`, `rate_limit_per_minute` and/or `is_active`;
send an explicit `rate_limit_per_minute: null` to clear a limit and
`is_active: false` to revoke the key. `DELETE /keys/{keyId}` revokes without
deleting the row, so the key stays in the key list with `revoked_at` set.

Statistics use `GET /projects/{projectId}/stats?period=24h|14d`. The dashboard
only requests the 24-hour period; `14d` is available to other clients. The
response has two parts: `buckets` is the period's array of `{ "bucket": "<ISO
8601 timestamp>", "count": <number> }` objects (including empty buckets), and
`by_platform` is an all-time array of `{ "platform": string, "issue_count":
number, "event_count": number }` ordered by event count descending, then
platform name ascending. `by_platform` is empty when the project has no
issues.

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
| `platform`    | –            | One or more comma-separated platform names, for example `unity,dotnet`.  |
| `q`           | –            | Title search text.                                                       |
| `sort`        | `last_seen`  | `last_seen`, `first_seen` or `count`.                                    |
| `limit`       | `25`         | Page size from 1 to 100; the dashboard sends 25.                         |
| `cursor`      | –            | Opaque value from the previous response's `next_cursor`.                 |

### Response shapes

Issue list responses wrap their rows as `{ "items": [...], "next_cursor":
... }`. `next_cursor` is `null` on the last page; pass it back unchanged as
`cursor` to fetch the next one. Event lists use the same wrapper. Statistics
are `{ "buckets": [...], "by_platform": [...] }` as described under
[Endpoints](#endpoints). A message row from `/messages` is
`{ "id": string, "received_at": string, "endpoint": string, "message": string }`.

#### Project

`GET /projects` returns `ProjectListItem[]`. The detail endpoints (`GET` and
`PATCH /projects/{projectId}`) return `ProjectDetail`, which adds the DSN of the
first active key. `POST /projects` returns `CreatedProject`: the same core
fields and `dsn`, without the counts.

| Field         | Type             | Description                                                              |
| ------------- | ---------------- | ------------------------------------------------------------------------ |
| `id`          | number           | Numeric project id.                                                      |
| `name`        | string           | Display name.                                                            |
| `slug`        | string           | Unique URL-friendly identifier; cannot be changed after creation.        |
| `platforms`   | string[]         | Platforms observed in the project's issues, ordered by event count. Empty when the project has no issues. |
| `created_at`  | string           | UTC ISO 8601 creation timestamp.                                         |
| `issue_count` | number           | Unresolved issues (list and detail responses).                           |
| `key_count`   | number           | Active keys; revoked keys are not counted (list and detail responses).   |
| `dsn`         | string \| null   | Detail, create: DSN of the first active key, or `null` when every key is revoked. |

#### ProjectKey

`GET /projects/{projectId}/keys` returns `ProjectKey[]`, including revoked
keys. `POST /projects/{projectId}/keys` and `PATCH /keys/{keyId}` return one
`ProjectKey`.

| Field                   | Type             | Description                                                          |
| ----------------------- | ---------------- | -------------------------------------------------------------------- |
| `id`                    | number           | Numeric key id.                                                      |
| `public_key`            | string           | 32 lowercase hexadecimal characters, sent by clients as `X-GlitchPort-Key`. |
| `label`                 | string \| null   | Optional display label, for example `production`.                    |
| `is_active`             | boolean          | `false` for a revoked key.                                           |
| `rate_limit_per_minute` | number \| null   | Per-key ingest limit: `null` means no per-key limit, `0` means unlimited. |
| `created_at`            | string           | UTC ISO 8601 creation timestamp.                                     |
| `revoked_at`            | string \| null   | Revocation timestamp, or `null` while active.                        |
| `dsn`                   | string           | Ingest DSN built from `Processor:PublicRelayUrl`.                    |

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
