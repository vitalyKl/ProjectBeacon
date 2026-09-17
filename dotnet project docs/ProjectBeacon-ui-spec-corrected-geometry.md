# ProjectBeacon UI Spec — Corrected Geometry, Transcribed From Source

This corrects the geometry/spacing sections of the earlier spec, which was
reconstructed from memory and Tailwind convention instead of read from the
actual mockup source. Every value below is copied from the literal
`widget_code` of the two Visualizer mockups (`projectbeacon_task_detail_mockup`,
`projectbeacon_app_shell_mockup`), not reconstructed. Where the two mockups
disagree with each other, or where a value references the Visualizer's own
internal design tokens whose resolved px/hex I don't actually have, that's
flagged explicitly rather than guessed — the whole point of this pass is to
stop doing that.

## What was wrong in the original text spec

| Original spec said | Mockup source actually has |
|---|---|
| Panel radius: 8px (`rounded-lg`) | **12px**, literal, both panels and the outer app-shell frame |
| One radius tier for everything else | **Two tiers**: 12px for panels/frame, a smaller `var(--radius)` token for cards/nav items/badges/message bubbles/tool chips — I don't have this token's resolved px value, see "Unresolved" below |
| One surface level (`--surface`) | **Two surface levels**: `var(--surface-2)` for floating panels (context brief, chat, changed-scope), `var(--surface-1)` for the sidebar background and board cards — this hierarchy never made it into the text spec at all |
| Uniform `p-4` (16px) panel padding | Panel padding is `1rem 1.25rem` — **16px vertical, 20px horizontal, not equal** |
| Single 16px icon size | Icons are contextual: 16px (section/nav icons), 15px (file-row icons), 14px (tool-chip icon) — collapsed to one value in the original spec |
| No stated border width | `0.5px solid` throughout, deliberately hairline, never mentioned |

## Corrected token table

### Radius
| Element | Value | Source |
|---|---|---|
| Panels (context brief, chat, changed-scope), outer app-shell frame | `12px` | Both mockups, literal |
| Cards, nav items, badges, message bubbles, tool-call chips, command-palette hint | `var(--radius)` — **unresolved, see below** | Both mockups |

### Surface (background) hierarchy
| Level | Used for |
|---|---|
| `var(--surface-2)` | Floating panels: context brief, changed-scope, chat panel |
| `var(--surface-1)` | Sidebar background, board cards, chat message bubbles (non-human), budget-track background |
| `var(--bg-accent)` | Human chat message bubble, active nav item background |

### Border
Every border in both mockups is `0.5px solid var(--border)`, no exceptions —
this is a deliberate hairline weight, not a default. One exception:
`border:0.5px solid var(--border-accent)` on the currently-active task card
in the board (a highlighted variant, not a different weight).

### Spacing — corrected per-context, not one flat `p-4`
| Context | Padding |
|---|---|
| Panel body (context brief, chat, changed-scope) | `1rem 1.25rem` (16px / 20px) |
| Board column area | `1rem 1.25rem` (16px / 20px) |
| Sidebar | `1rem 0.75rem` (16px / 12px) |
| Topbar / breadcrumb row | `0.85rem 1.25rem` (13.6px / 20px — yes, an odd number; that's what's in the source, round to 14px if that bothers you but note the change) |
| Board card | `10px` (flat, all sides) |
| Nav item, command-palette hint | `7px 8px` |
| Chat message bubble | `8px 12px` |
| Tool-call chip | `4px 8px` |
| Badge/pill | `2px 8px` |

### Gaps
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

### Type scale (reconstructed from actual usages, not previously written down anywhere)
| Size | Used for |
|---|---|
| `11px` | Badge/pill text, small status text on active card |
| `12px` | Muted secondary text, counts, tool-chip text, command-palette hint |
| `13px` | Body text on cards, code snippets, nav item labels, column headers |
| `14px` | Section-row labels, chat sender name |
| `16px` | (icon size, not text — see icons above) |

### Icon sizes
| Context | Size |
|---|---|
| Section-row icons (context brief), nav icons, send-button icon | `16px` |
| Changed-scope file-row icons | `15px` |
| Tool-call chip icon | `14px` |

### Component-specific geometry
- **Status dot** (chat header, active-card indicator): `8px × 8px` in the chat
  header, `6px × 6px` on the board card — **two different sizes for the same
  concept**, worth picking one (recommend 8px, matches the more prominent
  chat-header usage) rather than carrying both forward.
- **Avatar**: `26px × 26px`, `border-radius: 50%`, `11px` initials text.
- **Send button**: `36px × 36px` square, icon-only, `padding: 0`.
- **Budget track**: `6px` tall, `border-radius: 4px` — a **third** radius
  value, smaller than both tiers above; this one is small enough it's
  arguably just "fully rounded for a 6px-tall bar" rather than a real third
  tier — treat it as `border-radius: 9999px` (pill) rather than a literal
  `4px` token.
- **Sidebar width**: `168px` — this one was already correct in the original
  spec, transcribed correctly the first time.

## Unresolved — needs a decision, not a guess

`var(--radius)` (the smaller tier, used for cards/nav/badges/bubbles/chips)
is the Visualizer's own internal design-system token — I drew the mockup
using it, but I don't have its resolved pixel value; it's whatever the
Visualizer's base stylesheet defines it as, not something I chose. Rather
than guess a number and risk repeating exactly the mistake this whole
correction is about, this needs an explicit choice: pick a value for the
smaller radius tier (something in the 6–8px range would sit comfortably
below the panels' 12px) and treat it as fixed from that point on — but pick
it as a decision, not as a recollection of what I "probably" meant.

## What to do with this file

Replace the radius/spacing/surface sections of
`UI Design Migration Specification.md` with the tables above. The
MudBlazor component mapping in that file (which `Mud*` component to use for
what) is a separate concern from geometry and doesn't need to change — only
the numbers do.
