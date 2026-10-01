> Historical Phase 0 token snapshot. Current values: `ProjectBeacon.Web/Theme/DesignTokens.cs`.

# Phase 0 — Design Tokens and Decisions

Frozen baseline. Date: 2026-09-30.

Source: `ProjectBeacon.Web/wwwroot/app.css` (lines 1–43, `:root` block).

## Color Tokens

| Token | Value | Purpose |
|---|---|---|
| `--color-bg-app` | `#181818` | Application background |
| `--color-surface-1` | `#141414` | Primary surface (cards, sidebar) |
| `--color-surface-2` | `#1A1A1A` | Secondary surface (panels) |
| `--color-bg-sidebar` | `var(--color-surface-1)` | Sidebar background |
| `--color-bg-header` | `#181818` | Header/appbar background |
| `--color-bg-surface` | `var(--color-surface-2)` | Generic surface |
| `--color-bg-surface-hover` | `#202020` | Surface hover state |
| `--color-bg-accent` | `#1a2740` | Accent background (active nav, chat user bubble) |
| `--color-border` | `#2A2A2A` | Default border |
| `--color-border-subtle` | `#222222` | Subtle border (drawer footer) |
| `--color-border-strong` | `#333333` | Strong border (hover) |
| `--color-border-accent` | `var(--color-accent)` | Accent border |
| `--color-text-primary` | `#F5F5F5` | Primary text |
| `--color-text-secondary` | `#B5B5B5` | Secondary text |
| `--color-text-muted` | `#8A8A8A` | Muted text (nav, labels) |
| `--color-text-disabled` | `#5C5C5C` | Disabled text |
| `--color-accent` | `#3B82F6` | Accent (blue-500) |
| `--color-accent-hover` | `#2563EB` | Accent hover (blue-600) |
| `--color-accent-active` | `#1D4ED8` | Accent active (blue-700) |
| `--color-success` | `#34d399` | Success (emerald-400) |
| `--color-success-background` | `#0f2a22` | Success chip background |
| `--color-warning` | `#fbbf24` | Warning (amber-400) |
| `--color-warning-background` | `#2a2210` | Warning chip background |
| `--color-danger` | `#f87171` | Danger (red-400) |
| `--color-danger-background` | `#2a1416` | Danger chip background |
| `--color-info` | `#60a5fa` | Info (blue-400) |
| `--color-info-background` | `#152033` | Info chip background |

## Spacing Tokens

| Token | Value |
|---|---|
| `--space-1` | `4px` |
| `--space-2` | `8px` |
| `--space-3` | `12px` |
| `--space-4` | `16px` |
| `--space-5` | `20px` |
| `--space-6` | `24px` |
| `--space-8` | `32px` |
| `--space-10` | `40px` |
| `--space-12` | `48px` |
| `--space-16` | `64px` |

## Radius Tokens

| Token | Value |
|---|---|
| `--radius-panel` | `12px` |
| `--radius-small` | `8px` |
| `--radius-pill` | `9999px` |

## Border Tokens

| Token | Value |
|---|---|
| `--border-hairline` | `0.5px` |

## Breakpoints (from media queries in app.css)

| Breakpoint | Query | Used for |
|---|---|---|
| Mobile | `max-width: 479px` | Chat dock full-width, FAB repositioning |
| Mobile landscape | `max-width: 599px` | Page header stacking, actions alignment |
| Tablet | `max-width: 959px` | Kanban column stacking, menu toggle |
| Desktop | `min-width: 960px` | Menu toggle hidden |

## Layout Constants

| Element | Value |
|---|---|
| Appbar height | `64px` (`--mud-appbar-height`) |
| Content max-width | `1440px` (`.beacon-content-max`) |
| Sidebar (drawer) | MudBlazor `MudDrawer`, fixed |
| Auth card max-width | `400px` |
| Form max-width | `520px` |
| Chat dock width | `360px` (mobile: full-width) |
| Chat dock max-height | `70vh` (mobile: `80vh`) |
| Kanban column min-width | `240px` |
| Kanban zone min-height | `60vh` |
| Nav font-size | `13px` |
| Nav icon size | `16px` |
| Card title font-size | `13px` |
| Nav label font-size | `11px` (uppercase, letter-spacing `0.04em`) |
| Chip font-size | `11px` |

