# ProjectBeacon — Documentation Review

Date: 2026-07-03
Scope: All docs in repo. Code is the source of truth.

## Inventory

| Source | Path | Health |
|--------|------|--------|
| README | `README.md` (48 lines) | Good |
| AGENTS.md | `AGENTS.md` | Good, minor stale claims |
| Env example | `.env.example` | 1 misleading comment |
| Master roadmap | `dotnet project docs/ProjectBeacon-master-roadmap-v1.md` | Authoritative |
| Design doc | `dotnet project docs/ProjectBeacon-design-doc-v3.md` | Authoritative |
| Code review roadmap | `dotnet project docs/ProjectBeacon-code-review-roadmap-v3.md` | Authoritative |
| UI/UX roadmap | `dotnet project docs/ProjectBeacon-ui-ux-review-roadmap-v1.1.md` | Authoritative |
| UI migration spec | `dotnet project docs/UI Design Migration Specification.md` | Authoritative |
| MCP host | `dotnet project docs/mcp-host.md` | Good |
| Pipeline doc | `dotnet project docs/task-pipeline-local-agents.md` | Good |
| Endpoint classification | `dotnet project docs/ProjectBeacon-endpoint-classification.md` | Good |
| Auth matrix | `dotnet project docs/ProjectBeacon-authorization-matrix.md` | Good |
| Hardening roadmap | `dotnet project docs/ProjectBeacon-Hardening-Roadmap.md` | Good |
| Features | `dotnet project docs/features.md` | Good |
| Agent prompts | `dotnet project docs/agent-prompt-template.md` | Good |
| TZ docs | `dotnet project docs/ProjectBeacon-tz-*.md` | Good |
| UI polish | `dotnet project docs/ProjectBeacon-ui-polish-review-v1.md` | Good |
| Hardening prompts | `dotnet project docs/ProjectBeacon-Hardening-Agent-Prompts.md` | Good |
| Deploy | `deploy/README.md` | Good |
| Refactor docs | `dotnet project docs/refactor/*.md` (10 files) | Process docs |
| Skills | `dotnet project docs/Skills/*.md` (4 files) | Process docs |
| Docker/K8s | `docker-compose.yml`, `deploy/k8s/` | Clean |

## Coverage Gaps

1. No CONTRIBUTING.md (setup is in README + AGENTS.md, sufficient for current team)
2. No CHANGELOG.md (version in git tags + Directory.Build.props, acceptable)
3. No OpenAPI spec (API is internal and thin, endpoint classification doc covers it)
4. No testing guide (commands in AGENTS.md only)
5. `specs/` is a stub (README only, misleading)

## Findings

### 1. `.env.example` worker-token comment is misleading

- **Type:** wrong
- **Docs say:** `.env.example:20` — "Deploy-time worker bearer. Treated as project admin on every project."
- **Code shows:** `FinishWorkCommand.cs:111` and `PipelineSupport.cs:85` — token is compared via `FixedTimeEquals(actorId, workerToken)` as an ActorId string. Not a bearer token. AGENTS.md correctly says "It is an ActorId string... It is not a bearer that makes the caller project admin."
- **Impact:** Operator may configure it as a standard bearer token.
- **Fix:** Change comment to: "ActorId string for finish_work and pipeline force-close. Not a bearer token. Do not print."
- **Confidence:** high

### 2. Two auth GET endpoints lack rate limiting

- **Type:** missing (security)
- **Docs say:** AGENTS.md — "All auth endpoints get rate limiting."
- **Code shows:** `AuthEndpoints.cs:28-29` — `GET /v1/auth/me` (auth + human, no rate limit) and `GET /v1/auth/options` (anonymous, no rate limit). The other 9 auth endpoints all have `.RequireRateLimiting("auth")`.
- **Impact:** `/v1/auth/options` is an unauthenticated probe surface.
- **Fix:** Add `.RequireRateLimiting("auth")` to both, or narrow the AGENTS.md policy to mutation endpoints.
- **Confidence:** high

### 3. AGENTS.md finish_work description lists ActorId as a request field

- **Type:** partial
- **Docs say:** AGENTS.md — "POST /v1/work/finish_work accepts TaskId, Result, Output, ActorId"
- **Code shows:** `WorkEndpoints.cs:51-57` — `FinishWorkRequest` has TaskId, Result, Output, Review, ReviewTranscriptRef, ReviewRunId. No ActorId field. ActorId is resolved from the authenticated principal via `ActorContextFactory`, not from the body.
- **Impact:** An MCP agent following AGENTS.md might try to pass ActorId in the body.
- **Fix:** Remove "ActorId" from the field list in AGENTS.md. The worker token (env var) is a separate mechanism.
- **Confidence:** high

### 4. AGENTS.md Application feature folder list is not exhaustive

- **Type:** partial
- **Docs say:** AGENTS.md lists 9 feature folders: Tasks, Context, Decisions, Milestones, Projects, Auth, Identity, Agents, Devices.
- **Code shows:** Application/ has 21 subdirectories. 12 additional: Chat, CodeIndex, Reports, Evals, Mcp, Runtime, Security, Mail, Common, Data, Authorization, genout, Interfaces.
- **Impact:** Minor. New contributors may not know where to put cross-cutting code.
- **Fix:** Add "and cross-cutting folders (Chat, CodeIndex, Reports, etc.)" or say "feature folders plus cross-cutting concerns."
- **Confidence:** high

### 5. README drawer groups slightly stale

- **Type:** partial
- **Docs say:** README line 16 lists "Agents (Chat, user-level Agents and models, Workstations)"
- **Code shows:** Web drawer (per AGENTS.md pitfall section) has "Agents (Chat, Agents at /settings/agents, Workstations)" — consistent. But README says "OpenCode connections" while AGENTS.md says "Connections."
- **Impact:** Cosmetic.
- **Fix:** Align terminology between README and AGENTS.md.
- **Confidence:** medium

### 6. No doc explains `Application/genout/`

- **Type:** missing
- **Docs say:** Nothing.
- **Code shows:** Directory exists with content but no doc references it.
- **Impact:** Confusing to newcomers.
- **Fix:** Add a one-line comment or rename to something descriptive.
- **Confidence:** medium

## Rejected

- Splitting roadmaps into per-feature docs: The current single-file-per-topic layout in `dotnet project docs/` is well-organized.
- Moving AGENTS.md content into separate files: AGENTS.md is intentionally a single entry point for AI agents.

## Not Verified

- Full content of the master roadmap (1276 lines) against every code endpoint
- K8s manifest correctness against current deployment
- Playwright test documentation (tests are self-explanatory)
- Whether `features.md` content matches current feature set
