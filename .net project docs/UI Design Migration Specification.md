# UI Design Migration Specification

## 1. Purpose

Apply the visual language and interaction principles of the provided reference UI to the existing application without changing its business logic, domain model, API contracts, routing semantics, or existing functionality unless explicitly required.

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

Suggested desktop width:

```text
240–320px
```

The exact width must be determined according to the existing application.

Do not force a fixed width if the current application has a responsive navigation system.

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

Suggested semantic tokens:

```text
--color-bg-app
--color-bg-sidebar
--color-bg-header
--color-bg-surface
--color-bg-surface-hover
--color-bg-surface-active
--color-border
--color-border-subtle
```

Approximate visual direction:

```text
Application background:
#181818

Sidebar:
#141414

Surface/card:
#171717 / #1A1A1A

Border:
#2A2A2A

Strong border:
#333333
```

Exact values must be validated against the reference and adjusted globally through design tokens.

Do not scatter literal color values throughout components.

---

# 9. Design Tokens

Create a centralized design-token layer.

At minimum define:

## Colors

```text
background
background-secondary
surface
surface-hover
surface-active
border
border-subtle

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

Use a consistent spacing scale.

Recommended conceptual scale:

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

Components should use the spacing scale rather than arbitrary values.

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

Recommended hierarchy:

```text
Page title:
28–32px

Section title:
22–26px

Card title:
18–20px

Body:
14–16px

Secondary:
13–14px

Caption:
12–13px
```

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

- dark surface;
- subtle border;
- 12–16px radius;
- minimal/no shadow;
- internal padding;
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

- compact;
- rounded;
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
dark surface
subtle border
12–16px radius
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

Recommended sizes:

```text
Navigation: 18–20px
Button:     16–18px
Inline:     14–16px
```

If the project already contains an icon system, reuse it.

Do not introduce a second icon library without a clear reason.

---

# 25. Border Radius

Use a consistent radius system.

Suggested:

```text
small:
6–8px

medium:
10–12px

large:
14–16px

pill:
9999px
```

Cards and major surfaces should generally use the medium/large radius.

---

# 26. Shadows

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

# 27. Motion

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

# 28. Responsive Behavior

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

Define explicit responsive behavior for each major component.

---

# 29. Component Hierarchy

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

# 30. Existing Pages

Agents must classify every existing page into one of these categories:

```text
Dashboard
List
Details
Create/Edit
Settings
Authentication
Reports
Administration
Monitoring
Error/Status
Other
```

For each page identify:

- page title;
- primary action;
- secondary actions;
- main content;
- supporting information;
- interactive elements;
- empty state;
- loading state;
- error state.

Then apply the design system.

---

# 31. Migration Strategy

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

# 32. Agent Rules

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

# 33. Visual Acceptance Criteria

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

# 34. Reference Comparison

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
| Radius | Medium rounded |
| Animation | Subtle |
| Content spacing | Generous |

The goal is consistency of the visual language, not pixel-level reproduction.

---

# 35. Definition of Done

The migration is complete only when:

- [ ] Existing project architecture has been inspected.
- [ ] UI migration plan was created.
- [ ] Global design tokens exist.
- [ ] Application shell has been migrated.
- [ ] Sidebar has been migrated.
- [ ] Header has been migrated.
- [ ] Core reusable components have been migrated.
- [ ] At least one representative page has been completely migrated and reviewed.
- [ ] Remaining pages have been migrated.
- [ ] Responsive behavior has been checked.
- [ ] Loading states have been checked.
- [ ] Empty states have been checked.
- [ ] Error states have been checked.
- [ ] Focus states have been checked.
- [ ] Accessibility has been checked.
- [ ] Legacy conflicting styles have been removed or isolated.
- [ ] Build succeeds.
- [ ] Existing tests pass.
- [ ] No business logic was unintentionally changed.
- [ ] No duplicated design tokens remain.
- [ ] No page contains unnecessary hard-coded colors or spacing.
- [ ] Final UI has been visually reviewed against the reference.

---

# 36. Important Agent Instruction

The reference image defines the **visual direction**, not an exact implementation.

When the existing application has a better established pattern for a specific domain element, preserve its functionality and adapt its appearance to the new visual language.

Do not force every screen to look like the reference dashboard.

The final product should look as if the reference design system was originally created for the existing application.