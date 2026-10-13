# ProjectBeacon — Structural Target Model

Status: approved structural conventions — authoritative for placement and naming, and mirrored in `AGENTS.md` (Structure section). The migration plan in §8 is partially executed: Steps 1, 2, 3, and 4 done; Step 5 partial (task test files in `Application.Tests/Tasks/`); Steps 6 and 7 outstanding. The rules below are the target regardless of migration state.
Scope: structure, placement, and naming rules only. No new base classes or interfaces are introduced by this model.

This document fixes the final rules for where code lives. It is not an inventory of moved files; the migration plan in §8 is the only list of changes, and it references these rules.

## 1. Current structure and dependency graph

Production projects (verified from csproj references):

```
Domain  ←  Application  ←  Infrastructure
              ↘              ↙
              Cli            API  ←  Web
              ↘              ↙
               Worker ───────┘
```

- `ProjectBeacon.Domain` — no project references. `Common/` (`Entity`, tenant markers), `Entities/{Projects,Identity,Devices,Evals,Agents}`, `Enums/` (26 files), `CommandProtocol.cs` at the project root, empty `genout/`.
- `ProjectBeacon.Application` → Domain. Feature folders: Agents, Auth, Authorization, Chat, CodeIndex, Common, Context, Data, Decisions, Devices, Evals, Identity, Mail, Mcp, Milestones, Projects, Reports, Runtime, Security, Tasks.
- `ProjectBeacon.Infrastructure` → Domain, Application. `Data/` (`BeaconDbContext`, one `IEntityTypeConfiguration` per entity, `Migrations/`, `TenantScope`), `Http/`, `LlamaSwap/`, `Mail/`, `Security/`.
- `ProjectBeacon.API` → Application, Infrastructure. Thin endpoint groups in `Endpoints/`, auth middleware in `Auth/`.
- `ProjectBeacon.Web` → API (maps `/v1`), Application, Infrastructure. Feature-first `Features/{14}`, `Shared/`, `Theme/`, `Extensions/`, `Startup/`.
- `ProjectBeacon.Cli` → Application, Domain, Infrastructure. `Client/` (incl. `ModelSwapping/`), `Mcp/`.
- `ProjectBeacon.Worker` → Application, Domain, Infrastructure. One hosted service.
- Seven test projects, one per production project.

The intended direction (Domain ← Application ← Infrastructure; hosts depend on Application and Infrastructure; Application never references Infrastructure) is respected by every project reference. The violations below are structural, not referential.

### Confirmed violations

1. **Namespace↔assembly inversion.** Three files physically in Application declare `ProjectBeacon.Infrastructure.*` namespaces:
   - `Application/Data/BeaconPorts.cs` — `IBeaconDb`, `IBeaconDbFactory`, `ITenantContext`, `TenantContext` → `namespace ProjectBeacon.Infrastructure.Data`.
   - `Application/Runtime/LlamaSwapPorts.cs` — `ILlamaSwapProxy`, `ILlamaSwapCatalog`, `UnavailableLlamaSwapProxy`, llama-swap DTO records → `namespace ProjectBeacon.Infrastructure.LlamaSwap`.
   - `Application/Mail/IEmailSender.cs` — `namespace ProjectBeacon.Infrastructure.Mail`.

   The ownership is intentional (comment in `BeaconPorts.cs`: "Owned by Application so handlers do not reference the Infrastructure assembly"), but the namespaces misstate the assembly: consumers inside Application write `using Infrastructure.Data;`, and IDE/refactor tooling will mislocate these types.
