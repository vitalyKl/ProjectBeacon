# UI Design Migration Specification

Authoritative UI specification. Token values live in `ProjectBeacon.Web/Theme/DesignTokens.cs`. If this file and that class disagree, the class wins and this file should be corrected. Execution order stays in the master roadmap. The corrected-geometry note and the Phase 3 IA plan are archived; do not keep a second token table.

## 1. Purpose

The visual language below is the design system for the shipped Blazor Server + MudBlazor UI. Do not change business logic, domain model, API contracts, or routing to chase a visual change.

The target result is not a pixel-perfect copy of the reference screenshot.

The target result is a coherent application-wide design system inspired by the reference:

- dark, minimal, professional interface;
- persistent navigation sidebar;
- compact top header;
- card-based content;
- restrained borders;
- blue primary accent;
- semantic red/green states;
- generous spacing;
- strong visual hierarchy;
- minimal decorative elements;
- consistent component behavior across all pages.

The implementation must be adapted to the existing project's architecture and technology stack.

## 1.1 Shipped shell

`MainLayout` is the authenticated shell: `MudAppBar`, `MudDrawer` (`DrawerVariant.Responsive`, `Breakpoint.Md`, `Elevation="0"`), `ProjectSwitcher`, breadcrumbs, version footer, `LanguageSwitcher`, page body, and `ChatDock`.

Drawer groups:

```text
Work        Dashboard /dashboard, Board /board, Backlog /backlog, Roadmap /roadmap
Knowledge   Context /context, Decisions /decisions, Reports /reports
Agents      Chat /chat, Agents /settings/agents, Workstations /settings/workstations
Project     Project settings /project/settings
Account     Settings /settings, Connections /settings/connections
```

`/agents` redirects to `/settings/agents`. Agents and model backends are user-level. Workstations are user-owned devices. Project settings are project-level. Learn and Files are not shipped.

Anonymous `/` is `Landing.razor` (`LandingLayout`), not a redirect to the dashboard. Other auth routes: `/login`, `/register`, `/forgot`, `/reset`, `/recover`, `/bootstrap`, `/invite`. Task detail is `/task/{taskId}`. New project is `/projects/new`.

Chat has two surfaces. `/chat` is the full page. `ChatDock` is a FAB in the shell that opens a small panel and links to `/chat`. The dock is quick access, not a second product.

Shared primitives already in the tree: `PageHeader`, `StatusChip` with `ChipPalette`, `ProjectSwitcher`, `LanguageSwitcher`. Do not inline `MudChip` for status. Do not add page-local colors, radii, or spacing; change `DesignTokens.cs`.

Chrome strings go through `IStringLocalizer<Web>`. Culture is the cookie set by `GET /culture`.

Drawer width is `BeaconTheme.LayoutProperties.DrawerWidthLeft` = `168px`. Do not set a second width on `MudDrawer`.

---

# 2. Reference UI

The reference image represents the target visual language.

The main structural elements are:

1. Application shell.
2. Left navigation sidebar.
3. Top header.
4. Main content area.
5. Section headings.
6. Cards.
7. Status badges.
8. Active navigation state.
9. User/avatar control.
10. Contextual breadcrumbs/navigation.
11. Empty-space-driven layout rather than dense dashboard packing.

The reference must be treated as a **design reference**, not as a source of literal content.

Do not copy:

- product name;
- textual content;
- fake data;
- specific icons;
- exact navigation labels;
- exact card content.

Instead, map the visual structure to the existing application's real functionality.

---

# 3. Agent Workflow

Agents must not immediately rewrite the UI.

Implementation must be performed in the following phases.

## Phase 1 — Project reconnaissance

Before changing any code, inspect:

- solution/project structure;
- frontend technology;
- routing;
- existing layout components;
- global styles;
- theme implementation;
- reusable UI components;
- authentication/user UI;
- navigation;
- existing responsive behavior;
- pages/screens;
- forms;
- tables;
- dialogs/modals;
- notifications;
- loading states;
- error states.

