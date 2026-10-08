# UI-0 Design Contract

Phase UI-0 only. No Razor, token, or route changes in this phase.

Runtime color, spacing, radius, and type values stay in `ProjectBeacon.Web/Theme/DesignTokens.cs` until a later phase edits that class. Do not copy hex into Razor. Do not add a second CSS variable system. This file is the refactor target, not a runtime token source.

## Authority

When sources disagree, use this order:

1. Production behavior: existing routes, authorization, localization, handlers, and domain enums. Do not invent screens, statuses, or mock data to match the prototype.
2. `PROJECTBEACON-UI-UX-SPEC.md` for architecture and phase rules.
3. Visual target: `reference/figma project files/src/index.css` and composition in `reference/figma project files/src/App.tsx`.
4. `reference/figma-screens/*.png` for screenshot comparison. Those PNGs match the light `:root` theme (light canvas, dark sidebar), not `[data-theme="dark"]`.
5. `reference/figma-screens/FigmaMake-App.tsx` is obsolete. Do not use it.
6. `dotnet project docs/UI Design Migration Specification.md` remains the shipped-system description until tokens change. Its blue `#3B82F6`, 168px drawer, and dark-only canvas are the current app, not this target.

Do not port React, Vite, Tailwind, or the Figma project into the solution. Do not run that package as part of ProjectBeacon.

## Visual target

The screenshot target is a light operational canvas with a dark persistent sidebar and a violet accent. The Figma file also defines a full dark theme. Production today is dark-only (`DesignTokens` comment, `BeaconTheme` `IsDarkMode`).

Decision: later visual phases match the PNGs (light content, dark sidebar, violet accent). The dark token column below is recorded so a theme toggle can be added without another extraction. UI-1 does not flip the live theme by itself; token adoption is an explicit step inside the shell phase, still only through `DesignTokens.cs` and `BeaconTheme.cs`.

Figma body type is Inter at 13px / 1.45. IBM Plex Mono is only for metrics, ids, elapsed time, kbd, and telemetry. Do not make the UI monospace. MudBlazor icons stay; do not add the prototype SVG set.

### Token map

Shipped name is the `DesignTokens` constant. Figma name is the CSS variable in `index.css`. Adopt Figma values only by editing `DesignTokens.cs`.

| Shipped | Figma | Light (`:root`, PNG target) | Dark (`[data-theme=dark]`) |
|---|---|---|---|
| `BgApp` | `--bg` | `#f3f5f7` | `#0f1218` |
| `Surface2` | `--surface` | `#ffffff` | `#171b23` |
| `Surface1` | `--surface-2` | `#f7f8fa` | `#1c212b` |
| — | `--surface-3` | `#eceff3` | `#242a35` |
| sidebar | `--sidebar` | `#10141c` | `#0a0d12` |
| switcher | `--sidebar-2` | `#171c26` | `#11151c` |
| `TextPrimary` | `--text` | `#161b22` | `#edf0f5` |
| `TextSecondary` | `--text-2` | `#535d6c` | `#a3adbb` |
| `TextMuted` | `--text-3` | `#818b99` | `#707b8b` |
| `Border` | `--line` | `#dfe3e8` | `#2b323e` |
| `BorderStrong` | `--line-strong` | `#cbd1d9` | `#3a4351` |
| `Accent` | `--accent` | `#625cf6` | `#8883ff` |
| accent soft | `--accent-soft` | `#ecebff` | `#29284c` |
| `AccentHover` | `--accent-strong` | `#4f48dc` | `#aaa7ff` |
| `Success` | `--success` | `#168567` | `#52c69f` |
| `SuccessBackground` | `--success-soft` | `#e6f5f0` | `#16372f` |
| `Warning` | `--warning` | `#a56810` | `#e0a64c` |
| `WarningBackground` | `--warning-soft` | `#fff3da` | `#3b2d18` |
| `Danger` | `--danger` | `#cf4d55` | `#ef747b` |
| `DangerBackground` | `--danger-soft` | `#fdecee` | `#442329` |
| `Info` | `--info` | `#2877c7` | `#70abe6` |
| `InfoBackground` | `--info-soft` | `#e7f1fb` | `#1d3248` |