2. **`Domain/Entities/Projects` is a dumping ground.** 29 of 41 entities live in `Entities/Projects/` (tasks, chat, context, decisions, milestones, reports, `ApiToken`, agent/model entities…), while Identity, Devices, Evals, and Agents each have their own folders.
3. **EF Core types leak into Application contracts, implicitly funded.** `IBeaconDb` exposes `DbSet<T>`, `DatabaseFacade`, `EntityEntry<T>`; handlers use EF LINQ (`AnyAsync`, `Include`). EF packages reach Application **implicitly** through the allow-by-exclusion in `Directory.Build.props` (Application is not in the exclusion list), so the coupling is invisible in `ProjectBeacon.Application.csproj`.
4. **Namespace drift (minor).** `Web/Program.cs` → `ProjectBeacon.Web.Theme`; `Worker/Program.cs` → `ProjectBeacon.Worker.Services`; `Cli/Mcp/McpCodeTools.cs` and `Cli/Mcp/McpSessionTools.cs` → `ProjectBeacon.Cli` while sibling files use `ProjectBeacon.Cli.Mcp`; `Domain/CommandProtocol.cs` sits at the Domain root outside any subfolder.
5. **Dead marker interface (removed in Step 4).** `ITenantScoped` (no members) was never referenced anywhere; only `IOrgScoped`/`IProjectScoped` are consumed, by `TenantScopedQueryFilterConvention` (`Infrastructure/Data/TenantScopedQueryFilterConvention.cs`). Removed; the two scope interfaces now stand alone in `Domain/Common/ScopedEntities.cs`.

### Accepted by design (documented, not violations)

- `IBeaconDb` as an EF-shaped port: it is what lets ~100 handlers do real EF work without a project reference to Infrastructure. The fix for the coupling is an explicit package reference and a documented rule (§6, Step 6), not a new abstraction.
- Feature-bundle files (`TaskItemCommands.cs`, `TaskLifecycleHandlers.cs`, …): multiple records/handlers per file, grouped by feature.
- Manual DI registration in `Application/DependencyInjection.cs`: explicit and greppable.
- Web → API host reference: required so the Web host maps `/v1` in-process.
- `CodeIndex` concrete backends (`RoslynSignatureBackend`, `TreeSitterSignatureBackend`, `RoslynCallerFinder`) in Application: they are part of the MCP code-tool feature consumed by the CLI through Application.

## 2. Proposed target structure

No new projects. 7 production + 7 test projects remain. Feature-first inside projects; namespace always equals assembly root + directory.

```
ProjectBeacon.Domain/
├── Common/
│   ├── Entity.cs                  # base: Guid Id (v7), New<T>()
│   ├── ScopedEntities.cs          # IOrgScoped, IProjectScoped (ITenantScoped removed)
│   └── CommandProtocol.cs         # moved from Domain root
├── Entities/
│   ├── Identity/    Org, OrgMember, OrgInvite, User, UserSession,
│   │                PasswordResetToken, ApiToken
│   ├── Projects/    Project, ProjectMember, ProjectInvite, Label, LabelPath
│   ├── Tasks/       TaskItem, TaskComment, TaskDependency, TaskStep, Subtask,
│   │                PipelineSession, ReviewVerdict, TaskKind, TaskKindPhase, TaskPhase
│   ├── Roadmap/     Milestone
│   ├── Context/     ContextSection, ContextRevision, Constraint, Decision, DecisionTask
│   ├── Reports/     Report
│   ├── Chat/        ChatSession, ChatPart
│   ├── Agents/      AgentTemplate, LocalModelBackend, RoleBinding, OpenCodeConnection
│   ├── Devices/     DaemonDevice, DeviceHostSample, WorkstationCommand, ProjectRuntime
│   └── Evals/       EvalRun, ReviewRun
└── Enums/             # one enum per file (ContextEnums.cs split into 3)

ProjectBeacon.Application/
├── Common/         CQRS.cs, AutoLabel, PathMatcher, Tokenizer, WorkspacePath
├── Persistence/    IBeaconDb, IBeaconDbFactory, ITenantContext, TenantContext
│                   # (was Data/BeaconPorts.cs; ns ProjectBeacon.Application.Persistence)
├── Agents/         … + ChatModelHandlers.cs (moved from Auth/)
├── Mail/           IEmailSender.cs (ns ProjectBeacon.Application.Mail)
├── Runtime/        IAgentRuntime, ILlamaSwapProxy, ILlamaSwapCatalog,
│                   UnavailableLlamaSwapProxy, llama-swap DTO records
│                   (ns ProjectBeacon.Application.Runtime)
└── {Auth, Authorization, Chat, CodeIndex, Context, Decisions, Devices, Evals,
     Identity, Mcp, Milestones, Projects, Reports, Security, Tasks}/   # layout unchanged

ProjectBeacon.Infrastructure/   # layout unchanged
├── Data/            BeaconDbContext, Configurations/ (one per entity), Migrations/, TenantScope
├── Http/  ├── LlamaSwap/  ├── Mail/  └── Security/

ProjectBeacon.API/      # unchanged (Endpoints/, Auth/, Program.cs)
ProjectBeacon.Web/      # layout unchanged (namespace fix in Program.cs)
ProjectBeacon.Cli/      # layout unchanged (namespace fixes in Mcp/McpCodeTools, McpSessionTools)
ProjectBeacon.Worker/   # layout unchanged (namespace fix in Program.cs)

ProjectBeacon.Application.Tests/   # feature subfolders mirroring Application
└── {Agents, Auth, Chat, CodeIndex, Context, Devices, Evals, Identity,
     Milestones, Projects, Tasks, …}/
```

