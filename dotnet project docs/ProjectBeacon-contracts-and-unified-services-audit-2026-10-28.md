# ProjectBeacon — audit of functional contracts and unified services

**Date:** 2026-10-28  
**Reviewed ref:** `main` at `91f1061602c6326f4604a85eab59524b9733f559`  
**Review type:** static architecture/code review. No source changes, build, or test execution was performed as part of the review.

## Executive summary

ProjectBeacon already has useful abstractions (`IAgentRuntime`, `IModelBackend`, CQRS handler interfaces, `ILanguageRegistry`, persistence/security ports). The main architectural gap is not a lack of interfaces in general. It is the absence of a consistent layer that connects:

1. a definition of a capability/operation;
2. a stable reference to that capability;
3. the context and configuration in which it is used;
4. the policy under which it can be invoked;
5. the execution mechanism/provider selected for it;
6. the adapters through which it is exposed (API, UI, MCP, workstation, internal services).

Today, those concerns are described in separate enums, DTOs, command queues, handler classes, switch statements, MCP registries, and adapter-specific configuration. This creates change amplification: adding a capability can require coordinated edits across several lists and dispatchers, even when the business contract itself has not changed.

**Recommendation:** introduce a small, code-first Operation Catalog and typed execution/dispatch contracts incrementally. Do not introduce one universal runtime interface for all functionality, do not turn every operation into a database row by default, and do not replace the current security or command-delivery protocols.

## Scope and confidence

Findings below are grouped as:
- **Confirmed coupling:** directly visible in the inspected source.
- **Contract gap:** a mismatch or missing abstraction visible in the implementation, but not necessarily a currently failing user flow.
- **Review item:** a risk that requires focused verification before describing it as a defect/security vulnerability.
- **Known boundary:** a limitation documented or appropriate for the current single-host design.

Source links refer to the repository and the reviewed source tree. The audit is static; conclusions about runtime behavior or regressions should be confirmed with tests.

---

## A. Main findings

### A1. There is no shared machine-readable definition of a capability/operation

**Classification:** confirmed architectural gap · **Priority:** P1

Relevant areas:
- `ProjectBeacon.Domain/Enums/WorkstationCommandKind.cs`
- `ProjectBeacon.Domain/Enums/ModelBackendType.cs`
- `ProjectBeacon.Application/Authorization/AuthAction.cs`
- `ProjectBeacon.Application/Authorization/ResourceType.cs`
- `ProjectBeacon.Cli/Mcp/McpApiTools.cs`
- `ProjectBeacon.Cli/Mcp/McpStdioServer.cs`
- `dotnet project docs/features.md`

These mechanisms each answer a different local question: what command can be queued, what backend type can be stored, what resource/action is authorized, or what tool MCP advertises. There is no common operation identity/descriptor linking the functional contract, input/output, context requirements, execution target, policy metadata, and supported invocation surfaces.

The feature index is helpful documentation but is not a machine-readable source of truth for runtime registration.

**Impact:** functions can drift between the UI, API, MCP, control plane, and workstation. A consumer cannot reliably discover what a function does, what it needs, and which implementations are available without knowing implementation-specific lists.

**Direction:** add an immutable code-first `OperationDescriptor`/catalog with stable namespaced IDs (for example, `workspace.initialize`, `code.search`, `model.status`). The descriptor should point to typed request/response contracts and metadata; specialized executors remain separate.

### A2. Workstation command behavior is split across parallel lists and a central dispatcher

**Classification:** confirmed coupling · **Priority:** P1

Relevant files:
- `ProjectBeacon.Domain/Enums/WorkstationCommandKind.cs`
- `ProjectBeacon.Cli/Client/DaemonCommandExecutor.cs`
- `ProjectBeacon.Application/Devices/CommandSandbox.cs`
- `ProjectBeacon.Application/Devices/WorkstationCommandHandlers.cs`
- `ProjectBeacon.Domain/Entities/Devices/WorkstationCommand.cs`

The command enum defines names, but actual behavior is selected in several branches/switches inside `DaemonCommandExecutor`. Project-context requirements are independently enumerated in `CommandSandbox.IsProjectKind`; payload handling, server-side queue validation, and client execution are also separate.

This is not an argument to remove the durable command queue. The queue's version, attempts/generation and lease semantics are valuable protocol guarantees. The gap is that the behavior contract is not registered beside the executor.

**Direction:** define typed workstation operation handlers and metadata for context requirements, payload schema, idempotency/retry class, cancellation, and side effects. Keep the existing queue envelope and authorization/sandbox enforcement. Unknown or unregistered operations should fail closed. Preserve compatibility with in-flight command versions.