Identify:

- existing components that can be reused;
- components that should be restyled;
- duplicated UI;
- obsolete styling;
- hard-coded colors;
- hard-coded spacing;
- page-specific styling that should become global;
- places where introducing the new design system would reduce duplication.

Do not rewrite business logic during this phase.

Produce a short internal implementation plan before proceeding.

---

# 4. Architectural Rule

The UI migration must be separated from business logic.

Agents must not:

- change database schemas;
- change domain entities;
- change API contracts;
- change authentication behavior;
- change business rules;
- move business logic into UI components;
- introduce unnecessary state-management systems;
- rewrite working backend code merely to simplify UI implementation.

If an existing component contains business logic and visual logic together, extract only what is necessary for the UI migration.

Prefer incremental refactoring.

---

# 5. Application Shell

Create or refactor a global application shell.

Conceptually:

```text
Application
├── Sidebar
│   ├── Brand
│   ├── Primary navigation
│   ├── Secondary navigation
│   └── Bottom utility/navigation area
│
└── Main
    ├── Header
    │   ├── Breadcrumb/context
    │   └── User area
    │
    └── Page content
```

The shell must be reused by all authenticated/application pages.

Pages should not individually implement:

- sidebar;
- top navigation;
- user avatar;
- global background;
- global content padding.

---

# 6. Sidebar

## Visual characteristics

The sidebar should visually resemble the reference:

- dark background;
- slightly different tone from the main content;
- fixed or persistent on desktop;
- full application height;
- subtle separation from content;
- generous horizontal padding;
- compact navigation rows;
- rounded active navigation item.

Approximate proportions:

```text
┌───────────────────────┐
│                       │
│  [logo] Product       │
│                       │
│  [icon] Active        │
│  [icon] Navigation    │
│  [icon] Navigation    │
│  [icon] Navigation    │
│                       │
│  [icon] Navigation    │
│                       │
│  [icon] Navigation    │
│                       │
└───────────────────────┘
```

Desktop width is `168px`, set by `BeaconTheme.LayoutProperties.DrawerWidthLeft`. Do not also set `MudDrawer.Width`.

Sidebar padding in the mockup was `1rem 0.75rem`. The shipped header uses `--space-4` / `--space-3`; nav padding uses `--space-1` / `--space-3`.

Below `Breakpoint.Md` the drawer collapses. Do not require a permanent 168px column on tablet or mobile.

---

# 7. Sidebar Navigation

Navigation items must have three visual states:

## Default

- muted text;
- transparent background;
- low-contrast icon.

## Hover

- slightly brighter text;
- subtle background change;
- short transition.

## Active

- blue-tinted background;
- bright text;
- blue or white icon;
- rounded container.

Example conceptual colors:

```text
Default:
text        #B5B5B5

Hover:
background  #202020
text        #E5E5E5

Active:
background  dark blue
text        light blue / white
```

Do not use bright saturated blue backgrounds.

The active item should be clearly visible but still fit the dark theme.

---

# 8. Main Background

Use a layered dark palette rather than a single black value.

## Surface hierarchy

Two surface levels plus an accent level (transcribed from mockup source):

| Level | Used for |
|---|---|
| Surface 2 | Floating panels: context brief, changed scope, chat panel |
| Surface 1 | Sidebar background, board cards, non-human chat message bubbles, budget track background |
| Accent background | Human chat message bubble, active navigation item background |

Required semantic tokens:

```text
--color-bg-app         application background
--color-surface-1      sidebar, board cards, non-human bubbles, budget track
--color-surface-2      floating panels (context brief, changed scope, chat)
--color-bg-accent      active nav item, human chat bubble
--color-border         hairline borders
--color-border-accent  active board card border
```

Shipped hex values (`DesignTokens.cs`):