### Disputed classifications (verified from code, not convenience)

| Type | Folder | Evidence |
|---|---|---|
| `RoleBinding` | `Agents/` | "Which user-owned model backend a project's planner, actor, or review role uses" (`Entities/Projects/RoleBinding.cs`); FK to `LocalModelBackend`; managed by `SetRoleBindingHandler`/`RemoveRoleBindingHandler` in the Agents group of `Application/DependencyInjection.cs`. Configuration of *who executes* roles, not pipeline execution state. |
| `OpenCodeConnection` | `Agents/` (stays; already there) | User-owned provider connection (`UserId`, `ProviderId`, `ModelId`, `BaseUrl`, `ApiKeyCipher`), explicitly not tenant-scoped; managed by `Save/ListOpenCodeConnectionHandler` in Agents. Not Identity — it is not a Beacon credential. |
| `ProjectRuntime` | `Devices/` | `(ProjectId, DeviceId, LocalRoot)` working-tree attachment **on a device**; `Attach/List/DetachRuntimeHandler` live in `Application/Devices`; AGENTS.md groups it with device concerns ("a project has no single RootPath"). Judgment call: `Projects/` is defensible; Devices/ wins on handler and semantic locality. |
| `PipelineSession` | `Tasks/` | "One planner, actor, or review turn" with `TaskId` FK and `Task` navigation; all `Pipeline*Handlers` live in `Application/Tasks`. |
| `ReviewVerdict` | `Tasks/` | Pipeline review outcome for a task/subtask with `TaskId` FK; `RecordReviewVerdictHandler` in Tasks. Distinct from `Evals/ReviewRun` (evaluation-run record), which stays in `Evals/`. |

## 3. Placement rules by category

