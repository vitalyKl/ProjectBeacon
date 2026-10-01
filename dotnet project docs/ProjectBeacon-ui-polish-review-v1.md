# ProjectBeacon — UI Polish Review v1

## Purpose

This document records the UI issues found during the current visual/code review after Sprint 10.

It is a **post-refactor polish review**, not a replacement for the Master Roadmap or the UI/UX roadmap.

The current implementation is the source of truth. The screenshot baseline referenced by this review is the checked-in Playwright baseline.

## Review baseline

Reviewed against:

- repository: `vitalyKl/ProjectBeacon`
- branch: `main`
- review baseline commit: `3bb435041a2401ae7978ef914acf392e2394df5e`
- visual baseline: `tests/playwright/baseline/`
- target viewports: desktop 1280/1440, tablet 768/1024, mobile 360/390

## Already addressed

The following findings from the previous UI review are considered resolved or substantially addressed:

- Settings was split into focused routes: `/settings`, `/settings/agents`, `/settings/workstations`, `/settings/connections`.
- Mobile Board now uses a single visible Kanban column with a status selector.
- Board and Task Detail received a clearer action hierarchy.
- Knowledge/table screens gained mobile-friendly labels.
- Typography and spacing are centralized through design tokens.
- Shared Beacon UI primitives are used consistently.
- Screenshot baselines are checked in for desktop, tablet and mobile.

These should not be reintroduced as broad refactor work unless a regression is discovered.

# Open findings

## UI-001 — Mobile Chat FAB overlaps page content

**Priority:** P1  
**Status:** Open  
**Affected:** mobile 360/390

### Evidence

The floating Chat button is fixed to the lower-right corner. On the current mobile baselines it overlaps usable content, including the Settings/Authenticator area and lower Dashboard/Workstation content.

### Problem

The fixed control does not reserve a corresponding safe content area.

### Required change

Keep the floating interaction, but guarantee a safe visual/content inset on narrow screens. Account for FAB size, bottom/right spacing, mobile safe-area insets, expanded chat dock, and pages with controls near the viewport bottom.

### Acceptance criteria

- FAB does not cover interactive content at 360px or 390px.
- Keyboard/focus navigation is not obstructed.
- Expanded Chat Dock does not hide the active input/action area.
- Desktop behavior remains unchanged.

---

## UI-002 — Duplicate active navigation state for Settings/Agents

**Priority:** P1  
**Status:** Open  
**Affected:** desktop and responsive layouts

### Evidence

`MainLayout.razor` uses prefix matching for both `/settings` and `/settings/agents`. Therefore `/settings/agents` matches both links. The current Agents baseline shows both **Agents** and **Settings** visually active.

### Required change

Make the parent Settings route active only for the exact `/settings` route while preserving explicit active state for `/settings/agents`, `/settings/workstations`, and `/settings/connections`.

### Acceptance criteria

- `/settings` activates only Settings.
- `/settings/agents` activates only Agents.
- `/settings/workstations` activates only Workstations.
- `/settings/connections` activates only Connections.
- Breadcrumbs remain correct.

---

## UI-003 — Task Detail metadata spacing is visually broken

**Priority:** P1  
**Status:** Open  
**Affected:** Task Detail

### Evidence

The current baseline renders task metadata effectively concatenated:

`Done 9/17/2026Created At 9/16/2026`

### Problem

The two metadata values are visually merged and difficult to scan.

### Required change

Use an explicit metadata layout rather than adjacent inline text. Prefer a compact metadata row on desktop and stacked metadata on narrow screens, with consistent secondary/caption typography.

### Acceptance criteria

- No concatenated labels/values.
- Metadata remains readable at 360px.
- Date formatting is consistent across Task Detail.
- No page-specific typography hacks are introduced.

---

## UI-004 — Desktop Board columns reserve excessive empty height

**Priority:** P2  
**Status:** Open  
**Affected:** desktop Board

### Evidence