```text
Application background   #181818
Surface 1                #141414
Surface 2                #1A1A1A
Header                   same as application background
Surface hover            #202020
Accent background        #1a2740
Border                   #2A2A2A
Border subtle            #222222
Border strong            #333333
Text primary             #F5F5F5
Text secondary           #B5B5B5
Text muted               #8A8A8A
Text disabled            #5C5C5C
Accent                   #3B82F6
```

Surface tokens are not `#171717`. MudBlazor `Palette.Dark` is `#171717`; do not use that as a second surface token.

Do not scatter literal color values throughout components.

---

# 9. Design Tokens

The token layer already exists: `ProjectBeacon.Web/Theme/DesignTokens.cs`, emitted as CSS custom properties. `BeaconTheme.cs` reads from it. Do not create a second table in Razor or CSS.

The names below are the contract. Values are the constants in that class (spacing 4–64px, radii 12px / 8px / pill, hairline 0.5px, type scale 24 / 16 / 14 / 14 / 13 / 12).

## Colors

```text
background
surface-1
surface-2
bg-accent
border
border-accent

text-primary
text-secondary
text-muted
text-disabled

accent
accent-hover
accent-active

success
success-background

warning
warning-background

danger
danger-background

info
info-background
```

## Spacing

Use a consistent base scale:

```text
4
8
12
16
20
24
32
40
48
64
```

Panel and component padding is per-context, not a single flat value:

| Context | Padding |
|---|---|
| Panel body (context brief, chat, changed scope) | `1rem 1.25rem` (16px / 20px) |
| Board column area | `1rem 1.25rem` (16px / 20px) |
| Sidebar | `1rem 0.75rem` (16px / 12px) |
| Topbar / breadcrumb row | `0.85rem 1.25rem` (13.6px / 20px) |
| Board card | `10px` (flat) |
| Nav item, command-palette hint | `7px 8px` |
| Chat message bubble | `8px 12px` |
| Tool-call chip | `4px 8px` |
| Badge / pill | `2px 8px` |

Gaps:

| Context | Gap |
|---|---|
| Context-brief section rows (icon–label–count) | `10px` |
| Changed-scope file rows | `2px` (rows are separated by a bottom border, not whitespace) |
| Chat message list | `14px` |
| Board column gap | `12px` |
| Board card stack within a column | `8px` |
| Nav item icon-to-label | `8px` |
| Column header (title-to-count) | `6px` |
| Chat header (dot–name–status) | `8px` |
| Tool-call chip (icon-to-text) | `6px` |

Components should use the per-context values above rather than arbitrary ones.

---

# 10. Typography

The reference uses a modern sans-serif interface with relatively large, readable text.

Establish typography tokens:

```text
display
page-title
section-title
card-title
body
body-small
caption
label
```

Type scale (transcribed from actual mockup usages; supersedes any earlier 14–32px hierarchy):

| Size | Used for |
|---|---|
| `11px` | Badge/pill text, small status text on active card |
| `12px` | Muted secondary text, counts, tool-chip text, command-palette hint |
| `13px` | Body text on cards, code snippets, nav item labels, column headers |
| `14px` | Section-row labels, chat sender name |

Use font weights primarily in the range:

```text
400
500
600
```

Avoid excessive use of bold.

Typography must communicate hierarchy before color does.

---

# 11. Header

The global header should resemble the reference:

```text
┌─────────────────────────────────────────────────────────────┐
│ Context / Breadcrumb                         User / Avatar │
└─────────────────────────────────────────────────────────────┘
```

Characteristics:

- approximately 64–72px height;
- padding `0.85rem 1.25rem` (13.6px / 20px — the transcribed value is not a round number; keep it as-is);
- dark background;
- subtle bottom border;
- horizontally aligned content;
- left contextual navigation;
- right user/account controls.

The header must remain visually secondary to page content.

---

# 12. Breadcrumbs / Context Navigation

Where applicable, use:

```text
Parent  ›  Current page
```

The parent item should be visually muted.

The current page should have stronger contrast.

Do not create breadcrumbs where they do not provide useful navigation context.

---

# 13. User Avatar