| Category | Home | Rule |
|---|---|---|
| Domain entity | `Domain/Entities/{Feature}/` | One class per file. Feature means bounded context, not "has a ProjectId". Project-scoped is not a feature: `Label`/`LabelPath` stay with `Project`; `ApiToken` with credentials; `ProjectRuntime` with devices. |
| Value object | `Domain/Entities/{Feature}/` beside its owner | No standalone `ValueObjects/` folder unless one accumulates naturally. |
| Enum | `Domain/Enums/` | One enum per file. |
| Command / query | `Application/{Feature}/{Feature}Commands.cs` / `{Feature}Queries.cs` | Multiple records per file allowed; keep the existing bundle style. |
| Handler | `Application/{Feature}/*Handlers.cs` | One handler class per public operation, grouped by lifecycle. |
| DTO | Same feature folder as its command/query | Records; JSON shapes live here, not in API. |
| Port (interface consumed by handlers) | Application, beside the consuming feature | `Persistence/` for the DB port; `Mail/`, `Security/`, `Runtime/` as today. |
| Port implementation | The layer that owns the technology | Infrastructure for technology adapters; **application-level adapters and null objects stay in Application** (see §5). |
| EF entity configuration | `Infrastructure/Data/Configurations/{Entity}Configuration.cs` | One per entity; no `ToTable(...)` calls (verified: zero in that folder) — table names stay anchored by `DbSet` property names. |
| Migration | `Infrastructure/Data/Migrations/` | Always generated with `dotnet ef` including the model snapshot. |
| Host middleware | `Infrastructure/Http/` | API/tenant plumbing shared by the API and Web hosts. |
| UI chrome | `Web/Shared/`; feature pages in `Web/Features/{Feature}/` | Theme values only through `Theme/DesignTokens.cs`. |
| Workstation code | `Cli/Client/`; MCP tools in `Cli/Mcp/` | No domain writers outside the hosts. |
| Tests | `ProjectBeacon.{Project}.Tests/`, feature subfolders in Application.Tests | One test project per production project. |

**Subfolder introduction.** A subfolder is created only when a feature's types genuinely split into stable roles. `Commands.cs`/`Queries.cs` bundle a feature's command/query records and `*Handlers.cs` groups handlers by lifecycle — the established bundle style. `Models/`, `Abstractions/`, `Integrations/`, and `Persistence/` are created only when a real second consumer or a layer boundary justifies them; otherwise the types stay in the feature folder. `Interfaces/` and `Implementations/` symmetry folders are never created.

**`Common/` vs feature-local.** `Common/` holds only genuinely cross-feature shared types (`Domain/Common`: `Entity`, scoped markers, `CommandProtocol`; `Application/Common`: `CQRS`, `AutoLabel`, `PathMatcher`, `Tokenizer`, `WorkspacePath`). A type used by one feature stays in that feature folder; it is not promoted to `Common/` for symmetry.

## 4. `Entity`, `IEntity`, identity, factory conventions

**Keep `Entity`** (`Domain/Common/Entity.cs`): abstract base with `Guid Id` (version 7) and `static T New<T>() where T : Entity, new()`. 40 of the 41 entities derive from it (`DecisionTask` is the composite-key join row without its own `Id`); it is the single place identity is assigned and the anchor for EF key configuration.

**Do not create `IEntity`.** Verified against the code: no generic constraint, reflection, DI, or serialization path requires an entity interface. EF uses concrete `DbSet<T>`; `IBeaconDb.Entry<TEntity>` is unconstrained (`where TEntity : class`); JSON serializes DTO records, never entities; `TenantScopedQueryFilterConvention` keys off `IOrgScoped`/`IProjectScoped`. An interface would be pure symmetry with zero consumers.

**Remove `ITenantScoped`.** The memberless base is never referenced. Keep `IOrgScoped`/`IProjectScoped`, which carry the scope keys and drive the EF query-filter convention. Removal happens in its own step, after a reference audit and a behavior check of the convention.

**Factory convention (unchanged):** `Entity.New<T>()` is the only Id-assignment path. Every entity keeps a static `Create(...)` that sets invariants and defaults. The parameterless constructor exists solely for EF Core and is never called by application code.

**Base-class criterion.** `Entity` is the only domain base because it encodes one concrete shared invariant (version-7 `Guid` identity with `New<T>()` as the single Id-assignment path). Introduce a base class only when it encodes such a concrete shared invariant — never for grouping or symmetry. (The matching interface criterion is in §5: no interface without a second consumer or a layer boundary it protects; no interface is required per service, handler, or entity.)

## 5. Interface ownership and implementation placement

Rule: **the interface lives in the layer that defines the need, beside the feature that consumes it; the concrete implementation lives in the layer that owns the technology that satisfies it.**

