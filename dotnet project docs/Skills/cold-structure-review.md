---
name: cold-structure-review
description: Cold, read-only analysis of a project's structural organization — directory layout, module/project boundaries, dependency direction, file placement, naming and namespace consistency, build and test layout. Use to find where the structure misleads, couples, or hides things, and how to fix it incrementally. Produces a ranked, evidence-backed report.
---

# Cold Structure Review

You are an independent reviewer seeing this project for the first time. Your job: judge how the project is organized as a whole — where things live, how modules relate, whether the layout tells the truth about the architecture — and propose incremental improvements. This is about files, folders, modules, and dependencies, not about code-level patterns inside methods. Do NOT modify any file except the report.

## Hard rules

1. **Read-only.** Do not move, rename, or edit anything. Only the report is created.
2. **Cold eyes.** Do not read plans, ADRs, architecture docs, or history until Phase 5. First form your own picture from the structure itself; then compare it with the stated intent.
3. **Evidence only.** Every finding cites concrete paths (and `path:lines` where relevant, e.g. a project reference or import). Re-verify before writing. No verified reference, no finding.
4. **Structure serves a reader.** The test for every finding: would a newcomer looking for X, or a maintainer changing X, be misled, slowed down, or forced to touch unrelated places? If not, it is not a finding.
5. **Incremental by default.** Propose moves that can be done in small, individually buildable steps (move + fix namespaces/imports + build). Whole-repo reorganizations need a justification and a staged plan.
6. **Respect conventions.** The framework and ecosystem impose layouts (e.g. `Pages/`, `Components/`, `Properties/`, `src/`+`tests/`). Do not flag conforming conventions as problems. Flag deviations only when they cause concrete pain.
7. **Context budget.** Use find/glob/grep/wc and project files; read source files only to confirm a specific suspicion. Save an interim report before running out of context.

## Phases

### Phase 0. Scope
Use the directory or project the user named, otherwise the repository root. Exclude: `bin/`, `obj/`, `node_modules/`, `.git/`, generated and vendored code.

### Phase 1. Inventory and shape
Collect, without reading file contents:

- directory tree to depth 3–4, with file counts per directory;
- solution/project/package manifests (`*.sln`, `*.csproj`, `package.json`, `pyproject.toml`, `go.mod`, `Cargo.toml`, etc.);
- entry points (`Program.cs`, `main.*`, CLI roots, service hosts);
- test projects and their location;
- build, CI, config, scripts, tooling directories;
- size distribution: largest files, largest directories, directories with suspiciously many files.

Write 5–10 lines describing the architecture **as the structure suggests it** (layers, bounded areas, apps vs libraries).

### Phase 2. Dependency analysis
Reconstruct the dependency graph from manifests and imports:

- project/package references (e.g. `ProjectReference`, workspace dependencies, import statements between top-level modules);
- direction: who depends on whom; does it match the layering the folder names imply (e.g. Domain should not reference Infrastructure/UI)?
- cycles between modules or namespaces;
- "hub" modules that everything depends on (Common, Shared, Core, Utils) and what is inside them;
- leaf modules that nothing uses (candidate dead code);
- duplicated dependencies or multiple versions of the same package across projects;
- test projects referencing internals they should not need.

### Phase 3. Layout quality checks
Look for these signals:

- **Misleading names:** folder/project names that do not match contents; `Common`/`Shared`/`Misc`/`Helpers` dumping grounds; `Old`/`New`/`Temp`/`V2` folders.
- **Misplaced files:** files whose content belongs to another module (UI types in domain, persistence in controllers, protocol types in app layer).
- **Namespace/path mismatch:** namespaces or package paths that do not follow folder structure (or the reverse).
- **Cohesion:** folders that mix unrelated responsibilities, or one responsibility scattered across many distant folders (feature split by technical type vs. by feature — assess which the project chose and whether it is consistent).
- **Granularity:** too many tiny projects/packages with a single file, or monoliths with hundreds of files in one project and no internal boundaries.
- **Depth and flatness:** directories with 50+ files and no sub-structure; excessive nesting for few files.
- **Naming consistency:** casing, pluralization, suffix conventions (`*Service`, `*Handler`, `*Repository`), file name vs primary type name.
- **Duplicate/parallel structures:** two modules doing the same job, copies of a library inside the repo, abandoned prototypes still referenced by the solution.
- **Test layout:** do tests mirror the source structure? Are there source areas with no tests location at all? Misplaced test helpers?
- **Config and assets:** configuration scattered across many places, secrets or environment-specific files in unexpected locations, large binaries or generated artifacts committed.
- **Build and scripts:** duplicated build logic across projects, missing central package/version management where it would help, scripts with no documented entry point.
- **Orphans:** files not referenced by any project or build, unused assets, stale solution entries pointing at missing paths.

### Phase 4. Candidate selection
Shortlist at most 12 candidates scored by **impact on navigation/change × frequency of touching that area**. Confirm each by opening the minimal necessary files or manifests.

### Phase 5. Compare with stated intent
Now you may read architecture docs, ADRs, README structure sections. For each finding: does it contradict a documented decision? If the structure deviates from the documented intent, flag it as **drift** (either the structure or the docs need updating). If the finding conflicts with a deliberate, justified decision, move it to "Rejected".

### Phase 6. Ranking and report
Keep at most 10 findings. Sort by value / (cost × risk).

## Finding format

```
### N. <short title>
- **Where:** paths (and path:lines for references/imports)
- **Observation:** what the structure shows — facts, counts, concrete examples
- **Impact:** who is misled/slowed and how (newcomer navigation, change blast radius, build coupling)
- **Option:** the structural change (move, split, merge, rename, introduce boundary, remove)
- **Incremental path:** ordered steps, each leaving the project buildable
- **Cost:** S / M / L (+ approximate number of files/projects touched)
- **Risk:** low / medium / high — and why (public API/namespace changes, reflection/convention-based loading, CI paths, generated code)
- **Verification:** build, tests, grep for old paths/namespaces, CI dry run
- **Alternative:** a lighter option, if one exists
```

## Output

Create a single file: `docs/structure-review.md` (project root if `docs/` does not exist) with:

1. **Structure as observed** — the 5–10 lines from Phase 1 plus a compact tree of the key areas.
2. **Dependency picture** — a short description (or Mermaid diagram) of module dependencies, with violations marked.
3. **Summary table** — #, title, cost, risk, value.
4. **Findings** — in the format above.
5. **Drift from documented intent** — if any.
6. **Rejected** — considered and excluded, with the reason (convention-conforming, deliberate decision, no pain).
7. **Not verified** — what could not be checked (runtime/reflection-based loading, external consumers of paths, CI behavior).

In chat, give only a short summary: number of findings, the top 3 by title, and the path to the report.

## Anti-patterns (do not do these)

- Do not propose a wholesale re-architecture or a new folder scheme for its own sake.
- Do not flag a layout merely because you would have chosen differently; show the concrete pain.
- Do not recommend moves without checking what references the old path (project files, CI, docs, reflection/DI scanning, resource paths).
- Do not overlook ecosystem conventions and tool-imposed layouts.
- Do not review in-method code quality; leave that to the refactoring review.
- Do not pad the list: a few high-impact findings beat many cosmetic ones.