### A3. Model definitions and model execution are not fully separated

**Classification:** confirmed implementation coupling + contract gap · **Priority:** P1

Relevant files:
- `ProjectBeacon.Cli/Client/ModelSwapping/IModelBackend.cs`
- `ProjectBeacon.Cli/Client/ModelSwapping/LlamaServerBackend.cs`
- `ProjectBeacon.Cli/Client/ClientLlamaSwap.cs`
- `ProjectBeacon.Application/Runtime/LlamaSwapPorts.cs`
- `ProjectBeacon.Application/Tasks/ISessionSpawner.cs`
- `ProjectBeacon.Application/Agents/ModelBackendHandlers.cs`
- `ProjectBeacon.Application/Agents/OpencodePayload.cs`
- `ProjectBeacon.Domain/Entities/Agents/LocalModelBackend.cs`

A useful `IModelBackend` already exists, but `ClientLlamaSwap` stores concrete `LlamaServerBackend` instances in its own-backend dictionary. The manager is therefore coupled to a concrete runner despite the interface.

The control-plane contract is also expressed heavily in llama-swap terms (`ILlamaSwapCatalog`, YAML model specs, proxy status, reload/unload). The internal swapper parses that same external-tool-oriented configuration format.

There is a second, important contract mismatch in `ManualSessionSpawner.SessionFrom`: `ModelBackendType` supports `FreeToken`, `LlamaCpp`, and `OpenAiCompatible`, and the upsert handler validates launch fields differently by type, but the spawner builds a launch spec through `ILlamaSwapCatalog` from `LaunchCommand` for every backend type. The source comments describe the spawner as a v1 selector where real spawning comes later; treat this as a **future execution-contract gap**, not proof that an already-shipped pipeline path is failing.

**Direction:**
- separate a model/provider definition (what it is and how it is addressed) from a local process/backend (how it is started and supervised);
- inject/use `IModelBackend`, not `LlamaServerBackend`, in the manager;
- use specialized provider/runtime adapters rather than requiring every provider to produce a llama-swap launch command;
- represent runtime capabilities (streaming, abort, usage, health, concurrent residency) explicitly where callers need them;
- validate model type and required fields through one type-specific definition/validator rather than duplicated conditional rules.

Do not force OpenCode API-compatible models and local process runners into the same low-level interface if their actual operations differ. Share metadata/catalog and use specialized interfaces below it.

### A4. CQRS interfaces exist, but there is no common dispatch boundary

**Classification:** confirmed coupling · **Priority:** P1

Relevant files:
- `ProjectBeacon.Application/Common/CQRS.cs`
- `ProjectBeacon.Application/DependencyInjection.cs`
- `ProjectBeacon.API/Endpoints/TaskEndpoints.cs`
- `ProjectBeacon.Web/Features/Tasks/TaskDetail.razor`
- `ProjectBeacon.Application/Devices/DeviceLlamaSwapProxy.cs`

`ICommandHandler<TCommand,TResult>` and `IQueryHandler<TQuery,TResult>` provide useful typed contracts. In practice, registrations are mainly explicit concrete classes, endpoints inject named handlers, and Blazor pages invoke handler classes directly. Application services also call concrete handlers (for example, `DeviceLlamaSwapProxy` calls `EnqueueCommandHandler` and `GetCommandHandler`).

Manual DI registration is explicitly accepted in the structural target model, so registration itself is not a defect. The gap is the absence of a common dispatch seam through which to apply cross-cutting behavior consistently.

**Direction:** add a lightweight typed `ICommandDispatcher`/`IQueryDispatcher` over the existing interfaces. Use it incrementally where centralized tracing, validation, audit logging, metrics, or error mapping are useful. Avoid a framework migration solely for its own sake.

### A5. MCP API tools and local tools have separate catalogs and dispatch paths

**Classification:** confirmed coupling · **Priority:** P1/P2

Relevant files:
- `ProjectBeacon.Cli/Mcp/McpApiTools.cs`
- `ProjectBeacon.Cli/Mcp/McpStdioServer.cs`
- `ProjectBeacon.Application/Agents/McpCatalog.cs`

API tools are described as `McpTool` records (name, description, schema, handler). Local file/CodeIndex/session tools have another definition path and are routed by a switch in `McpStdioServer`; some special cases (such as `context_compile`) are handled separately.

This means MCP is a catalog of tool-specific implementations rather than a projection of common operation contracts.

