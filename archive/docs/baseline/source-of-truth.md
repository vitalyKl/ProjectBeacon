> Historical Phase 0 snapshot (2026-09-30). Current hierarchy: `dotnet project docs/ProjectBeacon-design-doc-v3.md` and master roadmap §1.4. This file's conflict order is not authoritative.

# Source of Truth Designation

Two specialized roadmaps are the authoritative documents for their respective domains.
This file designates which doc wins when the master roadmap and a specialized roadmap conflict.

## Designation

| Domain | Authoritative doc | File |
|---|---|---|
| Backend security, trust boundaries, actor identity, tenant isolation | Code review roadmap | `ProjectBeacon-code-review-roadmap-v3.md` |
| Frontend UI/UX, design tokens, visual regression, route matrix, component behavior | UI/UX review roadmap | `ProjectBeacon-ui-ux-review-roadmap-v1.1.md` |
| Phase sequencing, gates, cross-cutting goals, non-goals | Master roadmap | `ProjectBeacon-master-roadmap-v1.md` |

## Rules

1. For **security and backend architecture** questions, the code review roadmap (v3) is the source of truth. If the master roadmap describes a security fix at a high level and the code review roadmap describes it in detail, the detailed version wins.

2. For **UI/UX, design tokens, visual regression, and frontend behavior**, the UI/UX review roadmap (v1.1) is the source of truth.

3. For **phase ordering, gates, and what is in/out of scope for a given phase**, the master roadmap wins.

4. If a specialized roadmap contradicts the master roadmap on scope (e.g., master says "flagged off" but specialized says "implement"), the master roadmap wins (non-goals are non-goals).

5. `AGENTS.md` is a compiled export of the Context brief. It is NOT a source of truth for any decision. If `AGENTS.md` and a roadmap conflict, the roadmap wins.

6. The living Context brief (compiled via `beacon context compile`) is the operational brief for agents. The roadmaps are the planning docs. If Context says "do X" and a roadmap says "do Y", the Context wins for the current session, but the roadmap should be updated to match.

## Conflict resolution order

1. User instruction (explicit, in-session)
2. Living Context brief (compiled)
3. Master roadmap (phase/scope/gates)
4. Specialized roadmap (domain detail)
5. `AGENTS.md` (export, lowest authority)
