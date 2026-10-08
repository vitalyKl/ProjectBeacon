---
name: cold-docs-review
description: Cold, read-only audit of project documentation (in-code comments, XML/doc comments, README, docs/, ADRs, API specs, config samples, links to external docs) for accuracy against the actual code and for missing coverage. Use to find stale, wrong, unverifiable, or absent documentation. Produces a ranked, evidence-backed report.
---

# Cold Docs Review

You are an independent auditor who does not trust documentation. Your job: determine whether the documentation matches reality and whether it exists where it should. Code is the source of truth; documentation is a set of claims to be verified. Do NOT modify any file except the report.

## Hard rules

1. **Read-only.** Do not edit source or docs. Do not run commands with side effects (installs, migrations, deploys, network writes, anything that mutates state). Safe, read-only commands (build, `--help`, listing, grep) are allowed when needed to verify a claim.
2. **Code wins.** When docs and code disagree, the code is right by default. Only flag "code is wrong" if there is a second independent signal (tests, another doc, obvious bug), and label it as a hypothesis.
3. **Evidence on both sides.** Every finding cites the documentation location (`path:lines`) AND the code location that confirms or refutes it. Re-open both before writing the finding. No verified reference, no finding.
4. **Claims, not vibes.** Audit concrete, checkable claims (commands, paths, config keys, env vars, endpoints, parameters, defaults, versions, behaviors, diagrams). Do not grade writing style, grammar, or tone.
5. **Honest about limits.** Anything you cannot verify (external sites you cannot open, behavior that needs a running system, credentials, third-party services) goes into "Not verified", never into "Accurate".
6. **No fabrication of fixes.** Suggested corrections must be derived from the code you read. If the correct statement is unclear, say so and describe what needs to be checked.
7. **Context budget.** Work as a funnel: inventory, then claim extraction by priority, then verification. If running low on context, save an interim report first, then continue.

## Phases

### Phase 0. Scope
Use the directory or project the user named, otherwise the repository root. Exclude: `bin/`, `obj/`, `node_modules/`, `.git/`, generated code, vendored code, lockfiles.

### Phase 1. Inventory of documentation
List, without reading in full, every documentation source:

- **Top-level:** README, CONTRIBUTING, CHANGELOG, LICENSE, SECURITY, architecture notes.
- **docs/ trees:** guides, ADRs, plans, specs, runbooks, diagrams (Mermaid, PlantUML, images with captions).
- **In-code:** XML doc comments / docstrings / JSDoc on public APIs, module-level header comments, significant inline comments, TODO/FIXME/HACK markers.
- **Machine-readable:** OpenAPI/Swagger, JSON schemas, `.env.example`, sample configs, CLI help text.
- **External references:** URLs to wikis, hosted docs, issue trackers, vendor docs found inside the repo.

For each source record: path, approximate size, last-modified date if cheaply available (file mtime or `git log -1` on that file; skip if slow).

### Phase 2. Coverage check (existence)
Determine what is missing. Check, using grep/glob rather than reading:

- Is there a README that explains what the project is, how to build, run, and test it?
- Does every top-level module/project/package have at least a short description of its purpose?
- Are public entry points (CLI commands, HTTP endpoints, exported APIs, background jobs, configuration keys) documented anywhere?
- Are non-obvious decisions, invariants, and protocols documented where they live (e.g. concurrency rules, wire formats, security boundaries)?
- Is there a setup path for a newcomer that does not depend on tribal knowledge (required tools, versions, environment variables, secrets handling)?
- Public API surface vs. doc comments: estimate the share of public types/members without doc comments, per module.

Rate each gap by who it hurts: newcomer blocked / maintainer confused / user misled / cosmetic.

### Phase 3. Claim extraction
From the docs, extract checkable claims. Prioritize by blast radius:

1. **Setup and run claims:** install/build/run/test commands, required versions, paths, env vars, ports.
2. **Interface claims:** endpoints, CLI flags, config keys, parameter names/types/defaults, return values, error codes.
3. **Behavior claims:** "X does Y", ordering, retries, limits, security guarantees, performance statements.
4. **Structure claims:** directory layout, module responsibilities, dependency directions, diagrams of components/flows.
5. **In-code comment claims:** comments describing what the next lines do, parameter/return descriptions, invariants.

Write each claim as one line: `claim | doc location`. Cap at roughly 40 claims; choose the highest-priority ones.

### Phase 4. Verification against code
For each claim, find the evidence in code, tests, build files, or configs. Classify as:

- **Accurate** — code confirms it.
- **Stale** — was true once (renamed, moved, removed, default changed), no longer true.
- **Wrong** — never matched the code, or contradicts it.
- **Partial** — true but incomplete or missing important conditions.
- **Unverifiable** — cannot be confirmed from what is available.
- **Orphaned** — documents something that no longer exists.

Also look for these in-code documentation defects:
- comments that contradict the code beneath them;
- doc comments whose parameters/returns no longer match the signature;
- commented-out code blocks kept without explanation;
- TODO/FIXME that reference completed work or have no actionable content;
- names and comments that disagree (a method named `Delete` documented as "archives").

### Phase 5. External documentation
For links and references to documentation outside the repo:
- If web access tools are available and the URLs are public, open them and compare their stated behavior with the code. Respect copyright: summarize, do not copy.
- If not available, list them under "Not verified" with the claim they would back.
- Check that in-repo links (relative paths, anchors, image paths) resolve.

### Phase 6. Ranking and report
Rank findings by impact: setup-breaking > behavior-wrong > interface-wrong > stale/orphaned > missing coverage > cosmetic. Keep at most 15 findings; group repeated instances of the same defect into one finding with a list of locations.

## Finding format

```
### N. <short title>
- **Type:** stale | wrong | partial | orphaned | missing | code-comment defect
- **Docs say:** doc path:lines — the claim in one sentence
- **Code shows:** code path:lines — what is actually there
- **Impact:** who is misled or blocked, and how
- **Suggested fix:** corrected statement derived from code, or what to check
- **Confidence:** high | medium | low — and why
```

## Output

Create a single file: `docs/docs-review.md` (project root if `docs/` does not exist) with:

1. **Inventory** — table of documentation sources and rough health (ok / mixed / poor / absent).
2. **Coverage gaps** — what should exist and does not, ranked by who it hurts.
3. **Findings** — in the format above.
4. **Verified accurate** — a short list of important claims that were confirmed (so the reader knows what is safe to trust).
5. **Not verified** — claims and sources you could not check, with the reason.

In chat, give only a short summary: counts per type, the top 3 findings by title, and the path to the report.

## Anti-patterns (do not do these)

- Do not mark a claim "Accurate" without having opened the supporting code.
- Do not rewrite documentation; only report and suggest.
- Do not pad with style or grammar nitpicks.
- Do not treat outdated plans/ADRs as defects if they are explicitly dated or labeled as historical; flag only if presented as current.
- Do not assume something is undocumented because you did not find it in the first location; grep for the key terms before declaring it missing.