**Direction:** let the shared catalog expose only operations explicitly marked as MCP-compatible, with MCP schema and adapter metadata. Keep local file tools local; not every Application operation should be published as an agent tool. Keep `AllowedMcpTools` enforcement and define an alias/deprecation policy for stable tool IDs so saved pipeline permissions do not silently break after renames.

### A6. Error results are not a sufficiently stable cross-surface contract

**Classification:** confirmed contract gap · **Priority:** P2

Relevant files:
- `ProjectBeacon.Application/Common/CQRS.cs`
- `ProjectBeacon.API/ProblemResults.cs`
- `ProjectBeacon.Cli/Mcp/McpApiTools.cs`

`Result` and `Result<T>` carry an `ErrorKind`, but the default `Failure(string)` path leaves the kind as `None`, and the public contract has no stable machine-readable error code. HTTP mapping then relies on the kind or a caller-supplied fallback status; MCP commonly returns error text.

**Direction:** retain human-readable messages but add stable error codes and structured validation details. Map the same error contract into HTTP Problem Details, MCP tool errors, UI messages, and logs. Treat the current string-only failure sites as migration candidates, not a reason to rewrite all handlers at once.

### A7. `IBeaconDb` is an EF-shaped port, not a storage-neutral contract

**Classification:** confirmed coupling · **Priority:** P3 / only when independent storage matters

Relevant file: `ProjectBeacon.Application/Persistence/BeaconPorts.cs`

`IBeaconDb` exposes `DbSet<T>`, `DatabaseFacade`, `ChangeTracker`, and `EntityEntry<T>`; Application handlers use EF-specific LINQ and update methods. This deliberately avoids an Infrastructure project reference without forcing dozens of repositories, and the structural target model explicitly accepts this trade-off.

**Direction:** do not replace the whole interface with generic repositories. Extract narrower domain-specific persistence/query ports only for bounded areas that need test isolation, a different storage implementation, or service extraction. Do not label the current design as broken merely because it is EF-dependent.

---

## B. Additional findings beyond the initial summary

### B1. Task-kind phases look configurable, but some orchestration semantics still rely on hard-coded phase keys and order

**Classification:** confirmed coupling · **Priority:** P1/P2

Relevant files:
- `ProjectBeacon.Application/Tasks/ISessionSpawner.cs`
- `ProjectBeacon.Application/Tasks/TaskKindHandlers.cs`
- `ProjectBeacon.Domain/Entities/Tasks/TaskKindPhase.cs`

A phase stores `Key`, `Title`, `Instruction`, `FanOut`, and an optional model. But `ManualSessionSpawner` maps Planner to `understand`, Review to `check`, and other roles to `do`, then falls back to the first/last or second phase by index.

This creates a mismatch between a user-configurable workflow template and the orchestration semantics. Renaming, reordering, or adding phases can have implicit effects not represented in the phase contract.

**Direction:** give phases explicit semantic metadata or map a phase to a role/action through a validated binding. Define whether arbitrary phases are just sequential steps or can participate in planning/acting/review/fan-out. Do not infer semantics only from list position. Add tests for reorder, missing key, custom key, and role-binding behavior.

### B2. Desired-state reconciliation is specific to the current configuration types

**Classification:** confirmed coupling · **Priority:** P2

Relevant files:
- `ProjectBeacon.Application/Devices/DesiredState.cs`
- `ProjectBeacon.Application/Devices/DeviceLifecycleHandlers.cs`
- `ProjectBeacon.Application/Devices/WorkstationCommandHandlers.cs`
- `ProjectBeacon.Domain/Entities/Devices/DaemonDevice.cs`
- `ProjectBeacon.Domain/Entities/Devices/ProjectRuntime.cs`

Revision-based desired/applied state is a good pattern. However, the current model is organized around a user workstation configuration and project OpenCode configuration, with reconciliation commands and queue rules that know those configuration kinds directly.

**Direction:** if additional workstation-managed capabilities are expected, define a typed desired-state resource/reconciler contract and retain the existing per-scope revision guarantees. Do not prematurely generalize if workstation config and project OpenCode config are the only intended managed resources. The goal is to make the next managed resource additive rather than another parallel set of special cases.

### B3. Authorization is described in useful route-level artifacts, but policy metadata is not part of an operation contract

**Classification:** review item · **Priority:** P1 for architecture; security impact must be evaluated per handler