Figma also uses a soft shadow (`0 1px 2px` plus a wide low-alpha shadow) and radii `--radius-sm: 5px`, `--radius-md: 8px`, `--radius-lg: 12px`. Shipped radii are panel 12px, small 8px, pill. Map sm → controls, md → panels/cards, lg → command dialog. Do not add page-local radii.

Active nav in the prototype is `#232939` with a 2px violet rail, not a blue fill. Shipped drawer width is `BeaconTheme.LayoutProperties.DrawerWidthLeft` (`236px`). Changing width is a shell-phase edit of that one property, not a second width on `MudDrawer`.

## Shell and navigation

Figma labels are visual copy. Routes stay the real ones. Every route that exists today must stay reachable. Do not add `/projects`, `/pipeline`, or `/reviews`.

| Figma group | Figma item | Production destination | Notes |
|---|---|---|---|
| Operate | Overview | `/dashboard` | Relabel only. |
| Operate | Projects | `ProjectSwitcher` plus `/project/settings` | No separate `/projects` index. Create, rename, and delete stay on project settings. |
| Operate | Tasks | `/board` | Backlog `/backlog` and Roadmap `/roadmap` stay in the drawer. |
| Operate | Pipeline | current task pipeline, else `/board` | Pipeline is not its own route. It lives on `/task/{id}`. |
| Intelligence | Agents | `/settings/agents` | `/agents` already redirects here. |
| Intelligence | Workstations | `/settings/workstations` | |
| Intelligence | Context | `/context` | |
| Intelligence | Agent chat | `/chat` | `ChatDock` stays a shell shortcut, not a second product. |
| Govern | Reviews | tasks awaiting review on the board / task pipeline | No reviews index. Decisions `/decisions` and Reports `/reports` stay reachable. |
| Govern | Settings | `/settings` | Also keep `/settings/connections` and `/project/settings`. |

Shell pieces to restyle in UI-1, not rebuild:

- `ProjectBeacon.Web/Shared/MainLayout.razor` — drawer, app bar, breadcrumbs, version.
- `ProjectSwitcher.razor` — move toward the sidebar switcher; keep project selection and auth.
- Fleet summary in the drawer footer — real workstation heartbeat counts, not “4 runtimes”.
- Command palette — new, Ctrl/Cmd+K, real routes and existing actions only. `CommandLifecycle.razor` is command status, not a palette. Do not rename it.

Top bar: page title, subtitle, command trigger, theme toggle, account. Theme is a real preference (Dark, Light, System) stored in the `beacon-theme` cookie. Do not add a notification bell until a real notification source exists.

Drawer still collapses below `Breakpoint.Md`. Figma’s 1100px and 760px queries are composition hints (stack grids, hide secondary columns, bottom nav at small widths). They do not replace `Breakpoint.Md`. Required checks: 1440, 1024, ~768, 390, 360. Below `Md`: drawer overlay, primary actions stay visible, no horizontal page overflow. A bottom nav may mirror Overview, Tasks, Pipeline, Chat, Settings; it must not become a second route table.

Chrome strings go through `IStringLocalizer<Web>`. Figma English is not production copy.

## State vocabulary

One execution-state treatment: label plus icon or shape, not color alone. Extend `StatusChip` / `ChipPalette`. Do not add a parallel status widget that ignores them.

Prototype states and the nearest production facts:

| Prototype | Cue | Production source | Gap |
|---|---|---|---|
| Idle | clock | `ChatPhase` idle; no task-level idle | Do not add a task status. |
| Queued | clock | `WorkstationCommandStatus.Pending`; board column Todo | |
| Running | play | `TaskItemStatus.InProgress`, `SessionStatus.Active`, command Running | |
| Paused | pause | none | Do not invent Paused. |
| Blocked | shield | none as a status | Express with existing failed/review signals only. |
| Failed | close | session, subtask, command, runtime Failed | |
| Awaiting review | review | `TaskPipelineStage.Reviewing` | Not a board column today. |
| Approved | check | `TaskPipelineStage.Approved`, verdict Approve | |
| Completed | check | `TaskItemStatus.Done`, stage Closed | |
| Offline | server, struck label | device offline, `RuntimeHealth.Offline` | |

`TaskItemStatus` stays Todo / InProgress / Done. The prototype’s five board columns (Backlog, To Do, In Progress, Review, Done) are a later board composition (UI-4), built from status plus pipeline stage, not new enum values.

