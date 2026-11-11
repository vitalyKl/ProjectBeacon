# ProjectBeacon — Authorization Target Matrix (H1.1)

**Status:** implementation target for H1.2–H1.4.  
**Current behavior:** [ProjectBeacon-authorization-matrix.md](ProjectBeacon-authorization-matrix.md).  
**Scope:** hosted multi-tenant SaaS; HTTP actors, project/org roles, API-token capabilities, device actors and internal workers.

This document records the target authorization decisions. The companion matrix remains the evidence-based description of current behavior; its historical findings must not be rewritten as though the target were already enforced.

## 1. Non-negotiable rules

1. Authentication establishes who or what presented a credential; it does not itself grant access to a project, organization or resource.
2. Every project action is scoped to the resource's actual project and active organization. A route parameter, body field, header or stale JWT claim cannot broaden that scope.
3. An organization Owner/Admin may administer projects belonging to that organization. A project Owner/Admin may administer only that project. Organization membership with role Member does not automatically grant project membership or project-data access.
4. Project Member is the normal work role: use project work resources without receiving project/organization administration powers.
5. Owner/Admin manage memberships, invitations, tokens and project configuration. Only an Owner may grant, remove or transfer the Owner role. An Admin cannot promote itself or another user to Owner.
6. System Admin is a break-glass platform role, not an ordinary customer role. Every privileged bypass must be explicit and audited. Organization lifecycle restrictions (Suspended/Closed) still apply; system admin bypasses membership, not lifecycle policy.
7. API tokens are not humans. `CreatedByUserId` is audit/creator metadata, not proof of a human session. A token is pinned to one project and can act only through its capabilities.
8. The API-token `Admin` capability is not a project/org Owner or Admin role. It never grants membership, invitation, token-management, billing, organization or Control Plane access.
9. Device credentials identify one device. Device-owner metadata is not an acting human identity. A device may call only explicitly device-authorized endpoints and may not use customer task/project APIs as a human.
10. Worker is an internal service/database actor, never a public HTTP bearer. Its unscoped access is limited to the operations it must perform.
11. Missing actor, unknown resource/action, ambiguous scope, missing membership or mismatched project/org is denied. No generic allow fallback is permitted.
12. Resource authorization belongs in the Application/resource boundary. Endpoint metadata is a coarse first gate, not the only security decision. PostgreSQL RLS and EF query filters remain independent defense-in-depth.

## 2. Human role semantics

| Role | Read/use | Work mutations | Manage project resources | Manage organization | Owner transfer |
|---|---|---|---|---|---|
| Project Member | Assigned project data and normal work features | Create/update/work on tasks, comments, steps, context proposals and assigned sessions | No | No, unless independently an org manager | No |
| Project Admin | Same as Member | Same as Member | Yes, within the project | No, unless independently an org manager | No |
| Project Owner | Same as Member | Same as Member | Yes, within the project | No, unless independently an org owner | Yes, within that project |
| Org Member | Organization-level information allowed to members | No implicit project work access | No | No | No |
| Org Admin | Organization and project metadata; project data only through an explicit admin scope | Normal work in projects under the org | Yes, across projects in that org | Yes | No |
| Org Owner | Organization and project metadata; project data through an explicit admin scope | Normal work in projects under the org | Yes, across projects in that org | Yes | Yes, within the organization |
| System Admin | Cross-tenant only for an explicit support/operations purpose | Only through audited break-glass operations | Yes, subject to lifecycle guard | Yes, subject to lifecycle guard | Yes, audited |

**Membership resolution:** a user may hold both organization and project roles. The most privileged applicable role is used only for the resource's owning scope. An organization Member is not silently upgraded to project Member; an organization Admin/Owner is allowed to administer projects in that organization.

**Self-service onboarding:** an authenticated human may create a new organization. The creating user becomes its initial Owner in the same transaction. Creating a project inside an existing organization requires Org Admin/Owner. A project created without a valid active organization is rejected.

## 3. Resource/action target

Legend: **M** = project Member; **PA** = project Owner/Admin; **OA** = organization Owner/Admin for projects in that organization; **OO** = organization Owner; **SA** = audited System Admin.