Relevant files:
- `ProjectBeacon.Application/Authorization/IAuthorizationService.cs`
- `ProjectBeacon.Application/Authorization/AuthorizationService.cs`
- `ProjectBeacon.API/Auth/ActorRequirementExtensions.cs`
- `ProjectBeacon.API/Endpoints`
- `dotnet project docs/ProjectBeacon-authorization-matrix.md`
- `dotnet project docs/ProjectBeacon-endpoint-classification.md`

The project already has an authorization matrix and enforced endpoint classifications; these should be preserved. However, route gates, `AuthAction`/`ResourceType`, handler-specific checks, tenant/RLS rules, and direct Blazor-to-Application calls are separate mechanisms. Some handlers also accept commands that do not carry an `ActorContext`, so they cannot independently evaluate actor-specific policy without relying on scoped context or their caller.

This is **not by itself proof of an authorization vulnerability**. The review item is whether each business operation has the same required policy wherever it is invoked: API route, Web component, MCP adapter, worker, or internal service.

**Direction:** describe required actor class/capability/resource scope in operation metadata, but continue enforcing authorization on the trusted server side. Add parity tests that invoke representative operations through both HTTP and direct Application entry points. Never treat MCP visibility or UI visibility as an authorization check.

### B4. Custom MCP server configuration is weakly typed

**Classification:** implementation concern · **Priority:** P2/P3

Relevant file: `ProjectBeacon.Application/Agents/McpCatalog.cs`

Custom server definitions use stringly typed `Type`, `Command`, and `Url` fields. Local commands are split on spaces to form argument arrays; that representation cannot faithfully preserve shell-style quoting or arguments containing spaces. The type string also determines which fields are written into the resulting config.

**Direction:** use a discriminated configuration shape (`LocalCommand` with an argument list versus `RemoteEndpoint` with a URL), validate it once, and store commands as executable plus arguments rather than a single command string. Avoid shell execution; the goal is to preserve argument boundaries, not to add a shell.

### B5. Some extensibility registries are extensible in shape but built-in in composition

**Classification:** design boundary · **Priority:** P3

Relevant files:
- `ProjectBeacon.Application/CodeIndex/ILanguageRegistry.cs`
- `ProjectBeacon.Application/CodeIndex/LanguageRegistry.cs`
- `ProjectBeacon.Cli/Client/DaemonServices.cs`

The language registry properly abstracts extension lookup, but `CreateDefault()` defines the built-in language list in code. The workstation daemon has a separate internal DI composition root for its loops/services. This is reasonable for a single executable, but new providers/capabilities still require source-level composition changes.

**Direction:** if third-party or independently deployed extensions become a product requirement, move composition to explicit module registration. Until then, compile-time registration is simpler and safer. A registry does not need to imply hot-loading or arbitrary plugin execution.

### B6. Chat change notifications are process-local by design

**Classification:** known boundary · **Priority:** future scaling only

Relevant file: `ProjectBeacon.Application/Chat/ChatEventSignal.cs`

The event signal is process-local and documented as sufficient because Web handlers and readers share one process. It should not be treated as a cross-instance event bus if Web is later scaled horizontally or handlers/readers move to separate processes; in that design, notifications need a shared transport or durable event mechanism. This is not a defect under the documented current topology.

### B7. Documentation contains at least one scope statement that needs reconciliation

**Classification:** documentation consistency · **Priority:** P2

`README.md` describes Worker responsibilities as expiry/cleanup tasks, while `ProjectBeacon.Worker/Program.cs` also registers `StaleCommandRecoveryService`. The actual code and the command recovery documentation should be authoritative; update the README's description so operational responsibilities are not understated.

---

## C. Target architecture: common metadata, specialized execution contracts

Do not make every domain concept an `IOperation` implementation. Introduce a small catalog layer that describes capabilities and connects them to specialized contracts.

### Conceptual model

1. **Operation definition (code-first, stable identity):** ID, version, summary, request/response types or schemas, required context, supported invocation surfaces, and execution metadata.
2. **Binding/reference:** a project, task phase, user, agent role, or device configuration references an operation/provider by stable ID and stores only the settings that vary by scope.
3. **Policy:** actor/resource/capability requirements are declared alongside the operation and enforced by the trusted server-side adapter/application boundary.
4. **Specialized executor/provider:** workstation command executor, agent runtime, model provider, CodeIndex backend, notification provider, etc.
5. **Surface adapter:** HTTP, Blazor/UI, MCP, CLI or internal invocation. An adapter exposes only operations explicitly allowed for that surface.