- Ports consumed by handlers → Application: `IBeaconDb`/`IBeaconDbFactory`/`ITenantContext` (Persistence), `IEmailSender` (Mail), `IPasswordHasher`/`ITokenIssuer`/`ISecretProtector`/`ITotp` (Security), `ILlamaSwapProxy`/`ILlamaSwapCatalog` (Runtime), `IAgentRuntime`, `ISessionSpawner`, `ICallerFinder`/`ISignatureBackend`/`ILanguageRegistry`, `IAuthorizationService`.
- Technology implementations → Infrastructure: `SmtpEmailSender`, `BcryptPasswordHasher`, `JwtTokenIssuer`, `AesSecretProtector`, `RfcTotp`, `LlamaSwapCatalog`, `BeaconDbContext`.
- Application-level adapters and null objects stay in Application: `TenantContext` (mutable tenant-state holder — the contract is Application-owned because handlers need the tenant; Infrastructure only reads it in `BeaconDbContext`, `BeaconDbFactory`, and `TenantIsolationMiddleware`), `UnavailableLlamaSwapProxy` (default registered by Application DI, overridden per host), `DeviceLlamaSwapProxy` (status derived from device heartbeat data), `ManualSessionSpawner`. Verified: no `ILlamaSwapProxy` implementation exists in Infrastructure.
- `BeaconPorts.cs` contains only interfaces and the `TenantContext` holder — moving it to `Application/Persistence/` carries no EF implementation with it; `BeaconDbContext` stays in Infrastructure.
- No global `Interfaces/` or `Implementations/` folders. No new abstraction without a second consumer or a layer boundary it protects.

## 6. Namespace and naming rules

1. **Namespace = assembly root + directory**, always: `ProjectBeacon.{Project}[.{Subfolders...}]`. No cross-assembly namespace aliases; the three port files in §1 are the only existing exception and are removed in Step 1.
2. Relative usings (e.g. `using Domain.Entities.Projects;` inside `namespace ProjectBeacon.Application.Tasks`) stay allowed because they resolve through the `ProjectBeacon` root, but every using must resolve to types in an assembly the current project actually references — never to a namespace whose types live in another assembly.
3. One public type per file for entities, handlers, configurations, endpoints. Feature-bundle files (`*Commands.cs`, `*Handlers.cs`) are the established exception.
4. EF configuration files are named `{Entity}Configuration.cs`, one per entity, in `Data/Configurations/`.
5. **Table names are frozen.** They are anchored by the `DbSet` property names on `IBeaconDb`/`BeaconDbContext` (unchanged by any structural move) and pinned in historical Designer/snapshot models by explicit `b.ToTable("...")` strings. No structural move may rename a `DbSet` property or add `ToTable` calls to configurations.
6. Status enums keep `Domain/Enums/` and their current names (e.g. `TaskItemStatus`, which exists to avoid the BCL collision).

## 7. Rejected alternatives

| Alternative | Why rejected |
|---|---|
| Global `Interfaces/` + `Implementations/` + `Services/` directories | Ports already sit beside their consuming features; symmetry folders would re-scatter cohesive code and hurt navigation. |
| `IEntity` interface | Zero consumers (verified in §4). Pure ceremony. |
| Repository layer replacing `IBeaconDb` | ~100 handlers use EF LINQ (`Include`, async queries); abstracting this is large churn for no capability gain. The EF-shaped port is an accepted, documented compromise — not the one correct architecture. |
| Split Application into `Application.Contracts` + `Application.Handlers` | Project fragmentation; the current single assembly builds and tests cleanly. |
| Split Infrastructure into Data/Http/LlamaSwap projects | Over-fragmentation; folders suffice. |
| Per-feature test projects | One test project per production project already gives clean failure isolation. |
| Convention-based DI auto-scanning | Explicit registration is greppable and reviewable; auto-scan hides wiring. Optional later, not part of the target. |
| Moving `CommandProtocol.cs` into `Entities/Devices/` | It is a protocol constant, not an entity; `Common/` is the correct home. |
| Bulk-editing historical `*.Designer.cs` files after the Domain split | The Designer files are not the snapshot (see §8, Step 3). They embed each migration's historical model and are internally consistent with the old CLR names. They are touched only if a concrete check proves the chain inconsistent. |