| Resource | Read | Create | Update | Delete / revoke | Execute / drive | Administer |
|---|---|---|---|---|---|---|
| Project metadata | M, PA, OA, SA | OA (new project only) | PA, OA | OO or SA through lifecycle/retention workflow; no hard delete | — | PA, OA |
| Organization metadata | Org members, OA, SA | Any authenticated Human; creator becomes OO | OA | SA via terminal lifecycle workflow; no physical delete | — | OA |
| Membership | Own membership; PA/OA may list | PA/OA add Member/Admin; Owner grant is Owner-only | PA/OA within scope | PA/OA remove Member/Admin; removing Owner is Owner-only and cannot remove last Owner; self-leave is allowed unless it orphans ownership | Invite acceptance by addressed user | PA/OA; OO controls ownership |
| Invitations | PA/OA and invite recipient for own invitation | PA/OA | — | PA/OA | Recipient accepts/rejects own invitation | PA/OA |
| API tokens | PA/OA list metadata; secret shown once | PA/OA; granting `Admin` capability is Owner-only | — | PA/OA | Token acts only through its bound project/capabilities | PA/OA; no token manages tokens |
| Task | M, PA, OA, SA within scope | M, PA, OA | M, PA, OA | PA/OA; `TaskWrite` alone does not grant deletion | Task workflow as separately authorized | PA/OA |
| Task steps and comments | M reads project task activity | M adds steps/comments | M updates own authored content; PA/OA may moderate | Author may remove own content; PA/OA may moderate | — | PA/OA for moderation |
| Pipeline | M, PA, OA; tokens with `TaskRead` | M/PA/OA with `SessionDrive`; planner/actor/reviewer tokens only for pinned task and role | M/PA/OA with `SessionDrive` within task scope | Cancel/retry is a drive action and requires `SessionDrive`; force-close is Owner/Admin or scoped token `Admin` and audited | M/PA/OA with `SessionDrive`; harness tokens constrained by command, role, task, subtask and MCP allowlist | Approval requires PA/OA or explicitly assigned human reviewer; token reviewer submits only scoped verdict |
| Review | M may read review outcomes | Assigned reviewer or scoped Review token submits verdict | Assigned reviewer or PA/OA | — | Review/check execution requires `SessionDrive` and pinned task | PA/OA controls review lifecycle and final approval |
| Context and constraints | M, PA, OA; tokens with `ContextRead` | M may propose/upsert/import context and constraints | M may revise proposals; activation/rejection is PA/OA | PA/OA may remove authoritative context; proposed content follows author/review lifecycle | Compile/export is read-only and available to M or `ContextRead` token | PA/OA activates/rejects constraints and administers authoritative context |
| Decisions | M, PA, OA; tokens with `ContextRead` | M may propose | PA/OA accept/deprecate/supersede; authorship alone does not approve | — | — | PA/OA |
| Milestones | M, PA, OA | PA/OA | PA/OA | PA/OA | Close/reopen by PA/OA | PA/OA |
| Labels | M, PA, OA; tokens with `TaskRead` | PA/OA manage taxonomy | PA/OA add/remove paths | PA/OA | Match is read-only | PA/OA |
| Reports | M, PA, OA within project | M, PA, OA may generate project reports, subject to request limits | — | No user-facing delete; retention is platform-managed | Generate for a task in the same project | PA/OA manage report policy/retention |
| Chat | User reads own sessions; shared sessions require explicit participant membership | M creates own session | Owner/participants update prompt and session state | Owner may archive/delete own session if supported; Admin does not silently gain private message content | Session drive requires membership; device ingestion requires session's bound device | PA/OA manage project-wide chat settings, not private message contents |
| Eval | M may read own/project-visible results | M may start an evaluation in the project | Run owner or PA/OA | Run owner or PA/OA when supported | Run requires project membership and bounded inputs | PA/OA manage evaluation configuration |
| Device | Device owner sees own device; PA/OA see project devices | Human registers own device; PA/OA may provision for project | Owner updates own metadata; PA/OA manage assignment | Owner revokes own device; PA/OA revoke project devices | Device heartbeats/claims/completes only its own authorized commands | PA/OA manage enrollment; SA break-glass |
| Runtime | M may use assigned runtime; PA/OA inspect bindings | PA/OA attach/manage bindings; Member may attach own workstation where H10 permits | PA/OA manage bindings; device owner manages own device config | PA/OA detach bindings; device owner revokes own device | M uses assigned runtime; device executes commands addressed to its identity | PA/OA |
| Model backend | M uses project-bound backends; sensitive config hidden | PA/OA | PA/OA | PA/OA | Bound model use is available to project work; reload/unload/proxy administration is PA/OA | PA/OA |
| Agent template | M uses templates made available to project | PA/OA | PA/OA | PA/OA | Instantiate only within project/task scope | PA/OA |
| MCP | M uses project-configured servers and exposed tools | PA/OA configure | PA/OA configure | PA/OA configure | Tool use additionally constrained by token/session allowlists | PA/OA |
| Workstation configuration | Member sees own workstation config; PA/OA see policy metadata, not private secrets by default | Member registers own workstation; PA/OA create project policy | Member edits own settings; PA/OA manage shared policy | Member removes own workstation; PA/OA revoke project enrollment | Member uses own workstation; agent/device pinned to task, device, role and command scope | PA/OA |
| Work / finish-work | No general browse permission | — | Completion reported by assigned human or scoped API token | — | Human must belong to task project; API token needs `TaskWrite` and task/project binding; worker completion is internal-only | Force-close is separately privileged and audited |
| Worker | No public HTTP access | Internal work creation only through trusted service | Internal task/ledger updates within worker contract | No customer membership management | Only background operations granted to database/service role | Never a customer or platform-console identity |

