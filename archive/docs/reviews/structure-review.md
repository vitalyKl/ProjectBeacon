# ProjectBeacon — Structure Review

Date: 2026-07-03
Scope: Full monorepo at `A:\projects\ProjectBeacon`, excluding `bin/`, `obj/`, `node_modules/`, `.git/`, `typescript/` (git-ignored reference), generated code.

---

## Structure as Observed

ProjectBeacon is a .NET 9 monorepo with a clean 4-layer architecture: Domain ← Application ← Infrastructure, plus three hosts (API, Web, Cli) and a Worker. Each project has a matching test project. The Web host is feature-first (`Features/{Name}`). The CLI is split into `Client/` (workstation) and `Mcp/` (stdio). The repo root carries ~24 top-level directories (7 source projects + 6 test projects + infra directories), which is manageable for a monorepo of this size.

Compact tree (depth 2, source dirs only):

```
ProjectBeacon.sln
├── ProjectBeacon.Domain/          (entities, enums, value objects)
│   ├── Entities/{Projects,Identity,...}
│   └── Enums/
├── ProjectBeacon.Application/     (CQRS handlers, DTOs, interfaces)
│   ├── {Tasks,Context,Decisions,Milestones,Projects,Auth,Identity,
│   │    Agents,Devices,CodeIndex,Chat,Reports,Runtime,Security,
│   │    Evals,Mcp,Mail,Authorization,Interfaces,Common,Data,genout}
├── ProjectBeacon.Infrastructure/  (EF Core, config, migrations)
│   ├── Data/
│   │   ├── Configurations/         (39 IEntityTypeConfiguration files)
│   │   └── Migrations/
│   └── {Mail,Security,...}
├── ProjectBeacon.API/             (thin /v1 endpoints)
│   └── Endpoints/
├── ProjectBeacon.Web/             (Blazor Server + MudBlazor)
│   ├── Features/{Agents,Auth,Backlog,Board,Chat,Context,Dashboard,
│   │    Decisions,Landing,Projects,Reports,Roadmap,Settings,Tasks}
│   ├── Shared/
│   ├── Theme/
│   ├── Startup/
│   └── wwwroot/
├── ProjectBeacon.Cli/             (AssemblyName: beacon)
│   ├── Client/{ModelSwapping,...}
│   └── Mcp/
├── ProjectBeacon.Worker/          (background jobs)
├── *.Tests/                       (6 test projects, xUnit)
├── deploy/k8s/                    (blue-green manifests)
├── docs/                          (review reports)
├── dotnet project docs/            (roadmaps, design docs, skills)
├── scripts/                       (publish-daemon.ps1)
├── specs/                         (README only)
├── tests/playwright/              (E2E screenshot matrix + node_modules)
├── archive/docs/                  (historical, not a requirement source)
└── .publish-sel/                  (publish selection)
```

---

## Dependency Picture

```
Domain  ←  Application  ←  Infrastructure
  ↑             ↑                ↑
  └─────────────┴────────────────┘
        (all three referenced by hosts)

API       → Application + Infrastructure
Web       → API + Application + Infrastructure
Cli       → Application + Domain + Infrastructure
Worker    → Application + Domain + Infrastructure
```

- **Direction:** Correct. Domain has no ProjectReferences (leaf). Application references only Domain. Infrastructure references both Domain and Application.
- **No cycles detected.**
- **Hub module:** `ProjectBeacon.Application` is referenced by 4 hosts + Infrastructure + 2 test projects (7 references). This is the expected fan-in for a CQRS layer.
- **Leaf modules:** `ProjectBeacon.Domain` (referenced by 5 projects), `ProjectBeacon.Worker` (referenced only by Worker.Tests).
- **Test projects:** Each source project has a matching `.Tests` project. `Web.Tests` references Web + Infrastructure + Application + Domain (4 refs — slightly heavy but typical for integration tests).
- **No unexpected cross-references.** All follow the documented layering.

---

## Summary Table

| # | Title | Cost | Risk | Value |
|---|-------|------|------|-------|
| 1 | `tests/playwright/` contains committed `node_modules/` and large `out/`/`baseline/` screenshot trees | M | Low | Medium |
| 2 | `dotnet project docs/` uses a space in the directory name | S | Low | Medium |
| 3 | `specs/` directory contains only a README stub | S | Low | Low |
| 4 | `Application/` has 21 subdirectories at root level | S | Low | Low |
| 5 | `Application/genout/` — unclear purpose, no doc reference | S | Low | Low |
| 6 | `.publish-sel/` at repo root is an unusual location | S | Low | Low |
| 7 | No top-level `Makefile` or unified task runner | S | Low | Low |
| 8 | `scripts/` has a single file (`publish-daemon.ps1`) | S | Low | Low |

