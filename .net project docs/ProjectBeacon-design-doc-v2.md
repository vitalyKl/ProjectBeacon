# ProjectBeacon — Design Document v2 (.NET rebuild)

Supersedes the original `docs/design.md`. This is a from-scratch rebuild —
Blazor Server + MudBlazor, ASP.NET Core, EF Core — informed by an extensive
empirical review of the TypeScript implementation and a series of
architecture discussions. Where a decision below traces directly to
something found or decided during that process, it's noted — not for
ceremony, but so a future reader (human or agent) knows which rules are
load-bearing lessons and which are fresh judgment calls that could still be
revisited.

## 1. Vision

A control plane for projects where the people doing the work are a mix of
a human and one or more coding agents, running locally, on hardware the
human controls. The problem Beacon exists to solve: an agent starting a
task cold has to rediscover context every session — what's already decided,
what's off-limits, what's relevant in the codebase — usually by grepping
around and guessing. Beacon compiles that context once, keeps it current,
and hands it to the agent as a budgeted brief instead of leaving the agent
to reconstruct it by trial and error.

The target user is a solo developer or small team running local agents
(Qwen, Claude, or similar, via OpenCode or comparable harnesses) against
their own codebase — not a hosted SaaS product for teams who don't control
their own compute, at least not yet. Positioning follows from that: Beacon
is a relay, an orchestrator, and a system of record. It is never the thing
that executes an agent's turn.

## 2. Non-goals — the hard boundary, not a wish list

These aren't aspirational; they're the line that keeps the architecture
honest, and they've already shaped real decisions in this conversation
(the MCP-only file access design in §6 stays consistent with this because
of it):

- **No Beacon-hosted coding agent.** The agent process always runs on
  hardware the human controls. Beacon relays and orchestrates; it never
  spawns or owns the execution of an agent turn. This is why the live-chat
  design (§7) routes through a local daemon rather than a hosted sandbox,
  and why bash/shell execution stays local even when file access moves
  behind MCP (§6) — running `dotnet test` inside Beacon's own
  infrastructure would cross this line.
- **No new UI component library.** MudBlazor is the component system.
  Don't hand-roll a parallel Button/Panel/Chip set — the earlier TypeScript
  build made exactly this mistake once already (a stub `packages/ui` that
  got ignored in favor of ad hoc styling everywhere else); don't repeat it
  in the new stack by writing custom Razor components MudBlazor already
  provides.
- **Hosted multi-tenant is a later, explicitly gated phase**, not a
  parallel track built alongside self-host from day one. See §9.

## 3. Architecture

Four pieces, each with one clear job:

- **API** (ASP.NET Core) — the only writer to the database. Owns auth,
  projects, tasks, context/decisions/constraints, sessions, and the MCP
  tool surface's server-side implementation.
- **Web** (Blazor Server + MudBlazor) — the human-facing UI. Screens live
  under `Features/{Name}`. They call Application handlers in-process and
  hold no independent store. The Web host also maps the `/v1` API so
  agents and the UI share one process on `:5083`.
- **Local agent daemon** — a single implementation, run by the developer,
  that gives an agent (a) MCP tools scoped to one project and (b) a code
  index over the local working tree. This replaces what the TypeScript
  build had split into three independently-written, independently-drifted
  copies of the same "cache a code index per repo" logic — one of which
  never got a staleness fix applied to it at all despite being the one
  actual agents were most likely talking to. **One implementation, one
  place to get freshness right, no drift possible between copies.**
- **Worker** — background jobs only (GitHub sync, scheduled cleanup). Not
  a second code-serving path; if a self-hosted deployment needs remote code
  access without a local daemon, that's an explicit, separate design
  problem to solve later, not something the worker quietly grows into
  doing without a plan.

## 4. Context compilation — what a brief must actually contain

The brief an agent receives when it starts a task is the product's central
mechanism, so it's worth being precise about what "done" means for it,
having found several ways the previous implementation fell short of its
own intent:

- **Code orientation is not optional.** A brief for a task with a linked
  repo includes a tree snapshot and a changed-scope file list by default —
  not behind a flag nobody can set because the tool schema never exposed
  it. An agent's first move after claiming a task should be reading a file
  the brief already pointed at, not grepping to find out what exists.
- **Decisions carry their consequences.** A decision in a brief states not
  just what was decided but what breaks if it's ignored. Omitting this
  turned every "decision" in a brief into an unexplained rule an agent had
  no reason to respect over its own judgment.
- **Constraints are structured, and code that reasons about them uses the
  structure.** A constraint has a `kind` (`must` / `must_not` / `security`
  / `compliance`) as data, not as something to be re-derived by pattern-
  matching the constraint's text. Anything that needs to know whether two
  constraints conflict reads `kind`, in whichever language the constraint
  happens to be written in — it does not run English keyword regex against
  free text and call that a substitute for a field that already exists.
- **Token budgeting uses a real tokenizer.** A char-count heuristic
  calibrated on English text underestimates non-Latin script by roughly a
  third, measured directly against a real BPE tokenizer during this
  project's review — which is exactly backwards, since it means the budget
  system fails silently in the direction of *not* catching overflow, for
  whatever language the team actually writes in. Budget math must reflect
  real token counts, not an approximation tuned for one language and
  applied everywhere.
- **The code index is never silently stale.** One implementation (§3)
  means one fix, applied once: re-index cheaply and unconditionally before
  serving a query rather than trying to out-guess when a reindex is needed
  with a heuristic that can miss an ordinary file edit.
- **Definition of Done lives in the constraint system, not in a file on
  disk.** Explicitly rejecting the AGENTS.md-per-repo model for this: a
  "run tests before considering this done" rule is a project-level
  `must` constraint, scoped and versioned like any other, that the
  compiler includes in every brief automatically — never a document a
  human has to remember to copy into each new repository. The entire
  reason Beacon's context system exists is to make this kind of standing
  rule live in one place and travel with the task automatically; falling
  back to scattered on-disk docs for the most important rule of all (when
  is work actually finished) would undermine the product's own premise.
- **The brief tells the agent which tools to prefer, explicitly, every
  time.** Not a standing instruction the agent has to remember from
  connection time — a `## Tools for this task` section generated alongside
  the changed-scope list, naming the specific indexed-search tools that
  apply to this task's files. Task-level, contextual instruction beats a
  generic connection-time rule the model has to recall unprompted.

## 5. The task lifecycle

1. **Claim.** An agent asks for the highest-priority open task. For a
   single agent this is a list-then-start sequence; for multiple agents
   working concurrently, claiming needs to be atomic (a single operation
   that lists and locks together), not list-then-start as two separate
   calls racing against another agent doing the same thing.
2. **Get context.** `start_work` compiles and returns the brief per §4 —
   fixed to actually be complete, not the aspirational version of itself.
3. **Work.** The agent edits code under the Definition-of-Done constraints
   already present in the brief (§4) — it doesn't need to be told
   separately what "done" means, because that rule travelled with the task.
4. **Self-review, blind.** Before calling `finish_work`, the brief requires
   dispatching a cold, isolated reviewer subagent against the diff — no
   task context, no intent, just the diff and the neutral failure-shape
   prompt (see the `cold-diff-review` skill). This is not the same agent
   re-reading its own work in the same context; a reviewer told the intent
   confirms the intent, which is the whole reason self-review misses things
   self-review is structurally blind to. Findings get verified by reverting
   the relevant hunk before being trusted, same as the skill specifies.
5. **Structured confirmation, not prose.** `finish_work` accepts a
   structured review field — reviewer run, regressions found, regressions
   fixed — not a free-text summary a human has to trust at face value.
   The alternative is the same failure mode found in an unrelated local
   model earlier in this project's life: an agent stating it verified
   something it never actually checked. A structured field that has to be
   populated with real values is a much smaller surface for that to hide
   in than a paragraph of prose.
