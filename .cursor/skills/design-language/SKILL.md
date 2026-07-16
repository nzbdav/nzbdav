---
name: design-language
description: NzbDav frontend design language and styling guidelines, derived from the dmbdb (Debrid Media Bridge Dashboard) project. Use when refactoring, restyling, or building any frontend UI — pages, components, buttons, cards, badges, forms, tabs, themes, colors, spacing, or typography in frontend/app.
---

# NzbDav Design Language

Visual and styling guidelines for the NzbDav frontend. Apply these when refactoring or building UI in `frontend/app`.

## Core principles

1. **Dark-first.** The document uses daisyUI's built-in `night` theme via `data-theme="night"`.
2. **daisyUI-native components.** Use daisyUI component classes and supported markup for buttons, forms, toggles, modals, alerts, badges, tabs, tooltips, and loading indicators. Prefer the wrappers in `app/components/ui`; direct daisyUI classes are also allowed.
3. **Semantic colors.** Use daisyUI semantic utilities such as `bg-base-100`, `text-base-content`, `btn-primary`, and `text-error`. Do **not** use raw slate/blue/gray/amber/red palette utilities for chrome, text, borders, or surfaces.
4. **Tailwind for layout.** Continue using Tailwind utilities for spacing, responsive layout, typography, and one-off composition around daisyUI components.
5. **Density with breathing room.** Prefer native daisyUI size modifiers (`btn-xs`, `btn-sm`, `input-sm`) for dense controls inside generously spaced pages (`gap-8`, `p-4 md:px-8`).

## Reference pages

These routes already follow the design language — copy their patterns:

- `app/routes/login/route.tsx` — `hero`, `card`, `floating-label`, UI kit forms
- `app/routes/onboarding/route.tsx` — same auth/card pattern
- `app/routes/search/route.tsx` — `join`, tables, badges, semantic tokens without CSS modules

## Theme token vocabulary

The built-in daisyUI `night` theme (enabled in `app/app.css`) is the source of truth. Its primary daisyUI variables are:

| Token | Role |
|-------|------|
| `--color-base-100/200/300` | Surfaces through deepest page background |
| `--color-base-content` | Default foreground |
| `--color-primary` / `--color-primary-content` | Primary actions and their foreground |
| `--color-secondary` / `--color-accent` | Secondary semantic accents |
| `--color-neutral` / `--color-neutral-content` | Neutral surfaces and muted content |
| `--color-info/success/warning/error` | Status colors |

The old `--app-*` variables remain compatibility aliases for existing CSS modules. Do not use them as the source of truth for new components.

## Color semantics

Prefer semantic daisyUI utilities (and the shared UI kit) over fixed Tailwind palettes:

- **Accent / interactive:** `text-primary`, `bg-primary`, `border-primary`, `link` / `btn-primary`. Active tab = `tab-active` (or `text-primary border-primary` when hand-rolling).
- **Status dots** (small `rounded-full` circles, `h-2 w-2` in lists, `w-3 h-3 md:w-4 md:h-4` on cards):
  - running/healthy → `bg-success`
  - stopped/error → `bg-error`
  - degraded/unknown → `bg-warning`
  - inactive → `bg-base-content/40`
- **Action button intents:** use `btn-success`, `btn-error`, `btn-warning`, `btn-primary` (not hand-rolled `bg-green-500` etc.).
- **Alerts:** use the shared `Alert` component or daisyUI `alert` / `alert-warning` / `alert-error` / `alert-success` / `alert-info`.

Charts and other intentionally theme-independent data series may use explicit colors when needed; prefer mapping series to `var(--color-success)` / `var(--color-error)` etc. when possible.

## Surfaces and structure

- **Card:** `card bg-base-100 border border-base-content/10 shadow-md` (or `bg-base-100 rounded-lg …`). Prefer daisyUI `card` / `card-body` when the block is a card.
- **Panel / grouped section:** `rounded border border-base-content/10 bg-base-100` with an internal header row and `border-t border-base-content/10` divider.
- **Sidebar:** fixed-width (`max-w-[250px]`), `bg-base-300 border-r border-base-content/10`, collapsible on mobile.
- **Modal / overlay:** use the shared `Modal` (`dialog.modal`) or daisyUI modal markup — do not hand-roll slate overlays.
- **Ghost icon button:** `btn btn-ghost btn-sm` (or `btn-circle`).
- **Badge / pill:** daisyUI `badge` / shared `Badge`; use `font-mono` for numeric metrics.