---

## Findings

### 1. `tests/playwright/` carries build artifacts and dependencies

- **Where:** `tests/playwright/node_modules/`, `tests/playwright/out/`, `tests/playwright/baseline/`
- **Observation:** The `node_modules/` directory (~500+ files) and two sets of screenshot PNGs (`out/` and `baseline/`, ~144 PNGs total) are in the working tree. The `.gitignore` in `tests/playwright/` exists, but `node_modules/` and `out/` appear in the directory listing, suggesting they may be tracked or at least not ignored at the repo-root level.
- **Impact:** Bloats the working tree. If committed, inflates the repo. Screenshot baselines are reasonable to track, but `out/` (generated output) and `node_modules/` should not be.
- **Option:** Ensure `.gitignore` covers `node_modules/` and `out/`. Keep `baseline/` tracked. Consider moving `out/` to a temp location or adding it to gitignore.
- **Incremental path:** 1) Check `git ls-files tests/playwright/` to see what's tracked. 2) Add missing gitignore entries. 3) `git rm --cached` any tracked artifacts.
- **Cost:** S
- **Risk:** Low
- **Verification:** `git ls-files tests/playwright/` shows no `node_modules` or `out` entries.
- **Alternative:** Leave as-is if already gitignored and only present locally.

### 2. `dotnet project docs/` directory name contains a space

- **Where:** `dotnet project docs/`
- **Observation:** The directory name `dotnet project docs` has a space, requiring quoting in every shell command, glob, and script reference. It is referenced in `AGENTS.md`, `README.md`, and multiple other docs.
- **Impact:** Every reference must be quoted. Error-prone in scripts and CI. The name also mixes the tool (`dotnet`) with the content (`project docs`), which is confusing — it is not a `dotnet` command directory.
- **Option:** Rename to `docs/` at the repo root (merge with existing `docs/`), or to `product-docs/`, or to `spec/`. The existing `docs/` directory currently holds review reports only.
- **Incremental path:** 1) Merge `docs/` (review reports) into the renamed directory or keep `docs/` for operational docs. 2) Update all references in AGENTS.md, README.md, and cross-references. 3) `git mv` the directory.
- **Cost:** S
- **Risk:** Low (all references are in-repo)
- **Verification:** `grep -r "dotnet project docs" --include="*.md" --include="*.cs" --include="*.json"` returns zero matches after rename.
- **Alternative:** Keep as-is if the team finds the space manageable.

### 3. `specs/` directory is a stub

- **Where:** `specs/README.md` (single file)
- **Observation:** The `specs/` directory contains only a README. No actual spec files. Its purpose is unclear from the content.
- **Impact:** Misleading to newcomers — suggests specs live here but they don't.
- **Option:** Either populate it or remove it. If the intention is to hold feature specs, document that in the README. If specs are in `dotnet project docs/`, remove the empty directory.
- **Incremental path:** 1) Decide where specs belong. 2) Remove or populate.
- **Cost:** S
- **Risk:** Low
- **Verification:** Directory either removed or contains meaningful content.

### 4. `Application/` has 21 top-level subdirectories

- **Where:** `ProjectBeacon.Application/`
- **Observation:** 21 subdirectories at the project root: Agents, Auth, Authorization, Chat, CodeIndex, Common, Context, Data, Decisions, Devices, Evals, genout, Identity, Interfaces, Mail, Mcp, Milestones, Projects, Reports, Runtime, Security, Tasks.
- **Impact:** Moderate flatness. Most are feature folders (matching the documented convention). The non-feature ones (Common, Data, Interfaces, genout, Runtime, Security, Authorization, Mail) are cross-cutting and harder to navigate.
- **Option:** No change needed — this is the documented convention. Consider grouping cross-cutting concerns under `Shared/` or `_Infrastructure/` if the count grows.
- **Incremental path:** Not urgent. Revisit if the count exceeds ~25.
- **Cost:** S
- **Risk:** Low
- **Verification:** N/A (style preference)
- **Alternative:** No change.

### 5. `Application/genout/` — unclear purpose