## 8. Prioritized incremental migration plan

Six technical steps plus a documentation step. Every step is independently buildable: `dotnet build` and the affected test projects must pass before the next step starts. After the final technical step, full CI (`ci.yml`) must pass.

### Step 1 — Namespace consistency (no entity moves, no EF impact)

Ownership-verified moves, not mechanical renames. For each file the layer ownership was checked against its consumers before choosing the target namespace.

- `Application/Data/BeaconPorts.cs` → `Application/Persistence/BeaconPorts.cs`, namespace `ProjectBeacon.Application.Persistence`. Update `using Infrastructure.Data;` imports of the port types across Application, Infrastructure, API, Web, and tests. Note: `BeaconDbContext`, `TenantScope`, `PostgresConnection` remain in `ProjectBeacon.Infrastructure.Data`; only the port file moves.
- `Application/Runtime/LlamaSwapPorts.cs` → namespace `ProjectBeacon.Application.Runtime`.
- `Application/Mail/IEmailSender.cs` → namespace `ProjectBeacon.Application.Mail`.
- `Web/Program.cs` → namespace `ProjectBeacon.Web`; `Worker/Program.cs` → `ProjectBeacon.Worker`; `Cli/Mcp/McpCodeTools.cs` and `Cli/Mcp/McpSessionTools.cs` → `ProjectBeacon.Cli.Mcp`.
- `Domain/CommandProtocol.cs` → `Domain/Common/CommandProtocol.cs`, namespace `ProjectBeacon.Domain.Common`; update usings in CLI (and any other consumers).
- Delete the empty `Domain/genout/` directory.

No CLR entity type moves in this step; EF model is untouched.

### Step 2 — Application placement cleanup

- Move `Auth/ChatModelHandlers.cs` → `Agents/ChatModelHandlers.cs` (namespace `Application.Auth` → `Application.Agents`); update `DependencyInjection.cs` usings.

### Step 3 — Domain entity feature split (the only EF-sensitive step)

Move 24 entities out of `Entities/Projects/` into the feature folders of §2 (`ApiToken` → Identity; 10 task entities → Tasks; `Milestone` → Roadmap; 5 context entities → Context; `Report` → Reports; 2 chat entities → Chat; `AgentTemplate`, `LocalModelBackend`, `RoleBinding` → Agents; `ProjectRuntime` → Devices). `OpenCodeConnection` is already in `Agents/` and does not move. Update every using in Domain, Application, Infrastructure configurations, API, Web, Cli, and tests. No property, key, relation, index, or configuration change; `DbSet` property names untouched.

EF procedure — do **not** assume a new migration is needed:

1. **Baseline (before any move):** `dotnet ef migrations has-pending-model-changes` → must report none; record `git status` of `Data/Migrations/` (clean tree); on a fresh Postgres container apply all migrations and record the baseline table list (41 tables) from `information_schema.tables`.
2. **Pre-check (verified once, documented here):** zero `ToTable(...)` calls in `Data/Configurations/`; table names derive from `DbSet` property names in `IBeaconDb`/`BeaconDbContext`, which do not change; historical Designer/snapshot models pin the same names via explicit `b.ToTable("...")` strings.
3. **Move types and update namespaces only.**
4. **Run `dotnet ef migrations has-pending-model-changes`:**
   - clean → done. **No migration is added and no Designer file is edited.**
   - reported → inspect the diff first (expected cause: stale CLR name strings in the snapshot only). Update **`BeaconDbContextModelSnapshot.cs` minimally** — the full CLR type strings of the moved entities — and re-run until clean. A no-DDL "history" migration is not added under any circumstances in this step.