`app.css` currently uses `.kanban-zone { min-height: 60vh; }`. The desktop baseline shows very large empty columns when only a small number of tasks exists.

### Problem

The board reads as three large empty panels instead of a task workspace.

### Required change

Reduce forced empty space while preserving a usable drop target. Prefer a practical minimum drop area, natural growth with content, and consistent column alignment.

### Acceptance criteria

- Small boards no longer create excessive empty vertical space.
- Drag/drop target remains clearly usable.
- Three columns remain aligned on desktop.
- Mobile one-column behavior is preserved.

---

## UI-005 — Excessive card/border nesting remains

**Priority:** P2  
**Status:** Open  
**Affected:** Dashboard, Task Detail, Agents, Workstations, Context

### Problem

Several screens still combine outlined MudPaper, Beacon panels, nested cards, and additional outlined blocks. The design system is centralized, but visual hierarchy still relies on borders too frequently.

### Required change

Do not perform a broad visual rewrite. Review nested containers and remove an outer/inner border when the grouping is already clear through spacing, typography, background level, or section heading.

### Acceptance criteria

- Primary content groups remain distinct.
- Nested containers are used only for meaningful secondary objects/states.
- Structural clarity is preserved.
- Existing design tokens remain the source of geometry/color values.

---

## UI-006 — Mobile Task Detail tab navigation is difficult to scan

**Priority:** P2  
**Status:** Open  
**Affected:** mobile Task Detail

### Problem

The narrow layout compresses the tab strip so that only part of the tab set is immediately visible, while navigation arrows/overflow provide limited context.

### Required change

Improve narrow-screen tab discovery without creating a completely separate mobile implementation. Possible approaches include deliberate horizontal scrolling, clearer scroll affordance, stronger active-tab framing, or a compact mobile selector when the tab count exceeds available width.

### Acceptance criteria

- Current tab is obvious.
- All five sections remain reachable.
- The first screen does not misleadingly suggest that only one or two tabs exist.
- Keyboard navigation remains usable.

---

## UI-007 — Beacon-specific visual identity can be strengthened

**Priority:** P3  
**Status:** Open  
**Affected:** product-wide

### Observation

The current design system is coherent, but many pages still read primarily as a polished dark administrative UI. ProjectBeacon has product-specific concepts that can carry more visual weight: Agent runtime, Pipeline, Workstation state, Context budget, MCP, review/evaluation lifecycle, and model/runtime state.

### Required direction

Gradually make these concepts more visually distinctive without changing the overall dark/minimal visual language. This is not a request for decorative UI; the goal is to make Beacon's core operational model immediately recognizable.

### Acceptance criteria

- Runtime and pipeline states have a strong but restrained visual language.
- Context/token state is visually scannable.
- Agent/workstation/runtime relationships are easier to understand.
- No decorative dashboard noise is introduced.

---

# Recommended implementation order

1. UI-002 — duplicate active navigation
2. UI-003 — Task Detail metadata spacing
3. UI-001 — mobile Chat FAB safe area
4. UI-004 — Board column height
5. UI-005 — card/border density
6. UI-006 — mobile Task Detail tabs
7. UI-007 — Beacon-specific visual identity

## Agent execution rule

These findings are **implementation tasks**, not execution phases.

Agents must not treat this document as a replacement for the Master Roadmap.

Correct flow:

`Current Master Sprint -> relevant roadmap section -> implementation -> screenshot verification -> update this review`

When a finding is fixed:

- mark its status as `Fixed`;
- record the relevant commit;
- re-run the relevant screenshot baseline;
- verify desktop/mobile impact.

Do not close a finding based only on code inspection when the issue is visual.

## Verification requirements

For visual changes, verify at minimum:

- desktop 1440;
- mobile 390.

For responsive/layout changes, also verify:

- desktop 1280;
- tablet 768;
- tablet 1024;
- mobile 360.

A fix must not introduce a regression in another viewport.