- **Where:** `ProjectBeacon.Application/genout/`
- **Observation:** A directory named `genout` with no obvious mapping to a domain concept. Not referenced in AGENTS.md, README, or the design doc.
- **Impact:** Confusing to newcomers. "genout" could mean "generated output" but the content is unclear.
- **Option:** Rename to a descriptive name (e.g., `CodeGen/` or `Generators/`) or add a brief comment in the nearest .cs file explaining what it holds.
- **Incremental path:** 1) Inspect contents. 2) Rename or document.
- **Cost:** S
- **Risk:** Low
- **Verification:** `grep -r "genout" --include="*.cs"` returns zero matches after rename.
- **Alternative:** Leave as-is if it's an established internal convention.

### 6. `.publish-sel/` at repo root

- **Where:** `.publish-sel/`
- **Observation:** A hidden directory at the repo root. Unusual location for publish configuration.
- **Impact:** Low — it's a hidden dotfile directory, so it's easily overlooked.
- **Option:** Move to `scripts/` or `deploy/` if it's deploy-related. Or leave as-is if it's a tool-specific config that belongs at root.
- **Incremental path:** 1) Inspect contents. 2) Relocate if appropriate.
- **Cost:** S
- **Risk:** Low
- **Verification:** N/A

### 7. No unified task runner

- **Where:** Repo root
- **Observation:** No `Makefile`, `justfile`, or unified script. Build/test/format are invoked via raw `dotnet` commands. `scripts/` has only `publish-daemon.ps1`.
- **Impact:** Minor — the commands are simple (`dotnet build`, `dotnet test`, `dotnet format`). But a single entry point would reduce cognitive load for newcomers.
- **Option:** Add a `justfile` or a `tasks.ps1` with `build`, `test`, `format`, `lint` targets. Or document the commands in README (already done).
- **Incremental path:** Not urgent.
- **Cost:** S
- **Risk:** Low
- **Verification:** N/A
- **Alternative:** README already documents the commands adequately.

### 8. `scripts/` has a single file

- **Where:** `scripts/publish-daemon.ps1`
- **Observation:** One PowerShell script in the directory.
- **Impact:** Negligible.
- **Option:** No change needed. If more scripts are added, the directory becomes justified.
- **Incremental path:** N/A
- **Cost:** S
- **Risk:** Low
- **Verification:** N/A

---

## Drift from Documented Intent

AGENTS.md documents the architecture as:
> `ProjectBeacon.Domain` ← `ProjectBeacon.Application` ← `ProjectBeacon.Infrastructure`. Hosts (API, Web, Cli, Worker) reference Application and Infrastructure.

**Actual:** Matches exactly. All 4 hosts reference Application and Infrastructure. Cli also references Domain directly (justifiable for enum/type usage). No drift.

AGENTS.md says:
> Web UI is feature-first: `ProjectBeacon.Web/Features/{Feature}/`.

**Actual:** 14 feature directories under `Features/`. Matches.

AGENTS.md says:
> Application handlers live in matching feature folders (`Tasks/`, `Context/`, `Decisions/`, `Milestones/`, `Projects/`, `Auth/`, `Identity/`, `Agents/`, `Devices/`).

**Actual:** All 9 listed folders exist in Application. Plus 12 additional folders (Chat, CodeIndex, Reports, Evals, Mcp, Runtime, Security, Mail, Common, Data, Authorization, genout, Interfaces). The additional folders are cross-cutting or newer features not yet listed in AGENTS.md. **Minor drift:** AGENTS.md's list is not exhaustive.

AGENTS.md mentions `ProjectBeacon.Worker` as: "background jobs... Today it only expires sessions, API tokens, password-reset tokens, and invites."

**Actual:** Worker project exists with matching test project. Consistent.

No significant structural drift detected.

---

## Rejected

- **Splitting Application into per-feature projects:** Over-engineering for the current team size and codebase scale. The single Application project with feature folders is the right granularity.
- **Moving test projects into source projects:** The separate `.Tests` project per source project is the .NET standard layout. No change needed.
- **Adding a `src/` or `lib/` intermediate directory:** The flat root layout with `ProjectBeacon.*` prefix is clear and conventional for .NET monorepos. Adding nesting would break the established naming pattern.

---

## Not Verified

- Whether `tests/playwright/node_modules/` is actually git-tracked or just locally present (would need `git ls-files`).
- The purpose and content of `Application/genout/` (file-level inspection not performed).
- Whether `.publish-sel/` is used by any CI pipeline or local tooling.
- Whether the `specs/` directory was intended for a future feature or is vestigial.