The user control should follow the visual language of the reference:

- circular;
- compact;
- dark/blue accent;
- initials or existing user representation;
- subtle hover state.

Exact geometry: `26px × 26px`, `border-radius: 50%`, `11px` initials text.

Example:

```text
┌──────┐
│  VK  │
└──────┘
```

Do not introduce unnecessary profile decoration.

If the existing application already has a user menu, restyle the existing implementation instead of replacing it.

---

# 14. Page Layout

The content area should have generous spacing.

Conceptually:

```text
┌────────────────────────────────────────────────────────────┐
│                                                            │
│   Page title                                               │
│                                                            │
│   Optional description / actions                           │
│                                                            │
│   ┌────────────┐ ┌────────────┐ ┌────────────┐             │
│   │            │ │            │ │            │             │
│   │   Card     │ │   Card     │ │   Card     │             │
│   │            │ │            │ │            │             │
│   └────────────┘ └────────────┘ └────────────┘             │
│                                                            │
└────────────────────────────────────────────────────────────┘
```

Recommended content padding:

```text
Desktop:
32–40px

Large desktop:
40–48px

Mobile:
16–20px
```

Avoid extremely wide text blocks.

Use a reasonable content max-width where the page type benefits from it.

---

# 15. Cards

Cards are one of the primary visual primitives.

Reference characteristics:

- dark surface (board cards sit on surface 1);
- subtle hairline border;
- 8px radius (small tier, see section 25);
- minimal/no shadow;
- internal padding (board card: `10px` flat);
- clear title;
- secondary metadata;
- semantic badges where necessary.

Conceptual structure:

```text
┌──────────────────────────────┐
│ Card title                   │
│                              │
│ Primary information          │
│                              │
│ Secondary information        │
│                              │
│ [Status]                     │
└──────────────────────────────┘
```

Avoid excessive cards.

A card should represent a meaningful conceptual unit.

Do not wrap every small element in a card.

---

# 16. Card States

Cards may have:

- default;
- hover;
- selected;
- active;
- disabled;
- error;
- loading.

The visual difference between states should remain subtle.

For interactive cards:

```text
default  → subtle border
hover    → slightly brighter border/background
active   → accent border
```

The active card border is `0.5px solid` in the accent border color — the same hairline weight as every other border, only the color changes.

Avoid large scale animations.

---

# 17. Status Badges

The reference uses small semantic pills.

Examples:

```text
[Security]
[i18n]
[P0]
[Shipped]
```

Semantic categories:

### Success

Green text on dark green background.

### Danger

Red text on dark red background.

### Warning

Yellow/orange text on dark yellow/orange background.

### Neutral

Muted gray text on dark neutral background.

### Information

Blue text on dark blue background.

Badges should be:

- compact (`2px 8px` padding, `11px` text);
- rounded (small tier);
- readable;
- visually secondary to the main content.

Do not use badges as the primary navigation mechanism.

---

# 18. Semantic Color Rules

Color must communicate meaning.

Use:

```text
Blue   = interaction / active / primary action
Green  = success / completed / healthy
Red    = error / critical / destructive
Yellow = warning / attention
Gray   = neutral / inactive
```

Do not use red merely as decoration.

Do not use green merely because it looks visually attractive.

---

# 19. Buttons

Primary buttons should use the blue accent.

Secondary buttons should use:

- dark surface;
- subtle border;
- muted text.

Danger actions should use red only when the action is actually destructive.

Conceptual hierarchy:

```text
Primary:
[ Save ]

Secondary:
[ Cancel ]

Danger:
[ Delete ]
```

Avoid having several visually dominant buttons in the same area.

---

# 20. Inputs

Inputs should follow the same dark-surface language:

```text
┌──────────────────────────────┐
│ Placeholder                  │
└──────────────────────────────┘
```

Characteristics:

- dark background;
- subtle border;
- rounded corners;
- clear focus state;
- readable placeholder;
- visible error state.

Focus should use the primary blue accent.