5. **Post-verification:** (a) fresh DB — apply all existing migrations, table list must equal the baseline exactly; (b) DB with applied history — `dotnet ef database migrate` must apply nothing (no new `EFMigrationsHistory` row, no DDL); (c) full test suite green. Attach the baseline and post-verification results to the change.
6. **Historical `*.Designer.cs` files stay untouched** unless a concrete later operation (e.g. `dotnet ef migrations remove` or script generation) proves the migration chain inconsistent. If that happens, only the specific files are updated, with the reason recorded in the change.

### Step 4 — Remove `ITenantScoped` (done)

- Audit all references first (verified: the only references are the interface file itself). Delete `ITenantScoped`; keep `IOrgScoped`/`IProjectScoped` in a renamed file `ScopedEntities.cs`.
- Verify `TenantScopedQueryFilterConvention` still applies to the same entity set (23 `IProjectScoped` + 3 `IOrgScoped`) via the model-coverage fact in `Application.Tests/TenantFilterTests`.

### Step 5 — Test organization

- `Application.Tests`: move the flat test files into feature subfolders mirroring Application (file moves only; test namespaces stay `ProjectBeacon.Application.Tests`, so no rebuild churn beyond the moves).

### Step 6 — Make the EF dependency explicit in Application

- Add `Microsoft.EntityFrameworkCore` and `Microsoft.EntityFrameworkCore.Relational` `PackageReference`s directly to `ProjectBeacon.Application.csproj` (versions from `Directory.Packages.props`).
- Remove **only** Application from the implicit grant in `Directory.Build.props` (or invert the file to an explicit allow-list); verify every other project's references are unchanged.
- Record in this document and in AGENTS.md: **Application intentionally uses EF Core API through `IBeaconDb` and handler queries — a deliberate compromise, not the one correct architecture.**

### Step 7 — Documentation

- This document lives at `dotnet project docs/architecture/ProjectBeacon-structural-target-model.md`.
- Update `AGENTS.md` to state the final rules (not a file list): namespace-equals-assembly, the placement table of §3, the port-ownership rule with the application-adapter exception, the `Entity`/factory/no-`IEntity` decision, and the accepted EF-leak statement.

## 9. Acceptance criteria

| Step | Acceptance criteria |
|---|---|
| 1 | `dotnet build` clean; affected test projects green; no file declares a namespace of another assembly (grep check); `Domain/genout` gone; EF model untouched. |
| 2 | Build + tests green; `GetChatModelHandler`/`SetChatModelHandler` resolve under `Application.Agents`; no dangling `Application.Auth` usings. |
| 3 | Baseline captured before the move; `has-pending-model-changes` clean after (snapshot updated minimally only if it was not); fresh-DB migration applies the same 41 tables as baseline; migrate on a DB with applied history changes nothing; full test suite green; `Entities/Projects` contains exactly `Project`, `ProjectMember`, `ProjectInvite`, `Label`, `LabelPath`; no new migration in the commit. |
| 4 | Build + tests green; `ITenantScoped` unreferenced; `TenantFilterTests` passes with identical entity coverage. (Done.) |
| 5 | `dotnet test` green; every `Application.Tests` file sits in a folder matching its handler's Application feature; no orphan root-level test files. |
| 6 | Build + tests green; `ProjectBeacon.Application.csproj` lists the EF references explicitly; `Directory.Build.props` no longer grants EF to Application; all other project references unchanged. |
| 7 | Document at `dotnet project docs/architecture/ProjectBeacon-structural-target-model.md`; `AGENTS.md` states the final rules from §3–§6. |
| All | After Step 6, full CI (`ci.yml`) passes. |

## 10. Deferred (optional, not part of the target)

- Splitting the ~200-line `Application/DependencyInjection.cs` into per-feature registration methods.
- Convention-based DI scanning.
- Moving `CodeIndex` backends to Infrastructure (revisit only if a non-MCP consumer appears).