Pipeline rail stages in the prototype (Planning, Executing, Reviewing, Approved, Closed) already match `TaskPipelineStage`. Reuse that enum. ReopenedForRevision stays available even though the prototype omits it.

## Component inventory

Add a primitive only when it has its own meaning and at least two call sites. Compose MudBlazor. Suggested home: `ProjectBeacon.Web/Shared/`, same pattern as `StatusChip`.

| Primitive | Figma class | Build from | First real use |
|---|---|---|---|
| PBPanel | `.panel` | `MudPaper` + existing `.beacon-panel` | Dashboard, task workspace |
| PBMetric | `.metric` | text + mono value | Dashboard strip |
| PBStatus | `.status` / `.execution-state` | `StatusChip` | Everywhere status is shown |
| PBAvatar | `.avatar` | initials from display name | Shell, execution rows |
| PBProgress | `.progress` | `MudProgressLinear` | Running rows, fleet load |
| PBExecutionRow | `.running-row` | grid row | Dashboard running list |
| PBExecutionTimeline | `.timeline` / `.execution-event` | ordered real events | Dashboard activity, task workspace |
| PBPipelineRail | `.pipeline-rail` | existing task-detail stage track | Task detail, dashboard |
| PBRelationChain | `.relation-chain` | links to agent, device, pipeline, task | Task detail |
| PBRuntimeHealthRow | `.health-line` | `RuntimeHealth` + device heartbeat | Drawer footer, dashboard fleet |
| PBAttentionItem | `.attention-item` | link row | Dashboard attention list |
| PBEmptyState | — | `EmptyState.razor` | Keep and restyle; do not add a second empty state |
| PBCommandPalette | `.command-modal` | `MudDialog` or overlay | Shell |

Already enough without a new type: `PageHeader`, `LoadingState`, `ErrorState`, `LanguageSwitcher`, `CommandLifecycle`.

Do not build PB* wrappers that only rename `MudButton`, `MudTextField`, or `MudTable`.

## Page composition (later phases)

Data must come from existing handlers. If a prototype block has no backing query, omit it. Do not show sample agents, runtimes, or percentages.

- Dashboard (UI-3): attention first, then metric strip, running work, attention list, pipeline rail, fleet health, activity. Not an equal card grid. Current `Features/Dashboard/Dashboard.razor` is the page to change.
- Tasks (UI-4): `/board` stays the board. Denser cards. Do not drop backlog or roadmap.
- Task detail (UI-5): header, state-aware actions, relation strip, execution feed, next action. Tabs stay for work that already exists (steps, pipeline, discussion, context). This is the highest-scrutiny screen.
- Agents, workstations, context, chat (UI-6): same surfaces and routes, denser layout.
- Pipeline and reviews (UI-7): stay inside the task. Execution finished is not review finished.
- Settings (UI-8): same forms and permissions.

## Responsive rules

- Desktop ≥1440: sidebar open, dashboard grid with a narrow right column, content max width about 1680px.
- 1024: two-column dashboard; do not drop the attention list.
- ~768 and `Breakpoint.Md`: sidebar becomes a drawer. Metric strip wraps 2×2. Pipeline rail may scroll horizontally. Keep task state and the primary action visible.
- 390 and 360: single column. Hide activity feed before hiding running work or attention. No horizontal overflow on the page itself. Board columns may scroll sideways, one column in view.

Respect `prefers-reduced-motion`. The prototype’s hover lift and pulse are optional and off under reduced motion.

## Out of scope for every UI phase

- New domain statuses, entities, or API fields.
- React, Tailwind, or Figma dependencies in `ProjectBeacon.sln`.
- Fake commands, fake notifications, fake fleet counts.
- Replacing MudBlazor.
- Editing `archive/docs/`.
- Treating this contract as permission to change business rules.

## Phase boundary

UI-0 is done when this contract exists and the next session can implement UI-1 without re-reading `App.tsx` for tokens, nav, or states.

UI-1 shell is in `MainLayout`: Operate / Intelligence / Govern, sidebar project switcher, page heading, fleet footer from `ListDevices`, command palette of real routes (Ctrl/Cmd+K), bottom nav under `Breakpoint.Md`. Light canvas and violet accent are in `DesignTokens.cs` / `BeaconTheme` `PaletteLight`. Feature pages are not restyled. No `/projects`, `/pipeline`, or `/reviews` route was added.