6. **Land the change.** A normal git push and PR, separate from the
   `finish_work` call — Beacon links a PR to its task by matched changed
   files (boundary-aware path matching, not a bare prefix match that can
   misattribute a sibling directory's changes to the wrong task) and shows
   the diff next to the brief that produced it, so a human reviewing later
   can see what was promised against what actually shipped.

## 6. File access — MCP as the only channel, deliberately

The default assumption in the earlier build was that an agent has native
file tools and *might* prefer Beacon's indexed ones if reminded often
enough. This rebuild inverts that: the agent shouldn't know where the
project lives on disk at all, and shouldn't have a choice to make.

- **Reads and edits go through MCP tools exclusively**, enforced by
  denying the agent host's native file tools outright (`permission: {
  read: deny, edit: deny, glob: deny, grep: deny }` in the agent harness
  config) rather than asking the model to prefer one path over another.
  A model with no file tools in its list can't reach for the wrong one —
  this removes the choice instead of hoping the model makes it correctly,
  which is a categorically stronger guarantee than anything achievable
  through prompting alone.
- **This requires a write path that doesn't exist yet.** The MCP tool
  surface up to this point has been read-only by design. Making file
  access MCP-exclusive is not possible until a `write_file`/`apply_patch`
  tool exists — this is a prerequisite, not a detail, and it's the actual
  first build step for this idea, ahead of any client/daemon work.
- **Shell execution stays local, explicitly, because of §2.** Running
  `dotnet build`/`dotnet test`/`git` needs to happen somewhere with real
  process execution, and routing that through Beacon would mean Beacon
  executing code on the project — the exact thing the non-goals rule out.
  The boundary is precise: files through MCP, shell local. Not everything
  local, not everything remote.
- **This is a real trade-off, not a free upgrade — say so.** Once MCP is
  the only path to a file, the quality of the MCP layer stops being able
  to degrade gracefully (an agent falling back to grep when a tool call
  looks wrong) and becomes a single point of failure for the agent's
  entire ability to work. Given how much of this project's own review
  turned up real defects in exactly that layer, treat every future change
  to the MCP tool surface with the seriousness that "this is now the only
  way in" implies — a bug here doesn't degrade the experience, it stops
  the agent cold.

## 7. Live sessions and remote control

A locally-running daemon (an evolution of the CLI, not a new Electron/
MAUI client) holds the outbound connection to the API, drives the agent
process, and is the thing that would eventually let a task's chat stay
open in the web UI across a session rather than requiring a terminal to
stay attached. Scope for this phase: extend the existing local daemon
concept, not build a new always-connected client from scratch — a session
stays `active`/`paused` across turns and only closes when the task itself
reaches a terminal state, matching how session locking already models
"not done yet, but this leg of work is over" today. Full remote-driving
(triggering a turn from a phone, say) is real future work, not something
this rebuild needs to solve immediately — but the daemon should be shaped
so that's an extension later, not a rewrite.

## 8. Multi-agent coordination — the actual differentiator

This is worth building well specifically because generic project trackers
don't have this problem — they were built assuming a human is the unit of
work, where "who's doing what" gets solved by a standup, not software.

- **Live session visibility** — which agent is on which task, right now,
  not just a retrospective activity log.
- **Context lint that uses real signals.** A milestone/task orphan check
  that actually inspects the milestone's completion state against its
  full task set — not a check wired to a single task's status by accident,
  which is what "checks for orphaned work" quietly became in the earlier
  build once the actual wiring was traced.
- **Diff-aware task linking with correct path semantics** — a changed
  file belongs to a task if it's under a linked path or an exact match,
  never merely if it starts with the same string. Two different top-level
  directories sharing a prefix should never cross-attribute a PR.

## 9. Security model — self-host first, hosted gated behind proof

- **Tenant isolation, proven, not assumed.** If EF Core global query
  filters are the isolation mechanism, they need the equivalent of what
  Postgres RLS's `FORCE ROW LEVEL SECURITY` provides — i.e., verified to
  actually apply even to the app's own privileged access path, not just
  present in the model and trusted to work. The concrete lesson from this
  project's own history: a correctly-defined isolation policy that the
  application's own database role silently bypasses is not a smaller bug
  than having no policy — it's the same bug with better cosmetics. Prove
  isolation with a real cross-tenant test before trusting it, not by
  reading the policy definition and confirming it looks right.
- **Every auth entry point gets the same treatment, uniformly.** Login,
  registration, and bootstrap all get rate limiting — not two out of three
  because the third was added later and the pattern wasn't reapplied.
- **Capabilities are scoped to what they actually need.** A capability
  that can drive a live agent session is not the same as one that can
  write tasks — keep them separate so a leaked token's blast radius is
  legible, especially now that a "drive session" capability can trigger
  real code execution on a human's machine (§7), which is a materially
  different risk than editing a task title.
- **Hosted multi-tenant is a phase, not a parallel default.** Ship and
  dogfood self-host, single-tenant, first. Multi-tenant isolation gets
  proven — really proven, per the point above — before any hosted signup
  path opens, not as a nice-to-have alongside it.

## 10. Proving the core thesis

The product's entire value proposition rests on "a compiled brief helps an
agent more than raw exploration." That claim needs to be measured, not
asserted. An eval harness that replays hand-authored fixture numbers rather
than actually driving an agent twice (with and without a brief) and
recording what happened produces a report that looks like evidence and
isn't — worse than having no such report at all, because it can be mistaken
for one. The real version drives an agent through both conditions and
records real token counts, real turn counts, real pass/fail — and the
in-product "context cost" report shows that, not a number that could have
been typed into a JSON file by anyone.

## 11. UI

Blazor Server, MudBlazor, monochrome accent palette (no introduced brand
color — a decision made explicitly, not a default kept by omission),
`PaletteLight`/`PaletteDark` mapped from the token table established during
this project's design work, `DefaultBorderRadius` at 8px for panels with
buttons/inputs visually distinct at 6px. Shipped drawer (nine items):
Dashboard, Board, Backlog, Roadmap, Context, Decisions, Agents, Reports,
Settings. Learn and Files are not shipped. Culture is a cookie
(`.AspNetCore.Culture` via `GET /culture`), not custom JavaScript. The
compiled brief's token budget is a visible, real-time UI element (a
`MudProgressLinear` on Context and Task Detail, not a hidden backend
number) — the point of a *budgeted* brief is that a human can see what an
agent will actually receive, and that's wasted if it's invisible.

## 12. Agent discipline — first-class, not optional add-ons

Two standing skills are treated as part of the system, not user-supplied
extras:

- **`beacon-tool-discipline`** — never state a tool is unavailable without
  having called it in the same turn; prefer Beacon's MCP tools by name and
  by reason, not just by suggestion; never claim to have visually verified
  something a text-only model cannot see.
- **`cold-diff-review`** — required before `finish_work` (§5, step 4), run
  as a genuinely isolated subagent, findings verified by reversion before
  being trusted, racy findings held to a stress-test/interleaving-trace bar
  rather than a single revert-and-observe pass.

Both exist because of the same underlying pattern, observed more than once
during this project's review: an agent's unverified claim about its own
work is not evidence, however confidently it's phrased. Every mechanism in
this document that turns a claim into a structured, checkable fact — the
`finish_work` review field, the token-budget meter, the boundary-aware path
matcher, the real tokenizer — is the same fix applied at a different layer.

## 13. Explicitly deferred

Hosted multi-tenant (§9), full remote-driven sessions beyond a local
daemon (§7), GitLab/Bitbucket/Azure DevOps integrations, Learn/Files
screens, and a command palette. Board, Backlog, Roadmap, Context,
Decisions, Reports, Settings, and Task Detail are shipped. None of the
deferred items are rejected — they're sequenced behind the foundation
above being solid first.
