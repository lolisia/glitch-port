# Dashboard

The glitch-port dashboard is a React 19 single-page application built with Vite
and written in TypeScript. It lists and triages issues and events, manages
projects, keys, labels, alert rules and inbound filters, and shows release
aggregates and the legacy message queue. It talks only to the Processor's
Management API (`<VITE_API_BASE>/api/manage`); the screens, URL filters and API
shapes are documented in [`documents/dashboard.md`](../documents/dashboard.md).

## Running

From this directory:

```bash
npm ci
npm run dev
```

`npm run dev` starts the Vite dev server on <http://localhost:5173>. The other
scripts are:

| Command           | Description                                                              |
| ----------------- | ------------------------------------------------------------------------ |
| `npm test`        | Runs the Vitest suite once with Testing Library and jsdom.               |
| `npm run build`   | Type-checks with `tsc -b` and writes the production bundle to `dist/`.   |
| `npm run preview` | Serves the built `dist/` bundle locally.                                 |

The tests mock `fetch`, so they do not need a running Processor. The three
Vite variables `VITE_API_BASE`, `VITE_ADMIN_TOKEN` and `VITE_PROJECT_ID` are
described in [`documents/dashboard.md`](../documents/dashboard.md#configuration).

## UI stack

- **React 19 + Vite** with TypeScript and `@vitejs/plugin-react`.
- **Tailwind CSS v4** through the `@tailwindcss/vite` plugin (see
  `vite.config.ts`). There is no `tailwind.config.js`: the theme, the `dark`
  variant and the base layer live in `src/index.css`.
- **shadcn/ui new-york components** copied into `src/components/ui`. They are
  source files owned by this repository, not a runtime dependency, so they can
  be edited locally. `components.json` records the style (`new-york`), the
  neutral base colour, CSS variables, lucide icons and the aliases.
- **Radix primitives** (through the `radix-ui` package), `cmdk`,
  `lucide-react`, `class-variance-authority`, `clsx` and `tailwind-merge` as
  the supporting libraries.

### Adding a shadcn/ui component

Run the shadcn CLI from this directory:

```bash
npx shadcn@latest add <name>
```

For example `npx shadcn@latest add dialog` writes
`src/components/ui/dialog.tsx`. The CLI reads `components.json`, so it uses the
new-york style, the `@/` aliases and `src/index.css`. Review the generated file
like any other source file: keep the `@/` imports and use the existing tokens
instead of introducing new colours.

### The `@/` alias and `cn()`

- `@/` points at `dashboard/src` and is configured in both `vite.config.ts`
  (`resolve.alias`) and `tsconfig.app.json` (`compilerOptions.paths`). Use it
  for cross-directory imports such as
  `import { Button } from '@/components/ui/button'` or
  `import { cn } from '@/lib/utils'`; keep relative imports inside a feature
  folder.
- `cn()` in `src/lib/utils.ts` joins conditional class names with `clsx` and
  resolves conflicting Tailwind classes with `tailwind-merge`. Use it for
  conditional classes and whenever a component accepts a `className` prop.

## Settings (`#/settings`)

The settings screen owns the two per-browser preferences and applies them
immediately, without a save button:

- **Appearance** (theme) — System, Light or Dark. The choice is stored in
  `localStorage` under `glitchport.theme`.
- **Language** — English or Korean. The choice is stored under
  `glitchport.locale` and updates `document.documentElement.lang`.

The sidebar footer links to `#/settings`; it is a normal hash route handled by
`src/router.ts`. Theme and language controls do not live in the header or the
sidebar chrome.

## Theme

- The stored preference is `glitchport.theme` with the values `system`,
  `light` and `dark` (anything else falls back to `system`).
- `src/theme/ThemeProvider.tsx` resolves `system` against
  `prefers-color-scheme` and writes the resolved theme to the `<html>`
  element: `data-theme="light"` or `data-theme="dark"`, plus `color-scheme`.
  While the preference is `system` it follows the media query; an explicit
  preference stops listening.
- `dashboard/index.html` contains a small inline bootstrap script that reads
  the same `glitchport.theme` key and applies `data-theme` and `color-scheme`
  before the bundle loads, so reloading in dark mode does not flash the light
  theme. Keep the storage key and the accepted values in sync with
  `src/theme/theme.ts`.
- `src/index.css` defines the palette twice — the shadcn tokens on `:root`
  (light) and `:root[data-theme='dark']` (dark) — and maps them to Tailwind
  colour utilities in the `@theme inline` block (`bg-background`,
  `text-muted-foreground`, `border-border`, `bg-primary`, `text-success`, ...).
  The `@custom-variant dark` line selects the dark variant from
  `[data-theme=dark]`.
- Add a colour as a token in both blocks and use the generated utility. Never
  hard-code a colour in a screen, and never reintroduce the removed legacy
  `--color-*` aliases.

## Styling rules

- Screens are styled with Tailwind utilities and the shared shadcn components
  in `src/components/ui`. **Do not add per-screen `.css` files or import extra
  stylesheets.** `src/main.tsx` is the only module that imports CSS
  (`./index.css`), and `src/index.css` keeps only the design tokens, the
  `@theme inline` mapping, the `dark` variant and the small `@layer base`
  element defaults.
- Prefer semantic utilities (`bg-card`, `text-muted-foreground`,
  `border-border`, `text-destructive`, `text-warning`, ...) over raw palette
  classes so light and dark mode both work.
- Reusable presentation belongs in `src/components/`; screen-specific markup
  stays under the matching `src/pages/` folder.

## Source layout

```text
src/
  api/          Management API client and response types
  assets/       static assets imported by the bundle
  components/   shared components; components/ui holds the shadcn primitives
  event/        event payload rendering
  hooks/        shared React hooks
  i18n/         locale provider and the English/Korean message dictionaries
  layout/       app sidebar and header
  lib/          cn() and other small helpers
  pages/        screens grouped by feature
  project/      active-project persistence and resolution
  theme/        theme preference, provider and DOM application
  ui/           small shared presentational helpers (badges, chips, states)
  App.tsx       shell: providers, sidebar, header and route switch
  index.css     Tailwind entry, design tokens and base layer
  main.tsx      root render; the only CSS import in the app
  router.ts     hash router
```