## Decisions

| Decision | Status | Notes |
|---|---|---|
| Dark-only mode | **Confirmed** | No light mode in tokens or components. All colors are dark-theme. |
| Sidebar width | **MudBlazor default** (240px) | Controlled by `MudDrawer` component, not custom CSS. |
| Font | **Roboto** (Google Fonts) | Loaded in `App.razor`. |
| UI framework | **MudBlazor** (Material) | Direct usage, no wrapper UI kit. |
| Button text-transform | `none` | `.mud-button-root { text-transform: none; }` |
| Border style | Hairline (`0.5px`) | Used for all subtle borders. |
| Focus style | 2px accent outline + 4px glow | `.beacon-card-interactive:focus-visible` |
| Error boundary | Blazor default + custom CSS | `.blazor-error-boundary` with warning icon. |

## Component CSS Classes (prefix `beacon-`)

| Class | Purpose |
|---|---|
| `.beacon-content-max` | Content max-width wrapper |
| `.beacon-appbar` | Appbar styling |
| `.beacon-breadcrumbs` | Breadcrumb styling |
| `.beacon-profile` | Profile button |
| `.beacon-drawer` | Sidebar drawer |
| `.beacon-drawer-inner` | Drawer flex column |
| `.beacon-drawer-header` | Drawer top (brand) |
| `.beacon-brand` | Brand text |
| `.beacon-nav` | Navigation menu |
| `.beacon-nav-label` | Section labels in nav |
| `.beacon-drawer-footer` | Drawer bottom |
| `.beacon-card` | Base card |
| `.beacon-card-interactive` | Hover/focus card |
| `.beacon-board-card` | Kanban card |
| `.beacon-card-title` | Card title (pointer cursor) |
| `.beacon-card-title-row` | Card title flex row |
| `.beacon-status-dot` | Active status indicator |
| `.beacon-panel` | Panel surface |
| `.beacon-phase-strip` | Roadmap phase strip |
| `.beacon-phase-tick` | Phase tick button |
| `.beacon-phase-mark` | Phase progress mark |
| `.beacon-track` | Progress track |
| `.beacon-page-header` | Page header flex |
| `.beacon-page-actions` | Page action buttons |
| `.beacon-pre` | Preformatted text |
| `.beacon-chat-fab` | Chat FAB |
| `.beacon-chat-dock` | Chat dock panel |
| `.beacon-chat-dock-log` | Chat dock log |
| `.beacon-chat-log` | Chat log |
| `.beacon-chat-pane` | Chat pane |
| `.beacon-chat-user` | User message bubble |
| `.beacon-form` | Form max-width |
| `.beacon-ellipsis` | Text truncation |
| `.beacon-menu-toggle` | Mobile menu toggle (hidden ≥960px) |

## Auth / Landing Shell Classes

| Class | Purpose |
|---|---|
| `.auth-shell` | Auth page full-height flex |
| `.auth-toolbar` | Auth top bar |
| `.auth-body` | Auth center |
| `.auth-card` | Auth card (max 400px) |
| `.landing-shell` | Landing full-height flex |
| `.landing-nav` | Landing top nav |
| `.landing-main` | Landing content (max 960px) |
| `.landing-section` | Landing section spacing |
| `.landing-hero` | Landing hero |
| `.landing-title` | Landing title (max 18ch) |
| `.landing-lead` | Landing lead text (max 42ch) |
| `.landing-footer` | Landing footer |
| `.landing-table` | Landing table |
| `.landing-mock` | Landing mock grid (2-col → 1-col <800px) |
| `.landing-mock-cols` | Landing mock columns (2-col → 1-col <800px) |