The operation catalog should be the common *description and discovery* layer, not a replacement for specialized contracts:
- `IAgentRuntime` remains appropriate for streamed agent sessions.
- `IModelBackend` remains appropriate for local process lifecycle.
- A workstation command handler remains appropriate for device-side commands.
- `ILanguageRegistry` remains appropriate for language discovery.
- CQRS remains appropriate for Application use cases.

Keep the operation definitions in code initially. Persist bindings and user choices only where they actually vary at runtime. Add DB-backed, user-defined operation definitions only if the product truly needs users to create capabilities rather than configure built-in ones.

### Expected dependency direction

```text
Operation definitions/catalog (stable metadata + typed contracts)
        |
        +-- policy metadata and validation
        |
        +-- binding/configuration (user / project / task / device)
        |
        +-- specialized executor/provider contracts
                 |
                 +-- API / UI / MCP / CLI adapters
                 |
                 +-- concrete implementations (OpenCode, llama-swap,
                     llama-server, Roslyn, Tree-sitter, etc.)
```

Adapters must not bypass application policies, and catalogue discovery must never grant permission by itself.

---

## D. Suggested implementation sequence

### P0 — clarify contracts before adding more functionality

1. Create a small operation descriptor and a code-first registry.
2. Inventory every `WorkstationCommandKind` with payload type, response type, execution host, project/device context, idempotency/retry behavior, and security requirements.
3. Resolve the model provider/runtime contract gap before activating real pipeline spawning for non-`LlamaCpp` model types.
4. Write down invariants for TaskKind phase semantics and test arbitrary custom keys/reordering.

**Exit condition:** the inventory identifies one authoritative contract per operation, and no newly added workstation operation needs another manually maintained list for dispatch/context classification without an explicit reason.

### P1 — centralize calls without rewriting business logic

1. Add typed command/query dispatchers over the existing handlers.
2. Migrate a small representative slice first (for example task read/write and one internal service), leaving other direct calls untouched.
3. Build MCP definitions from catalog entries marked MCP-compatible; keep local tools and security filters.
4. Introduce stable error codes and common surface mapping.

**Exit condition:** API, Web, and MCP adapters can invoke a representative operation without knowing its concrete handler class, and result/error semantics are consistent.

### P2 — separate providers and reconciled resources where justified

1. Decouple the model manager from concrete `LlamaServerBackend`.
2. Split local process launch descriptors from provider model IDs and remote endpoints.
3. Generalize desired-state reconciliation only if additional managed resource types are planned.
4. Convert custom MCP configuration to typed local/remote shapes.

**Exit condition:** adding a new implementation for an existing capability does not require changing all consumers of that capability.

### P3 — make extension points configurable only when needed

Add module/plugin registration or persistence for operation definitions only when independently shipped extensions or user-created operations become a concrete requirement. Until then, code-first registration is preferable.

---

## E. Acceptance criteria for a follow-up implementation

- Every workstation command has a single typed contract and a registered executor.
- Context requirements, idempotency/retry behavior and allowed invocation surfaces are explicit rather than inferred from scattered switches.
- Unknown/unregistered operations fail closed.
- Existing command envelope/version/lease/generation semantics and sandbox/auth gates remain intact.
- Model provider identity is distinct from local process lifecycle; the manager uses the interface where a backend abstraction exists.
- Configurable task phases have explicit semantics that do not silently depend on array index.
- API, UI, MCP and internal invocation apply equivalent business authorization.
- Errors expose stable machine codes while retaining safe human-readable messages.
- Existing EF-shaped persistence remains until a bounded need justifies a narrower port.
- Tests cover registry completeness, duplicate IDs, unknown IDs, version compatibility, access-policy parity, model-provider validation, and custom phase ordering.

## F. Explicit non-goals

- No microservice split as a prerequisite for better contracts.
- No universal base class for all domain entities or all functions.
- No dynamic execution of arbitrary code from the database.
- No automatic exposure of every operation through MCP or HTTP.
- No wholesale repository pattern rewrite of `IBeaconDb`.
- No replacement of the existing workstation command-delivery reliability protocol.
- No claim that the reviewed coupling automatically implies a security vulnerability or a current runtime failure.

## Source set inspected

The review focused on `ProjectBeacon.Domain`, `ProjectBeacon.Application`, `ProjectBeacon.Infrastructure`, `ProjectBeacon.API`, `ProjectBeacon.Web`, `ProjectBeacon.Cli`, `ProjectBeacon.Worker`, plus the current design, feature, authorization and endpoint-classification docs. Relevant source references are listed alongside each finding.