Do not remove focus indicators for accessibility.

---

# 21. Tables

For data-heavy pages:

- dark surface;
- subtle row separators;
- muted column headers;
- strong primary values;
- semantic badges;
- restrained hover state.

Avoid heavy grid lines.

Conceptual:

```text
┌──────────────────────────────────────────────┐
│ NAME        STATUS       CREATED      ACTION │
├──────────────────────────────────────────────┤
│ Server A    [Healthy]    Today        ...    │
│ Server B    [Warning]    Yesterday    ...    │
└──────────────────────────────────────────────┘
```

The table should visually belong to the same system as the cards.

---

# 22. Modals / Dialogs

Dialogs should use the same surface hierarchy.

```text
Overlay:
dark translucent layer

Dialog:
dark surface (surface 2)
subtle border
12px radius (panel tier)
```

The dialog must have:

- clear title;
- optional description;
- content;
- action area.

Avoid excessive shadows or glassmorphism unless the existing project explicitly requires it.

---

# 23. Notifications

Notifications/toasts should follow the semantic color system.

Examples:

```text
Success → green accent
Warning → yellow accent
Error   → red accent
Info    → blue accent
```

Keep them visually compact.

---

# 24. Icons

Use one consistent icon library.

Do not mix several unrelated icon styles.

Icons should generally be:

- simple;
- line-based;
- compact;
- visually subordinate to labels.

Sizes (transcribed from mockup usages):

```text
Section-row icons (context brief), nav icons, send-button icon:  16px
Changed-scope file-row icons:                                    15px
Tool-call chip icon:                                             14px
```

If the project already contains an icon system, reuse it.

Do not introduce a second icon library without a clear reason.

---

# 25. Border Radius

Two tiers, transcribed from mockup source:

| Tier | Value | Used for |
|---|---|---|
| Panel tier | `12px` | Panels (context brief, chat, changed scope), outer app-shell frame, dialogs |
| Small tier | `8px` | Cards, nav items, badges, message bubbles, tool-call chips, command-palette hint |
| Pill | `9999px` | Budget track (6px-tall bar, fully rounded), status dots |