## 4. API-token capability contract

Capabilities are scoped to the token's single bound project. They do not establish human membership and are evaluated independently of the token creator.

| Capability | Grants | Does not grant |
|---|---|---|
| `TaskRead` | Read tasks, task steps/comments, pipeline state and labels explicitly exposed to token routes | Any write, session drive, context read, project/org administration |
| `TaskWrite` | Create/update tasks and task-associated content explicitly mapped to TaskWrite routes | Task deletion, project metadata, memberships, invites, tokens, model config, session execution by implication |
| `SessionDrive` | Drive a pinned pipeline/session/subtask and submit results for its assigned role | General task metadata writes, project administration, human approval |
| `ContextRead` | Read/list/export/compile project context and constraints | Context writes/import, activation/rejection, decisions or project administration |
| `Admin` | Union of the four project-agent capabilities above and narrow force-close, only within the bound project and with audit | Human/System Admin identity, Owner/Admin role, membership/invites/tokens, billing, organization or Control Plane access |

Endpoint metadata must request the narrowest capability. Pipeline drive routes use `SessionDrive`, not `TaskWrite`; destructive task deletion and project/organization management remain human-manager operations.

## 5. Device and worker contracts

- Device authentication is a separate actor type. A device may use only routes carrying device authorization metadata. It cannot pass human or API-token routes by carrying owner metadata.
- The device owner may manage/revoke their own device. Project Member use and Owner/Admin binding management follow the Runtime row above.
- Worker is not represented by `ActorContext` on the public HTTP surface. Background worker identity comes from the service/database boundary and receives only operations required by its job contract.

## 6. Denial and audit semantics

- Missing/invalid identity: 401 at the authentication boundary.
- Authenticated actor with wrong actor type, role, capability, project, organization, device, task or subtask scope: 403; use 404 where needed to avoid cross-tenant resource enumeration.
- Inactive organization: deny customer data access regardless of role or system-admin status. Operational recovery is a separate audited Control Plane workflow.
- Owner transfer, token creation/revocation, member/invite changes, model/runtime administration, force-close, break-glass support and organization lifecycle changes produce audit events.
- Audit records contain stable actor type/id, target resource, action and outcome. Secrets, bearer tokens, passwords and private chat content are never copied into audit metadata.

## 7. Implementation sequence

1. H1.2: make the unified authorization service fail closed for unknown actor/resource/action and add scoped project/org role resolution.
2. H1.3: migrate API routes to typed actor/capability metadata, including `SessionDrive`; keep route inventory exhaustive.
3. H1.4: enforce the same decisions at the resource/handler boundary, not only endpoint filters. Start with Project/Org/Member/Invite/Token, then Task/Pipeline/Context/Decision/Milestone/Model/Runtime.
4. Add positive and negative tests for Member, Admin, Owner, Org Admin/Owner, System Admin, every token capability, Device owner, non-member and cross-project/cross-org attempts. Keep the current-behavior matrix as comparison baseline until each row has implementation and test proof.

## 8. Explicitly rejected shortcuts

- No "authenticated human = project administrator" rule.
- No "API token creator = human actor" rule.
- No token `Admin` shortcut into project, organization, platform or Control Plane administration.
- No implicit membership derived solely from organization membership with role Member.
- No device-owner claim treated as a human session.
- No generic allow fallback for a new `ResourceType` or `AuthAction`.
- No system-admin bypass of Suspended/Closed organization lifecycle controls.