## Typography

- Page title: `text-4xl font-bold`, with optional subtitle `text-xs text-base-content/60 mt-1`.
- Section heading: `text-xl font-semibold`. Sidebar/nav group: `text-lg font-bold`.
- Group micro-label: `text-xs uppercase tracking-wide text-base-content/50`.
- Body/meta hierarchy: `text-base-content` → `text-base-content/80` → `text-base-content/60` → `text-base-content/45`.
- Metrics and numbers: `font-mono`.

## Buttons

- Use `btn` plus a semantic modifier: `btn-primary`, `btn-success`, `btn-error`, `btn-warning`, `btn-outline`, or `btn-ghost`.
- Use the native size scale: `btn-xs`, `btn-sm`, default, and `btn-lg`. Use `btn-circle` for icon-only circular buttons.
- Prefer the shared `Button` wrapper when its variant API fits.
- Icons inside buttons still use explicit Material Symbol sizes such as `!text-[18px]`.

## Icons

Use **Material Symbols Rounded** (variable font, weight 300, `FILL 0` default; `FILL 1` for emphasized/filled states like play/stop). Size icons explicitly with `!text-[Npx]` rather than relying on the inherited font size. Icon names as text content, e.g. `play_arrow`, `refresh`, `expand_more`, `save`, `close`.

## Forms

- Use daisyUI `fieldset`, `fieldset-legend`, and `label` for grouped fields (or shared `Field` / `Label` / `HelpText`).
- Controls use `input`, `select`, `textarea`, `checkbox`, `radio`, and `toggle` (or shared `Input` / `Select` / …).
- Use native semantic states such as `input-error` and native size modifiers instead of recreating borders/focus rings (`border-red-500` is not allowed).
- Do not add global element rules for inputs, selects, or checkboxes; they override daisyUI component styling.

## Tabs

Use `tabs tabs-border` with `tab`, `tab-active`, and `tab-disabled`. Tabs may pair a Material Symbol with a label.

## Layout

- App shell: full-height (`h-dvh`) flex row — sidebar + `flex-1 min-h-0 overflow-y-auto` main pane. Guard flex children with `min-w-0`.
- Page container: `px-4 py-4 md:px-8` with `flex flex-col gap-8` between page-level blocks.
- Card grids: `grid grid-cols-1 lg:grid-cols-2 gap-4`.
- Mobile-first with `sm:`/`md:`/`lg:` steps; card internals stack on mobile (`flex-col`) and go horizontal at `sm:` (`sm:flex-row sm:items-center sm:justify-between`).

## Motion

- Standard transition: `transition-all ease-in-out duration-200`.
- Loading: `animate-spin` on a refresh/`cached` icon, or daisyUI `loading loading-spinner`.
- Chevron rotation for expand/collapse: `rotate-180` toggle with `transform transition ease-in-out`.
- Hover micro-scale on grouped icons: `group-hover:scale-105`.
- Drag feedback: `opacity-75 scale-[0.99]`, `cursor-grab active:cursor-grabbing`.

## Scrollbars

Hide by default in dense panes (`.no-scrollbar`), or show a thin styled one (`.yes-scrollbar`: 6px wide, rounded accent-colored thumb).

## Applying themes

Set `data-theme="night"` on the document root. New code must use daisyUI semantic classes (`bg-base-*`, `text-base-content`, etc.), not raw palette utilities.

## NzbDav-specific notes

- The frontend is React Router 8 with Tailwind 4, daisyUI 5, and (legacy) CSS modules.
- Prefer Tailwind + daisyUI over new CSS modules. Existing route CSS modules remain supported through the `--app-*` compatibility aliases and should migrate incrementally.
- Keep Material Symbols Rounded for icons; daisyUI does not provide an icon set.
- Run `npm run typecheck` in `frontend/` after styling refactors.