The mockup source references the small tier as an internal design token whose resolved pixel value was not available. `8px` is an explicit decision (within the 6–8px range suggested for a value sitting comfortably below the panel tier's 12px), not a value transcribed from the source. It is fixed from this point on.

---

# 26. Component Geometry

Per-component values transcribed from the mockup source. Values not covered here fall back to the token tables in sections 8–10 and 25.

## Border weight

Every border is `0.5px solid` — a deliberate hairline weight, never the browser default. One color exception: the currently-active board card uses `0.5px solid` in the accent border color (a highlighted variant, not a different weight).

## Panels (context brief, changed scope, chat)

- Background: surface 2.
- Radius: 12px.
- Body padding: `1rem 1.25rem` (16px vertical / 20px horizontal — not equal).

## Sidebar

- Width: `168px` (`BeaconTheme.DrawerWidthLeft`). Background: surface 1.
- Padding: `1rem 0.75rem` (16px / 12px).
- Nav items: `7px 8px` padding, `8px` icon-to-label gap, `16px` icon, `13px` label text, small radius tier.
- Active nav item: accent background, bright text, accent icon.

## Board

- Column area padding: `1rem 1.25rem`.
- Column gap: `12px`; card stack gap within a column: `8px`.
- Column header (title-to-count): `6px` gap, `13px` text.
- Card: surface 1 background, `10px` flat padding, small radius tier, `13px` body text.
- Active card: `0.5px solid` accent border.
- Status dot: `8px` (the mockup used `8px` in the chat header and `6px` on board cards; the chat-header size is adopted as the single value).

## Context brief

- Section rows: `10px` gap between icon, label, and count.
- Icon `16px`; label `14px`; count `12px` muted.

## Changed scope

- File rows: `2px` gap; rows are separated by a bottom border, not whitespace.
- Row icon: `15px`.

## Chat

Full page: `/chat` (`Features/Chat/Chat.razor`). Shell dock: `ChatDock` (FAB, panel, link to the full page). Both use `ChatPartView`. The dock is not a separate conversation model.

- Header: `8px` gap between dot, name, and status; status dot `8px`.
- Message list gap: `14px`.
- Bubble: `8px 12px` padding, small radius tier; non-human bubbles on surface 1, human bubbles on the accent background.
- Sender name: `14px`.
- Tool-call chip: `4px 8px` padding, `6px` icon-to-text gap, `14px` icon, `12px` text, small radius tier.
- Send button: `36px × 36px` square, icon-only (`16px` icon), `padding: 0`.
- Avatar: `26px × 26px`, `border-radius: 50%`, `11px` initials.

## Budget track

- `6px` tall, pill radius; background: surface 1.

## Command palette

- Hint row: `7px 8px` padding, `12px` text, small radius tier.

---

# 27. Shadows

The reference relies primarily on contrast and borders rather than shadows.

Default:

```text
box-shadow: none
```

Use shadows only where required to establish elevation, such as:

- dropdowns;
- popovers;
- dialogs.

Even there, keep them subtle.

---

# 28. Motion

Animations must be restrained.

Use short transitions for:

- hover;
- focus;
- active state;
- dropdown opening;
- sidebar interactions.

Recommended duration:

```text
120–200ms
```

Avoid:

- bouncing;
- large scaling;
- dramatic sliding;
- decorative animations.

The application should feel fast and utilitarian.

---

# 29. Responsive Behavior

Desktop is the primary visual reference, but the design must be responsive.

Desktop:

```text
Sidebar visible
Header visible
Multi-column layouts allowed
```

Tablet:

```text
Sidebar may collapse
Content padding decreases
Cards adapt to available width
```

Mobile:

```text
Sidebar becomes drawer/bottom navigation where appropriate
Single-column content
Reduced padding
Tables may become horizontally scrollable or transform into cards
```

Do not simply shrink the desktop layout.

The shipped drawer collapses under `Breakpoint.Md`. Critical flows still need a usable single column there; do not rely on hover-only actions.

## Accessibility

- Icon-only controls need an accessible name (`aria-label` or equivalent). `ChatDock` close and FAB already do.
- Status is not color alone. Use `StatusChip` / `ChipPalette` and text.
- Primary actions must be reachable without a pointer. Kanban drag needs a non-drag alternative where drag exists.
- Dialogs and menus must move focus in a way MudBlazor already provides; do not roll a second focus trap.
- User-facing chrome is localized. Do not hardcode English chrome in Razor. Leave user-authored titles and descriptions as written.

A full keyboard audit is not claimed by this document.

---

# 30. Component Hierarchy

The final component architecture should approximately resemble:

```text
AppShell
│
├── Sidebar
│   ├── Brand
│   ├── Navigation
│   │   └── NavigationItem
│   └── UtilityNavigation
│
├── Header
│   ├── Breadcrumbs
│   └── UserMenu
│
└── MainContent
    ├── PageHeader
    ├── Section
    ├── Card
    ├── Badge
    ├── Button
    ├── Input
    ├── Table
    ├── Dialog
    └── Notification
```

Names must be adapted to the conventions of the existing project.

Do not blindly create all components if some are not required.

---

# 31. Routes

Do not rediscover the page list. Authenticated routes:

| Route | Page |
|---|---|
| `/dashboard` | Dashboard |
| `/board` | Board |
| `/backlog` | Backlog |
| `/roadmap` | Roadmap |
| `/context` | Context |
| `/decisions` | Decisions |
| `/reports` | Reports |
| `/chat` | Chat (full page) |
| `/task/{taskId}` | Task detail |
| `/settings/agents` | User-level agents and models (`/agents` redirects here) |
| `/settings/workstations` | User-owned workstations |
| `/project/settings` | Project settings |
| `/projects/new` | Create project |
| `/settings` | Account settings |
| `/settings/connections` | OpenCode connections |

Auth and landing use their own layouts: `/`, `/login`, `/register`, `/forgot`, `/reset`, `/recover`, `/bootstrap`, `/invite`.

Every new screen still needs an empty, loading, and error state. That is a requirement for new work, not a classification exercise.

---

# 32. Migration Strategy

Historical. Tokens, `MainLayout`, and the routes in §31 already exist. Do not re-run this sequence. New UI work extends `DesignTokens.cs` and the shared primitives instead.

Do not migrate every page simultaneously.

Use this order:

## Step 1

Implement design tokens.

## Step 2

Implement application shell.

## Step 3

Implement sidebar.

## Step 4

Implement header.

## Step 5

Implement core primitives:

- Button;
- Badge;
- Card;
- Input;
- Select;
- Table;
- Dialog;
- Notification.

## Step 6

Migrate one representative page.

The first page should exercise as many common components as possible.

## Step 7

Review against the reference.

## Step 8

Migrate remaining pages.

## Step 9

Remove obsolete styles.

## Step 10

Run responsive/accessibility regression checks.

---

# 33. Agent Rules

Agents must follow these rules during implementation.

### Rule 1 — Inspect first

Never assume the project architecture.

### Rule 2 — Reuse existing infrastructure

Prefer existing:

- components;
- CSS architecture;
- theme system;
- routing;
- icon library;
- localization;
- accessibility infrastructure.

### Rule 3 — Centralize visual decisions

Colors, spacing, typography, radius and transitions belong to the design system.

### Rule 4 — Avoid page-specific duplication

If the same visual pattern appears on three pages, create/reuse a shared component.

### Rule 5 — Do not over-engineer

Do not create abstractions for one-off elements.

### Rule 6 — Preserve functionality

A visual migration must not silently change behavior.

### Rule 7 — Small changes

Make changes in small, verifiable steps.

### Rule 8 — Validate after every major phase

Build and test after:

- design tokens;
- application shell;
- shared components;
- representative page;
- full migration.

---

# 34. Visual Acceptance Criteria

The UI migration is successful when:

- the entire application feels like one coherent product;
- all pages share the same spacing system;
- colors are consistent;
- typography is consistent;
- navigation is consistent;
- active states are immediately recognizable;
- semantic states are consistently represented;
- cards look like members of the same component system;
- forms use the same input language;
- buttons have predictable hierarchy;
- dark surfaces have sufficient contrast;
- no page looks like an untouched legacy screen;
- responsive behavior is intentional;
- no major business functionality was altered.

---

# 35. Reference Comparison

Agents should periodically compare the implementation against the reference using these dimensions:

| Dimension | Target |
|---|---|
| Overall mood | Dark / minimal / professional |
| Density | Medium / spacious |
| Sidebar | Persistent / compact |
| Header | Minimal |
| Cards | Dark / bordered / rounded |
| Borders | Subtle |
| Shadows | Minimal |
| Primary accent | Blue |
| Success | Green |
| Error | Red |
| Typography | Clean / modern |
| Icons | Minimal line icons |
| Radius | 12px panels, 8px small elements |
| Animation | Subtle |
| Content spacing | Generous |

The goal is consistency of the visual language, not pixel-level reproduction.

---

# 36. Rules for later UI changes

- Change tokens in `DesignTokens.cs` only.
- Status uses `StatusChip` and `ChipPalette`.
- Chrome uses `IStringLocalizer<Web>`.
- Razor calls Application handlers, not `BeaconDbContext`.
- New screens ship empty, loading, and error states.
- The drawer collapse below `Breakpoint.Md` stays intact.
- Do not add a second geometry document.

---

# 37. Important Agent Instruction

The reference image defines the **visual direction**, not an exact implementation.

When the existing application has a better established pattern for a specific domain element, preserve its functionality and adapt its appearance to the new visual language.

Do not force every screen to look like the reference dashboard.

The final product should look as if the reference design system was originally created for the existing application.