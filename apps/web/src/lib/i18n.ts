export const LOCALES = [
  "en",
  "es",
  "uk",
  "fr",
  "de",
  "pt",
  "pl",
  "it",
  "ja",
  "zh",
  "ru",
  "lt",
  "lv",
  "be",
  "kk",
  "ko",
] as const;

export type Locale = (typeof LOCALES)[number];

export const LOCALE_STORAGE_KEY = "beacon.locale";

export const LOCALE_LABELS: Record<Locale, string> = {
  en: "English",
  es: "Español",
  uk: "Українська",
  fr: "Français",
  de: "Deutsch",
  pt: "Português",
  pl: "Polski",
  it: "Italiano",
  ja: "日本語",
  zh: "简体中文",
  ru: "Русский",
  lt: "Lietuvių",
  lv: "Latviešu",
  be: "Беларуская",
  kk: "Қазақша",
  ko: "한국어",
};

const STRINGS = {
  en: {
    "nav.home": "Home",
    "nav.board": "Board",
    "nav.backlog": "Backlog",
    "nav.roadmap": "Roadmap",
    "nav.context": "Context",
    "nav.files": "Files",
    "nav.agents": "Agents",
    "nav.decisions": "Decisions",
    "nav.reports": "Reports",
    "nav.settings": "Settings",
    "nav.learn": "Learn",
    "common.loading": "Loading…",
    "common.selectProject": "Select a project from the header.",
    "common.language": "Language",
    "common.org": "Org",
    "common.project": "Project",
    "common.logout": "Log out",
    "common.failedSession": "failed to load session",
    "common.failedProjects": "failed to load projects",
    "common.logoutFailed": "logout failed",
    "reports.title": "Reports",
    "reports.intro":
      "Generate a snapshot of the current board, or import a review so agents can read it and create follow-up tasks.",
    "reports.generate": "Generate report",
    "reports.generating": "Generating…",
    "reports.snapshots": "Snapshots",
    "reports.emptyReports": "No reports yet. Generate one when you want a checkpoint agents can read.",
    "reports.reviews": "Imported reviews",
    "reports.reviewsIntro":
      "Paste a review, audit, or user note. Agents can list these and turn findings into tasks.",
    "reports.reviewTitle": "Title (optional)",
    "reports.reviewBody": "Review markdown",
    "reports.import": "Import review",
    "reports.importing": "Importing…",
    "reports.emptyReviews": "No reviews imported yet.",
    "reports.loadFailed": "failed to load reports",
    "reports.generateFailed": "failed to generate report",
    "reports.importFailed": "failed to import review",
    "learn.title": "Learn",
    "learn.intro":
      "Beacon is the operating system around your agents. Humans keep the brief and the board. Local agents pick up Ready work.",
    "learn.start": "Start here",
    "learn.startBody":
      "Create or pick a project in the header. Write the living brief in Context. Put work on the Board as Ready. Mint a project token on Agents, then run setup on the machine that hosts the agent.",
    "learn.screens": "What each screen is for",
    "learn.home":
      "Home is the current snapshot: brief, milestones, index status, and work that is ready or in flight.",
    "learn.board":
      "Board is the living queue. Drag a card to change status. Ready is what local agents can start.",
    "learn.backlog": "Backlog is the same work as a list. Use it when you want to scan or re-status quickly.",
    "learn.roadmap":
      "Roadmap groups work by milestone. Dependencies show which task must finish before another can start.",
    "learn.context":
      "Context is the living project brief. Compile a session brief from here or from a task. AGENTS.md is an export. The board is the queue. The code index is optional.",
    "learn.files":
      "Files is a read-only tree of the connected repo. Attach a local path when none is connected. Open a file to see an excerpt when the local sidecar or bind-mount index is up.",
    "learn.agents":
      "Agents is where you mint a project token and watch sessions. Beacon does not run a hosted coding agent.",
    "learn.decisions": "Decisions and constraints are the durable rules agents should not invent around.",
    "learn.reports":
      "Reports is a checkpoint of the board plus imported reviews. Agents can list them and turn findings into tasks.",
    "learn.settings":
      "Settings holds the project id, members, local repo attach, and area labels. Areas are scoped prefixes, not free-form chips.",
    "learn.check": "How to check a finished task",
    "learn.checkBody":
      "Open the task. Read How to check. Follow those steps in the app. If the notes are empty, ask the agent to write them on finish_work.",
    "learn.multi": "More than one project",
    "learn.multiBody":
      "Each Beacon project needs its own token. Run beacon connect or beacon setup again with that project id. Local MCP can switch with --project, BEACON_PROJECT, or project_id on a tool call.",
    "learn.guide": "First hour",
    "learn.guideOne": "1. Write Goals and Definition of Done in Context.",
    "learn.guideTwo": "2. Create a Ready task with How to check filled in.",
    "learn.guideThree": "3. Mint a token on Agents and run setup.cmd or beacon setup.",
    "learn.guideFour": "4. Let the agent call start_work, then confirm the change from How to check.",
    "files.intro":
      "Browse the connected repository. This is a read-only excerpt from the local sidecar or bind-mount index. File bodies stay on the machine.",
    "files.repo": "Repository",
    "files.loadFailed": "failed to load files",
    "files.emptyRepos": "No repository is connected to this project yet.",
    "files.emptyReposHint":
      "Attach a local path here, or run the new-project wizard. GitHub App import is not available on this instance.",
    "files.unavailable": "Code index is not available.",
    "files.unavailableHint":
      "Start the local sidecar or bind-mount worker index to browse files. Compile and the board still work without it.",
    "files.emptyTree": "The index has no tree for this repository yet.",
    "files.excerptRange": "Lines {start}–{end}",
    "files.loadingFile": "Loading file…",
    "files.binary": "This file is binary and cannot be shown.",
    "files.notFound": "That path is not in the index.",
    "files.more": "Show more",
    "files.selectFile": "Select a file to read a local excerpt.",
    "settings.language": "Language",
    "settings.languageHint": "Choose the language for labels, empty states, and this Learn page.",
    "home.welcome": "Create a project to start the board. Beacon will add a first milestone and a few starter tasks.",
    "common.selectOrg": "Select an org to continue.",
    "common.name": "Name",
    "common.slug": "Slug",
    "common.title": "Title",
    "common.description": "Description",
    "common.status": "Status",
    "common.save": "Save",
    "common.saving": "Saving…",
    "common.cancel": "Cancel",
    "common.creating": "Creating…",
    "common.createProject": "Create project",
    "common.compileBrief": "Compile brief",
    "common.compiling": "Compiling…",
    "common.edit": "Edit",
    "common.path": "Path",
    "common.content": "Content",
    "common.copied": "Copied",
    "common.copy": "Copy",
    "common.invite": "Invite",
    "common.role": "Role",
    "common.apply": "Apply",
    "common.continue": "Continue",
    "common.never": "never",
    "common.repository": "Repository",
    "common.brand": "Beacon",
    "common.failedLoadHome": "failed to load home",
    "common.failedCreateProject": "failed to create project",
    "common.failedLoadWork": "failed to load work",
    "common.failedUpdateStatus": "failed to update status",
    "common.conflictReapplied": "Updated elsewhere — reapplied.",
    "common.failedUpdateTask": "failed to update task",
    "common.failedLoadSettings": "failed to load settings",
    "common.failedRefreshSettings": "failed to refresh settings",
    "common.failedUpdateProject": "failed to update project",
    "common.failedUpdateMember": "failed to update member",
    "common.failedCreateInvite": "failed to create invite",
    "common.failedUpdateRepo": "failed to update repository",
    "common.failedAttachRepo": "failed to attach repository",
    "common.nameRequired": "name is required",
    "common.titleRequired": "title is required",
    "common.inviteTargetRequired": "email or GitHub login is required",
    "status.backlog": "backlog",
    "status.ready": "ready",
    "status.in_progress": "In progress",
    "status.in_review": "In review",
    "status.blocked": "blocked",
    "status.done": "done",
    "status.canceled": "canceled",
    "decisionStatus.proposed": "proposed",
    "decisionStatus.accepted": "accepted",
    "decisionStatus.superseded": "superseded",
    "decisionStatus.deprecated": "deprecated",
    "constraintKind.must": "must",
    "constraintKind.must_not": "must not",
    "constraintKind.security": "security",
    "constraintKind.compliance": "compliance",
    "constraintStatus.proposed": "proposed",
    "constraintStatus.active": "active",
    "constraintStatus.rejected": "rejected",
    "index.notConnected": "Not connected",
    "index.bindMount": "bind mount",
    "index.hostedClone": "hosted clone",
    "index.sidecar": "sidecar",
    "index.both": "both",
    "index.connected": "connected",
    "index.offline": "offline",
    "index.mode": "Mode",
    "index.sidecarLabel": "Sidecar",
    "index.worker": "Worker",
    "index.lastIndexed": "Last indexed",
    "index.title": "Index",
    "home.brief": "Project brief",
    "home.editInContext": "Edit in Context",
    "home.noBrief":
      "No project brief yet. Open Context to write Goals, Architecture, and the other sections, then compile a session brief from there or a task.",
    "home.milestones": "Milestones",
    "home.noMilestones": "No open milestones yet.",
    "home.ready": "Ready for agents",
    "home.noReady": "No ready tasks. Move a card to Ready and a connected agent can call start_work.",
    "home.inFlight": "In flight",
    "home.noInFlight": "Nothing in progress. Open the backlog to pick up work.",
    "home.newHereBefore": "New here? Open",
    "home.newHereAfter":
      "for what each screen is for, how to check finished work, and how local agents use more than one project.",
    "board.hint": "Drag a card to any status. A human move releases an agent lock.",
    "board.filterArea": "Filter by area",
    "board.allAreas": "All areas",
    "backlog.hint": "List view. Change status from any row.",
    "backlog.empty": "No tasks yet. Create one to start the board.",
    "backlog.statusFor": "Status for {title}",
    "lock.title": "An agent holds this task",
    "lock.label": "Locked",
    "lock.suffix": " · locked",
    "task.new": "New task",
    "task.howToCheck": "How to check",
    "task.howToCheckHint": "How to check (what a human should do after this is done)",
    "task.howToCheckHelp": "What a human should click or run after an agent finishes this task.",
    "task.howToCheckPlaceholder":
      "Open the changed screen, try the empty and error states, then confirm the happy path.",
    "task.noMilestone": "No milestone",
    "task.areas": "Areas",
    "task.proposed": "proposed",
    "task.failedCreate": "failed to create task",
    "task.notFound": "task not found",
    "task.backToBoard": "Back to board",
    "task.agent": "Agent {name}",
    "task.noAreas":
      "No areas yet. Add them in Settings so compile and changed-scope can follow path prefixes.",
    "task.sessionBrief": "Session brief",
    "task.sessionBriefHint":
      "Compile merges the project brief, this task, attached areas, and any extras that are available. The index is optional.",
    "task.compileAgain": "Compile again",
    "task.offered":
      "This task is Ready. Connected agents can call start_work and receive this compiled brief. Beacon does not run a hosted coding agent.",
    "task.briefUnavailable": "Brief unavailable. {error}",
    "task.compileWhen": "Compile when you want the session brief. Edit the living project brief in Context.",
    "task.milestone": "Milestone: {title}",
    "task.noBriefSections": "No brief sections compiled. Write the project brief in Context first.",
    "task.dropped": "Dropped: {items}",
    "task.comments": "Comments",
    "task.noComments": "No comments yet.",
    "task.writeComment": "Write a comment",
    "task.comment": "Comment",
    "task.posting": "Posting…",
    "task.agentActivity": "Agent activity",
    "task.noAgentEvents": "No agent events on this task yet.",
    "task.failedComment": "failed to add comment",
    "task.failedLabels": "failed to update labels",
    "task.briefUnavailableShort": "brief unavailable",
    "task.title": "Task",
    "copy.projectId": "Project ID",
    "placeholder.stub":
      "This screen is a stub. Context editor, compile preview, and local code tools are available.",
    "sidecar.banner":
      "This project is serving code through the sidecar tunnel. File contents pass through the control plane.",
    "brief.empty": "No brief sections yet.",
    "landing.title": "Hosted and self-host are equal paths.",
    "landing.intro": "Sign in on this instance. Hosted GitHub and self-host login are equal first steps.",
    "landing.hosted": "Hosted",
    "landing.hostedBody": "Continue with GitHub on the hosted control plane. Optional on self-host.",
    "landing.continueGithub": "Continue with GitHub",
    "landing.githubDisabled":
      "GitHub sign-in is not enabled on this instance. Use self-host login or bootstrap.",
    "landing.selfHost": "Self-host",
    "landing.selfHostBody":
      "Local username and password, or first-user bootstrap with the operator token.",
    "landing.localLogin": "Local login",
    "landing.bootstrap": "First-user bootstrap",
    "login.title": "Local login",
    "login.intro": "Username and password for this Compose or air-gapped instance.",
    "login.username": "Username",
    "login.password": "Password",
    "login.signIn": "Sign in",
    "login.signingIn": "Signing in…",
    "login.invalid": "invalid login or password",
    "login.githubInstead": "Continue with GitHub instead",
    "login.firstUser": "First user? Bootstrap this instance",
    "login.back": "Back to both paths",
    "github.failedTitle": "GitHub sign-in",
    "github.failedDefault": "GitHub sign-in failed",
    "github.backHome": "Back home",
    "bootstrap.title": "First-user bootstrap",
    "bootstrap.intro":
      "Create the first user with the operator bootstrap token. This works once; after that, use local login.",
    "bootstrap.token": "BOOTSTRAP token",
    "bootstrap.passwordHint": "Password must be at least 10 characters.",
    "bootstrap.create": "Create first user",
    "bootstrap.creating": "Creating first user…",
    "bootstrap.failed": "bootstrap failed",
    "bootstrap.already": "Already bootstrapped? Local login",
    "settings.selectProject": "Select a project to edit members and settings.",
    "settings.forProject": "Project members and settings for {name}.",
    "settings.members": "Members",
    "settings.membersHint": "These roles apply only to this project.",
    "settings.inviteTarget": "Email or GitHub login",
    "settings.inviteExpiresIn": "Invites expire in 7 days.",
    "settings.sending": "Sending…",
    "settings.sendInvite": "Send invite",
    "settings.noMembers": "No members yet.",
    "settings.pendingInvites": "Pending invites",
    "settings.inviteMeta": "{role} · expires {when}",
    "settings.roleFor": "Role for {name}",
    "settings.sidecarTunnel": "Sidecar tunnel",
    "settings.sidecarHint":
      "When a sidecar is connected, remote code tools can read this machine through the control plane.",
    "settings.connectRepo": "Connect a repository first.",
    "settings.repositories": "Repositories",
    "settings.repositoriesHint":
      "Local sidecar or Compose bind-mount. File bodies stay on the machine. GitHub two-way sync and hosted clone stay off.",
    "settings.noRepos": "No repository is connected to this project yet.",
    "settings.attachLocal": "Attach local repository",
    "settings.attaching": "Attaching…",
    "settings.localRootHint":
      "Relative POSIX path under the workspace or this machine (for example . or apps/web). Absolute paths and parent traversal are rejected.",
    "settings.invalidLocalPath": "Enter a relative POSIX path. Absolute paths and .. are not allowed.",
    "settings.askAdminRepo": "An admin can attach a local repository to this project.",
    "settings.sidecarOn": "Sidecar connected. Code tools may transit file contents.",
    "settings.sidecarOff": "Sidecar offline.",
    "settings.hostedClone": "Hosted clone",
    "settings.hostedHint":
      "Index a GitHub clone on this instance. Leave this off unless you want source on the server.",
    "settings.index": "Index",
    "settings.roleAdmin": "Admin",
    "settings.roleWrite": "Write",
    "settings.roleRead": "Read",
    "settings.modeSidecar": "This machine",
    "settings.modeBindMount": "Compose workspace",
    "settings.modeHostedClone": "Hosted clone",
    "settings.modeBoth": "Sidecar, then hosted clone",
    "labels.areas": "Areas",
    "labels.areasHint":
      "Catalog of scoped areas. New projects start with API, Web, CLI, Visual, and UX. Active labels expand compile and changed-scope to their path prefixes.",
    "labels.newArea": "New area",
    "labels.color": "Color",
    "labels.noPrefix": "No path prefix",
    "labels.empty": "No areas yet. Create backend, web, or UX scopes here so compile stays scoped.",
    "labels.activate": "Activate",
    "labels.pathPrefixes": "Path prefixes",
    "labels.noneYet": "None yet. Detect may suggest one later.",
    "labels.remove": "Remove",
    "labels.repoForPrefix": "Repository for prefix",
    "labels.chooseRepo": "Choose repo",
    "labels.addPrefix": "Add prefix",
    "labels.saveArea": "Save area",
    "labels.noPrefixes": "No path prefixes",
    "labels.failedLoad": "failed to load labels",
    "labels.failedCreate": "failed to create label",
    "labels.failedActivate": "failed to activate label",
    "labels.failedUpdate": "failed to update label",
    "labels.failedPaths": "failed to update path prefixes",
    "labels.chooseRepoPath": "choose a repo and path prefix",
    "agents.selectProject": "Select a project to see sessions, activity, and tokens.",
    "agents.intro":
      "Ready tasks are offered to connected agents. Mint a token here, then on the machine that will host the agent double-click setup.cmd (Windows) or run ./setup.sh / beacon setup. Paste the token when the console asks. Setup writes Grok, Cursor, and Claude MCP configs so those agents can call Beacon. The token stays in BEACON_HOME under that project id and is not copied into those files. Run setup or beacon connect again for another project. Switch with beacon mcp --project or BEACON_PROJECT. Beacon does not run a hosted coding agent.",
    "agents.noRepos": "No repositories yet.",
    "agents.ready": "Ready for agents",
    "agents.noReady":
      "No ready tasks. Move a card to Ready and a connected beacon mcp agent can start it.",
    "agents.offered": "Offered · start_work",
    "agents.sessions": "Active sessions",
    "agents.noSessions": "No active sessions.",
    "agents.lastSeen": "{host} · last seen {when}",
    "agents.activity": "Activity",
    "agents.noActivity": "No activity yet.",
    "agents.tokens": "Tokens",
    "agents.newToken": "New token",
    "agents.copyNow": "Copy this token now. It will not be shown again.",
    "agents.copyToken": "Copy token",
    "agents.expires": "Expires",
    "agents.ttl7d": "7 days",
    "agents.ttl90d": "90 days",
    "agents.ttl1y": "1 year",
    "agents.ttlNone": "No expiry",
    "agents.confirmNever": "I confirm this token should never expire.",
    "agents.allowCode": "Allow code tools (tree, file, symbols)",
    "agents.createToken": "Create token",
    "agents.adminsOnly": "Project admins can mint and revoke tokens.",
    "agents.noTokens": "No tokens yet.",
    "agents.codeOn": "code tools on",
    "agents.codeOff": "code tools off",
    "agents.tokenMeta": "{prefix}… · {code} · expires {when}",
    "agents.revoke": "Revoke",
    "agents.failedLoad": "failed to load agents",
    "agents.failedRefresh": "failed to refresh agents",
    "agents.confirmExpiry": "Confirm that this token should never expire.",
    "agents.failedCreate": "failed to create token",
    "agents.failedRevoke": "failed to revoke token",
    "agents.indexLine": "Index: {value}",
    "agents.sidecarLine": "Sidecar: {value}",
    "agents.workerLine": "Worker: {value}",
    "agents.lastIndexedLine": "Last indexed: {value}",
    "decisions.intro":
      "ADRs and always-on constraints for {name}. Applying a constraint needs project admin.",
    "decisions.adrs": "ADRs",
    "decisions.empty": "No decisions yet.",
    "decisions.constraints": "Constraints",
    "decisions.noConstraints": "No constraints yet.",
    "decisions.new": "New decision",
    "decisions.context": "Context",
    "decisions.decision": "Decision",
    "decisions.consequences": "Consequences (optional)",
    "decisions.status": "Decision status",
    "decisions.required": "title, context, and decision are required",
    "decisions.failedCreate": "failed to create decision",
    "decisions.failedLoad": "failed to load decisions",
    "decisions.failedApply": "failed to apply constraint",
    "decisions.newConstraint": "New constraint",
    "decisions.rule": "Rule",
    "decisions.scopeOptional": "Scope path (optional)",
    "decisions.kind": "Constraint kind",
    "decisions.constraintStatus": "Constraint status",
    "decisions.bodyRequired": "body is required",
    "decisions.failedCreateConstraint": "failed to create constraint",
    "roadmap.hint": "Timeline groups work by milestone. Dependencies show which tasks block others.",
    "roadmap.timeline": "Timeline",
    "roadmap.dependencies": "Dependencies",
    "roadmap.empty": "No milestones or tasks yet.",
    "roadmap.noTasks": "No tasks yet.",
    "roadmap.unscheduled": "Unscheduled",
    "roadmap.noUnscheduled": "No unscheduled tasks.",
    "roadmap.noLinks":
      "No task links yet. A blocks link means the first task must finish before the second can start. Relates is a note, not a gate.",
    "roadmap.graphHint":
      "Solid red arrows mean blocks. Dashed gray arrows mean relates. Click a card to open the task.",
    "roadmap.graphLabel": "Task dependency graph",
    "roadmap.links": "Links",
    "roadmap.addLink": "Add link",
    "roadmap.needTwo": "Create at least two tasks before linking them.",
    "roadmap.thisTask": "This task",
    "roadmap.type": "Type",
    "roadmap.thatTask": "That task",
    "roadmap.blocks": "blocks",
    "roadmap.relates": "relates",
    "roadmap.relatesTo": "relates to",
    "roadmap.chooseTwo": "choose two tasks",
    "roadmap.cycle": "That link would create a cycle.",
    "roadmap.failedAdd": "failed to add dependency",
    "roadmap.failedLoad": "failed to load roadmap",
    "roadmap.open": "open",
    "roadmap.closed": "closed",
    "context.selectProject": "Select a project to edit its brief.",
    "context.intro":
      "This is the living brief. Agents compile it. AGENTS.md is an export for hosts that only read the repo. Import is a one-time bootstrap, not how you keep the brief current. What to do next lives on the board, not here.",
    "context.importing": "Importing…",
    "context.importFiles": "Import files",
    "context.pasteFile": "Paste file",
    "context.exporting": "Exporting…",
    "context.export": "Export AGENTS.md",
    "context.importPasted": "Import pasted file",
    "context.nodes": "Nodes",
    "context.newBrief": "New brief",
    "context.empty":
      "No brief yet. Write one here, or import an AGENTS.md / CLAUDE.md / conventions file.",
    "context.importedReview": "Imported, review",
    "context.previewTab": "Agent preview",
    "context.revisionsTab": "Revisions",
    "context.newBriefHint": "New brief. Save to create it.",
    "context.scope": "Scope",
    "context.scopeProject": "project",
    "context.scopeRepo": "repo",
    "context.scopePath": "path",
    "context.repoId": "Repo id",
    "context.markReviewed": "Mark reviewed",
    "context.compilePreview": "Compile preview",
    "context.previewHint":
      "The preview still works if some extras are unavailable. Omitted parts are listed below.",
    "context.noRevisions": "No stored revisions yet. Compile a preview to keep one.",
    "context.revision": "Revision {id}",
    "context.nothingDropped": "Nothing dropped.",
    "context.noSections": "No sections in this brief yet.",
    "context.dropped": "Dropped: {items}",
    "context.saved": "Saved.",
    "context.markedReviewed": "Marked reviewed.",
    "context.failedLoad": "failed to load context",
    "context.failedRevisions": "failed to load revisions",
    "context.pathRequired": "path is required for path scope",
    "context.repoRequired": "repo id is required for this scope",
    "context.failedSave": "failed to save context",
    "context.failedPreview": "failed to compile preview",
    "context.failedImport": "failed to import files",
    "context.failedExport": "failed to export",
    "context.section.goals": "Goals",
    "context.section.non_goals": "Non-goals",
    "context.section.architecture": "Architecture",
    "context.section.stack": "Tech stack",
    "context.section.conventions": "Conventions",
    "context.section.style": "Style",
    "context.section.commands": "Commands",
    "context.section.definition_of_done": "Definition of Done",
    "context.section.security": "Security",
    "context.section.pitfalls": "Pitfalls",
    "context.section.glossary": "Glossary",
    "context.section.ownership": "Ownership",
    "context.source.native": "Native",
    "context.importedOne": "Imported 1 file for review.",
    "context.importedMany": "Imported {count} files for review.",
    "context.unrecognized":
      "No recognized context files. Import AGENTS.md, CLAUDE.md, CONVENTIONS.md, Cursor or Grok rules, CODEOWNERS, or a package manifest.",
    "wizard.newProject": "New project",
    "wizard.newProjectOrg": "New project · {org}",
    "wizard.stepCreate": "Create project",
    "wizard.stepConnect": "Connect code",
    "wizard.stepDetect": "Detect",
    "wizard.stepBrief": "First brief",
    "wizard.stepAgent": "Connect an agent",
    "wizard.starterHint":
      "Visibility is private. The org comes from the header switcher. A starter catalog of areas (API, Web, CLI, Visual, UX) is created so tasks and compile can stay scoped.",
    "wizard.github": "GitHub repository",
    "wizard.githubLater":
      "GitHub App import is available later from Settings. Continue with this machine or a workspace path.",
    "wizard.githubUnavailable": "Unavailable on this instance. Coming later.",
    "wizard.thisMachine": "This machine",
    "wizard.mintHint": "Mint a CLI token and run beacon connect on the machine that has the code.",
    "wizard.mintAnother": "Mint another token",
    "wizard.mintCli": "Mint CLI token",
    "wizard.shownOnce": "Shown once. Copy it now.",
    "wizard.indexWorkspace": "Index this Compose workspace",
    "wizard.indexHint":
      "Relative POSIX path under the workspace volume. Creates a local bind-mount repo.",
    "wizard.connected": "Connected",
    "wizard.indexPath": "Index this path",
    "wizard.detectHint":
      "Detect runs in the background. You can continue without waiting. If the layout has known directories such as apps/api, Detect binds those prefixes onto the starter areas that still have none.",
    "wizard.noRepo": "No repo connected yet. Detection can run later.",
    "wizard.startDetect": "Start detect",
    "wizard.runLater": "Run later",
    "wizard.skip": "Skip for now",
    "wizard.detectPending": "Detection is pending. You can run it later and keep editing this brief.",
    "wizard.firstMilestone": "First milestone",
    "wizard.firstTask": "First task",
    "wizard.attachAreas": "Attach starter areas",
    "wizard.attachHint": "Suggested from project create. Edit names and path prefixes later in Settings.",
    "wizard.savedContinue": "Saved — continue",
    "wizard.saveBrief": "Save brief",
    "wizard.stdio": "stdio / beacon mcp",
    "wizard.http": "HTTP MCP",
    "wizard.stdioHint": "Local agents get context and code through beacon mcp.",
    "wizard.httpHint":
      "Control plane (context & tasks). Code tools need a local sidecar, a self-host bind-mount, or an enabled hosted clone.",
    "wizard.openProject": "Open project",
    "wizard.selectOrg": "Select an org in the header first.",
    "wizard.failedMint": "failed to mint token",
    "wizard.failedIndex": "failed to index workspace",
    "wizard.failedDetect": "failed to start detect",
    "wizard.failedBrief": "failed to save brief",
    "wizard.defaultGoals": "Ship a first working loop for this project.",
    "wizard.defaultNonGoals": "Do not invent extra scope before the first milestone.",
    "wizard.defaultStack": "Languages, frameworks, package manager, and how this repo is laid out.",
    "wizard.defaultCommands": "Install, typecheck, lint, test, and run the local app.",
    "wizard.defaultDod":
      "A task is done only when all of these are true:\n- Typecheck and lint are clean in the packages you changed.\n- Tests cover the new behavior and are green.\n- The screens or APIs you touched still work, including empty and error states.",
    "wizard.defaultMilestone": "First slice",
    "wizard.defaultTask": "Confirm repo layout and fill the first brief",
    "wizard.firstTaskDescription": "Proposed first task from the new-project wizard.",
    "layout.description": "Project operating system for mixed human + AI-agent development.",
    "common.priority": "Priority",
    "priority.urgent": "Urgent",
    "priority.high": "High",
    "priority.normal": "Normal",
    "priority.low": "Low",
    "priority.custom": "P{value}",
    "labelStatus.proposed": "proposed",
    "labelStatus.active": "active",
    "reviewStatus.needs_review": "needs review",
    "reviewStatus.reviewed": "reviewed",
    "activity.create": "create",
    "activity.update": "update",
    "activity.delete": "delete",
    "activity.status": "status",
    "activity.comment": "comment",
    "activity.start_work": "start work",
    "activity.finish_work": "finish work",
    "activity.lock_stolen": "lock stolen",
    "activity.lock_released": "lock released",
    "activity.propose": "propose",
    "activity.apply": "apply",
    "activity.import": "import",
    "activity.github_clone_invalidated": "GitHub clone invalidated",
    "activity.line": "{verb} · {object}",
    "context.scopePathEmpty": "(path)",
    "context.scopeRepoWithPath": "repo / {path}",
  },
  es: {
    "nav.home": "Inicio",
    "nav.board": "Tablero",
    "nav.backlog": "Backlog",
    "nav.roadmap": "Hoja de ruta",
    "nav.context": "Contexto",
    "nav.files": "Archivos",
    "nav.agents": "Agentes",
    "nav.decisions": "Decisiones",
    "nav.reports": "Informes",
    "nav.settings": "Ajustes",
    "nav.learn": "Aprender",
    "common.loading": "Cargando…",
    "common.selectProject": "Elige un proyecto en la cabecera.",
    "common.language": "Idioma",
    "common.org": "Org",
    "common.project": "Proyecto",
    "common.logout": "Salir",
    "common.failedSession": "no se pudo cargar la sesión",
    "common.failedProjects": "no se pudieron cargar los proyectos",
    "common.logoutFailed": "no se pudo cerrar la sesión",
    "reports.title": "Informes",
    "reports.intro":
      "Genera una instantánea del tablero o importa una revisión para que los agentes la lean y creen tareas.",
    "reports.generate": "Generar informe",
    "reports.generating": "Generando…",
    "reports.snapshots": "Instantáneas",
    "reports.emptyReports": "Aún no hay informes. Genera uno cuando quieras un punto de control.",
    "reports.reviews": "Revisiones importadas",
    "reports.reviewsIntro":
      "Pega una revisión, auditoría o nota. Los agentes pueden listarlas y convertir hallazgos en tareas.",
    "reports.reviewTitle": "Título (opcional)",
    "reports.reviewBody": "Markdown de la revisión",
    "reports.import": "Importar revisión",
    "reports.importing": "Importando…",
    "reports.emptyReviews": "Aún no hay revisiones importadas.",
    "reports.loadFailed": "no se pudieron cargar los informes",
    "reports.generateFailed": "no se pudo generar el informe",
    "reports.importFailed": "no se pudo importar la revisión",
    "learn.title": "Aprender",
    "learn.intro":
      "Beacon es el sistema operativo alrededor de tus agentes. Las personas mantienen el brief y el tablero. Los agentes locales toman el trabajo Ready.",
    "learn.start": "Empieza aquí",
    "learn.startBody":
      "Crea o elige un proyecto en la cabecera. Escribe el brief en Contexto. Pon el trabajo en Ready. Crea un token en Agentes y ejecuta setup en la máquina del agente.",
    "learn.screens": "Para qué sirve cada pantalla",
    "learn.home":
      "Inicio es el resumen: brief, hitos, índice y el trabajo listo o en curso.",
    "learn.board":
      "El tablero es la cola viva. Arrastra una tarjeta para cambiar el estado. Ready es lo que pueden empezar los agentes locales.",
    "learn.backlog": "El backlog es el mismo trabajo en lista. Úsalo para revisar o cambiar estados rápido.",
    "learn.roadmap":
      "La hoja de ruta agrupa por hito. Las dependencias dicen qué tarea debe terminar antes de otra.",
    "learn.context":
      "Contexto es el AGENTS.md vivo. Compila un brief de sesión desde aquí o desde una tarea. El índice es opcional.",
    "learn.agents":
      "En Agentes creas un token y ves sesiones. Beacon no ejecuta un agente de código alojado.",
    "learn.decisions": "Decisiones y restricciones son las reglas que los agentes no deben inventar.",
    "learn.reports":
      "Informes es un punto de control del tablero más revisiones importadas. Los agentes pueden listarlas y crear tareas.",
    "learn.settings":
      "Ajustes guarda el id del proyecto, miembros y áreas. Las áreas son prefijos, no chips libres.",
    "learn.check": "Cómo comprobar una tarea terminada",
    "learn.checkBody":
      "Abre la tarea. Lee How to check. Sigue esos pasos en la app. Si está vacío, pide al agente que lo escriba en finish_work.",
    "learn.multi": "Más de un proyecto",
    "learn.multiBody":
      "Cada proyecto necesita su propio token. Vuelve a ejecutar beacon connect o beacon setup con ese id. El MCP local cambia con --project, BEACON_PROJECT o project_id en la herramienta.",
    "learn.guide": "Primera hora",
    "learn.guideOne": "1. Escribe Goals y Definition of Done en Contexto.",
    "learn.guideTwo": "2. Crea una tarea Ready con How to check.",
    "learn.guideThree": "3. Crea un token en Agentes y ejecuta setup.cmd o beacon setup.",
    "learn.guideFour": "4. Deja que el agente llame start_work y comprueba el cambio con How to check.",
    "settings.language": "Idioma",
    "settings.languageHint": "Elige el idioma de las etiquetas, estados vacíos y esta página.",
    "home.welcome": "Crea un proyecto para empezar el tablero. Beacon añadirá un primer hito y unas tareas.",
  },
  uk: {
    "nav.home": "Головна",
    "nav.board": "Дошка",
    "nav.backlog": "Беклог",
    "nav.roadmap": "Дорожня карта",
    "nav.context": "Контекст",
    "nav.files": "Файли",
    "nav.agents": "Агенти",
    "nav.decisions": "Рішення",
    "nav.reports": "Звіти",
    "nav.settings": "Налаштування",
    "nav.learn": "Довідка",
    "common.loading": "Завантаження…",
    "common.selectProject": "Оберіть проєкт у шапці.",
    "common.language": "Мова",
    "common.org": "Орг",
    "common.project": "Проєкт",
    "common.logout": "Вийти",
    "common.failedSession": "не вдалося завантажити сесію",
    "common.failedProjects": "не вдалося завантажити проєкти",
    "common.logoutFailed": "не вдалося вийти",
    "reports.title": "Звіти",
    "reports.intro":
      "Знімок поточної дошки або імпорт рев’ю, яке агенти можуть прочитати й перетворити на задачі.",
    "reports.generate": "Згенерувати звіт",
    "reports.generating": "Генерація…",
    "reports.snapshots": "Знімки",
    "reports.emptyReports": "Звітів ще немає. Згенеруйте контрольний знімок, коли будете готові.",
    "reports.reviews": "Імпортовані рев’ю",
    "reports.reviewsIntro":
      "Вставте рев’ю, аудит або нотатку. Агенти можуть читати їх і створювати задачі.",
    "reports.reviewTitle": "Заголовок (необов’язково)",
    "reports.reviewBody": "Markdown рев’ю",
    "reports.import": "Імпортувати рев’ю",
    "reports.importing": "Імпорт…",
    "reports.emptyReviews": "Імпортованих рев’ю ще немає.",
    "reports.loadFailed": "не вдалося завантажити звіти",
    "reports.generateFailed": "не вдалося згенерувати звіт",
    "reports.importFailed": "не вдалося імпортувати рев’ю",
    "learn.title": "Довідка",
    "learn.intro":
      "Beacon — операційна система навколо ваших агентів. Люди тримають бриф і дошку. Локальні агенти беруть Ready-задачі.",
    "learn.start": "Почніть тут",
    "learn.startBody":
      "Створіть або оберіть проєкт у шапці. Напишіть бриф у Контексті. Поставте роботу в Ready. Згенеруйте токен на Агентах і запустіть setup на машині агента.",
    "learn.screens": "Навіщо кожен екран",
    "learn.home":
      "Головна — поточний знімок: бриф, віхи, індекс і робота, що готова або в роботі.",
    "learn.board":
      "Дошка — жива черга. Перетягніть картку, щоб змінити статус. Ready — те, що можуть брати локальні агенти.",
    "learn.backlog": "Беклог — та сама робота списком. Зручно швидко переглянути або змінити статус.",
    "learn.roadmap":
      "Дорожня карта групує роботу за віхами. Залежності показують, яка задача має завершитися раніше.",
    "learn.context":
      "Контекст — живий AGENTS.md. Компілюйте сесійний бриф звідси або з задачі. Індекс коду необов’язковий.",
    "learn.agents":
      "На Агентах випускають токен проєкту і дивляться сесії. Beacon не запускає хостованого кодувального агента.",
    "learn.decisions": "Рішення і обмеження — стійкі правила, які агенти не повинні вигадувати.",
    "learn.reports":
      "Звіти — контрольний знімок дошки плюс імпортовані рев’ю. Агенти можуть читати їх і створювати задачі.",
    "learn.settings":
      "Налаштування зберігають id проєкту, учасників і області. Області — префікси шляхів, не вільні чіпи.",
    "learn.check": "Як перевірити завершену задачу",
    "learn.checkBody":
      "Відкрийте задачу. Прочитайте How to check. Виконайте ці кроки в застосунку. Якщо поле порожнє, попросіть агента записати його в finish_work.",
    "learn.multi": "Кілька проєктів",
    "learn.multiBody":
      "Кожному проєкту потрібен свій токен. Знову запустіть beacon connect або beacon setup з цим id. Локальний MCP перемикається через --project, BEACON_PROJECT або project_id у виклику інструмента.",
    "learn.guide": "Перша година",
    "learn.guideOne": "1. Напишіть Goals і Definition of Done у Контексті.",
    "learn.guideTwo": "2. Створіть Ready-задачу з How to check.",
    "learn.guideThree": "3. Згенеруйте токен на Агентах і запустіть setup.cmd або beacon setup.",
    "learn.guideFour": "4. Нехай агент викличе start_work, потім перевірте зміну за How to check.",
    "settings.language": "Мова",
    "settings.languageHint": "Оберіть мову підписів, порожніх станів і цієї сторінки.",
    "home.welcome": "Створіть проєкт, щоб почати дошку. Beacon додасть першу віху і кілька стартових задач.",
  },
  fr: {
    "nav.home": "Accueil",
    "nav.board": "Tableau",
    "nav.backlog": "Backlog",
    "nav.roadmap": "Feuille de route",
    "nav.context": "Contexte",
    "nav.files": "Fichiers",
    "nav.agents": "Agents",
    "nav.decisions": "Décisions",
    "nav.reports": "Rapports",
    "nav.settings": "Paramètres",
    "nav.learn": "Découvrir",
    "common.loading": "Chargement…",
    "common.selectProject": "Choisissez un projet dans l’en-tête.",
    "common.language": "Langue",
    "common.org": "Org",
    "common.project": "Projet",
    "common.logout": "Se déconnecter",
    "common.failedSession": "échec du chargement de la session",
    "common.failedProjects": "échec du chargement des projets",
    "common.logoutFailed": "échec de la déconnexion",
    "reports.title": "Rapports",
    "reports.intro":
      "Générez un instantané du tableau actuel, ou importez une revue pour que les agents la lisent et créent des tâches de suivi.",
    "reports.generate": "Générer un rapport",
    "reports.generating": "Génération…",
    "reports.snapshots": "Instantanés",
    "reports.emptyReports":
      "Aucun rapport pour l’instant. Générez-en un quand vous voulez un point de contrôle lisible par les agents.",
    "reports.reviews": "Revues importées",
    "reports.reviewsIntro":
      "Collez une revue, un audit ou une note. Les agents peuvent les lister et transformer les constats en tâches.",
    "reports.reviewTitle": "Titre (facultatif)",
    "reports.reviewBody": "Markdown de la revue",
    "reports.import": "Importer la revue",
    "reports.importing": "Import…",
    "reports.emptyReviews": "Aucune revue importée pour l’instant.",
    "reports.loadFailed": "échec du chargement des rapports",
    "reports.generateFailed": "échec de la génération du rapport",
    "reports.importFailed": "échec de l’import de la revue",
    "learn.title": "Découvrir",
    "learn.intro":
      "Beacon est le système d’exploitation autour de vos agents. Les humains tiennent le brief et le tableau. Les agents locaux prennent le travail Ready.",
    "learn.start": "Commencer ici",
    "learn.startBody":
      "Créez ou choisissez un projet dans l’en-tête. Rédigez le brief vivant dans Contexte. Mettez le travail Ready sur le Tableau. Créez un jeton de projet dans Agents, puis lancez setup sur la machine qui héberge l’agent.",
    "learn.screens": "À quoi sert chaque écran",
    "learn.home":
      "Accueil est l’instantané actuel : brief, jalons, état de l’index, et travail prêt ou en cours.",
    "learn.board":
      "Le tableau est la file vivante. Faites glisser une carte pour changer le statut. Ready est ce que les agents locaux peuvent commencer.",
    "learn.backlog":
      "Le backlog est le même travail en liste. Utilisez-le pour parcourir ou changer les statuts rapidement.",
    "learn.roadmap":
      "La feuille de route groupe le travail par jalon. Les dépendances indiquent quelle tâche doit finir avant qu’une autre commence.",
    "learn.context":
      "Contexte est l’AGENTS.md vivant. Compilez un brief de session ici ou depuis une tâche. L’index de code est facultatif.",
    "learn.agents":
      "Dans Agents, vous créez un jeton de projet et suivez les sessions. Beacon n’exécute pas d’agent de code hébergé.",
    "learn.decisions":
      "Les décisions et contraintes sont les règles durables que les agents ne doivent pas contourner.",
    "learn.reports":
      "Rapports est un point de contrôle du tableau plus les revues importées. Les agents peuvent les lister et en faire des tâches.",
    "learn.settings":
      "Paramètres contient l’id du projet, les membres et les libellés de zone. Les zones sont des préfixes, pas des puces libres.",
    "learn.check": "Comment vérifier une tâche terminée",
    "learn.checkBody":
      "Ouvrez la tâche. Lisez How to check. Suivez ces étapes dans l’app. Si les notes sont vides, demandez à l’agent de les écrire dans finish_work.",
    "learn.multi": "Plus d’un projet",
    "learn.multiBody":
      "Chaque projet Beacon a besoin de son propre jeton. Relancez beacon connect ou beacon setup avec cet id. Le MCP local bascule avec --project, BEACON_PROJECT, ou project_id sur un appel d’outil.",
    "learn.guide": "Première heure",
    "learn.guideOne": "1. Écrivez Goals et Definition of Done dans Contexte.",
    "learn.guideTwo": "2. Créez une tâche Ready avec How to check rempli.",
    "learn.guideThree": "3. Créez un jeton dans Agents et lancez setup.cmd ou beacon setup.",
    "learn.guideFour": "4. Laissez l’agent appeler start_work, puis confirmez le changement depuis How to check.",
    "settings.language": "Langue",
    "settings.languageHint": "Choisissez la langue des libellés, des états vides et de cette page Découvrir.",
    "home.welcome":
      "Créez un projet pour démarrer le tableau. Beacon ajoutera un premier jalon et quelques tâches de départ.",
  },
  de: {
    "nav.home": "Start",
    "nav.board": "Board",
    "nav.backlog": "Backlog",
    "nav.roadmap": "Roadmap",
    "nav.context": "Kontext",
    "nav.files": "Dateien",
    "nav.agents": "Agenten",
    "nav.decisions": "Entscheidungen",
    "nav.reports": "Berichte",
    "nav.settings": "Einstellungen",
    "nav.learn": "Lernen",
    "common.loading": "Laden…",
    "common.selectProject": "Wählen Sie ein Projekt in der Kopfzeile.",
    "common.language": "Sprache",
    "common.org": "Org",
    "common.project": "Projekt",
    "common.logout": "Abmelden",
    "common.failedSession": "Sitzung konnte nicht geladen werden",
    "common.failedProjects": "Projekte konnten nicht geladen werden",
    "common.logoutFailed": "Abmelden fehlgeschlagen",
    "reports.title": "Berichte",
    "reports.intro":
      "Erzeugen Sie einen Snapshot des aktuellen Boards oder importieren Sie eine Review, damit Agenten sie lesen und Folgeaufgaben anlegen können.",
    "reports.generate": "Bericht erzeugen",
    "reports.generating": "Wird erzeugt…",
    "reports.snapshots": "Snapshots",
    "reports.emptyReports":
      "Noch keine Berichte. Erzeugen Sie einen, wenn Sie einen Prüfpunkt für Agenten brauchen.",
    "reports.reviews": "Importierte Reviews",
    "reports.reviewsIntro":
      "Fügen Sie eine Review, ein Audit oder eine Notiz ein. Agenten können sie listen und Erkenntnisse in Aufgaben verwandeln.",
    "reports.reviewTitle": "Titel (optional)",
    "reports.reviewBody": "Review-Markdown",
    "reports.import": "Review importieren",
    "reports.importing": "Import…",
    "reports.emptyReviews": "Noch keine Reviews importiert.",
    "reports.loadFailed": "Berichte konnten nicht geladen werden",
    "reports.generateFailed": "Bericht konnte nicht erzeugt werden",
    "reports.importFailed": "Review konnte nicht importiert werden",
    "learn.title": "Lernen",
    "learn.intro":
      "Beacon ist das Betriebssystem um Ihre Agenten. Menschen halten Brief und Board. Lokale Agenten übernehmen Ready-Arbeit.",
    "learn.start": "Hier starten",
    "learn.startBody":
      "Erstellen oder wählen Sie ein Projekt in der Kopfzeile. Schreiben Sie den lebenden Brief in Kontext. Setzen Sie Arbeit auf dem Board auf Ready. Erzeugen Sie auf Agenten ein Projekttoken und führen Sie Setup auf dem Rechner des Agenten aus.",
    "learn.screens": "Wofür jeder Bildschirm da ist",
    "learn.home":
      "Start ist der aktuelle Snapshot: Brief, Meilensteine, Indexstatus und Arbeit, die bereit oder in Bearbeitung ist.",
    "learn.board":
      "Das Board ist die lebendige Warteschlange. Ziehen Sie eine Karte, um den Status zu ändern. Ready ist, was lokale Agenten starten können.",
    "learn.backlog":
      "Das Backlog ist dieselbe Arbeit als Liste. Nutzen Sie es zum schnellen Durchsehen oder Umstufen.",
    "learn.roadmap":
      "Die Roadmap gruppiert Arbeit nach Meilenstein. Abhängigkeiten zeigen, welche Aufgabe zuerst fertig sein muss.",
    "learn.context":
      "Kontext ist das lebende AGENTS.md. Kompilieren Sie hier oder von einer Aufgabe einen Session-Brief. Der Code-Index ist optional.",
    "learn.agents":
      "Unter Agenten erzeugen Sie ein Projekttoken und sehen Sitzungen. Beacon führt keinen gehosteten Coding-Agenten aus.",
    "learn.decisions":
      "Entscheidungen und Einschränkungen sind die dauerhaften Regeln, um die Agenten nicht herumerfinden sollen.",
    "learn.reports":
      "Berichte sind ein Prüfpunkt des Boards plus importierte Reviews. Agenten können sie listen und Erkenntnisse in Aufgaben verwandeln.",
    "learn.settings":
      "Einstellungen enthalten Projekt-ID, Mitglieder und Bereichslabels. Bereiche sind Präfixe, keine freien Chips.",
    "learn.check": "Wie man eine fertige Aufgabe prüft",
    "learn.checkBody":
      "Öffnen Sie die Aufgabe. Lesen Sie How to check. Folgen Sie diesen Schritten in der App. Wenn die Notizen leer sind, bitten Sie den Agenten, sie bei finish_work zu schreiben.",
    "learn.multi": "Mehr als ein Projekt",
    "learn.multiBody":
      "Jedes Beacon-Projekt braucht ein eigenes Token. Führen Sie beacon connect oder beacon setup erneut mit dieser Projekt-ID aus. Lokales MCP wechselt mit --project, BEACON_PROJECT oder project_id beim Tool-Aufruf.",
    "learn.guide": "Erste Stunde",
    "learn.guideOne": "1. Schreiben Sie Goals und Definition of Done in Kontext.",
    "learn.guideTwo": "2. Legen Sie eine Ready-Aufgabe mit ausgefülltem How to check an.",
    "learn.guideThree": "3. Erzeugen Sie auf Agenten ein Token und führen Sie setup.cmd oder beacon setup aus.",
    "learn.guideFour": "4. Lassen Sie den Agenten start_work aufrufen und bestätigen Sie die Änderung über How to check.",
    "settings.language": "Sprache",
    "settings.languageHint": "Wählen Sie die Sprache für Beschriftungen, leere Zustände und diese Lernseite.",
    "home.welcome":
      "Erstellen Sie ein Projekt, um das Board zu starten. Beacon fügt einen ersten Meilenstein und ein paar Startaufgaben hinzu.",
  },
  pt: {
    "nav.home": "Início",
    "nav.board": "Quadro",
    "nav.backlog": "Backlog",
    "nav.roadmap": "Roteiro",
    "nav.context": "Contexto",
    "nav.files": "Arquivos",
    "nav.agents": "Agentes",
    "nav.decisions": "Decisões",
    "nav.reports": "Relatórios",
    "nav.settings": "Definições",
    "nav.learn": "Aprender",
    "common.loading": "A carregar…",
    "common.selectProject": "Selecione um projeto no cabeçalho.",
    "common.language": "Idioma",
    "common.org": "Org",
    "common.project": "Projeto",
    "common.logout": "Sair",
    "common.failedSession": "falha ao carregar a sessão",
    "common.failedProjects": "falha ao carregar os projetos",
    "common.logoutFailed": "falha ao sair",
    "reports.title": "Relatórios",
    "reports.intro":
      "Gere um instantâneo do quadro atual ou importe uma revisão para os agentes lerem e criarem tarefas de seguimento.",
    "reports.generate": "Gerar relatório",
    "reports.generating": "A gerar…",
    "reports.snapshots": "Instantâneos",
    "reports.emptyReports":
      "Ainda não há relatórios. Gere um quando quiser um ponto de controlo que os agentes possam ler.",
    "reports.reviews": "Revisões importadas",
    "reports.reviewsIntro":
      "Cole uma revisão, auditoria ou nota. Os agentes podem listá-las e transformar achados em tarefas.",
    "reports.reviewTitle": "Título (opcional)",
    "reports.reviewBody": "Markdown da revisão",
    "reports.import": "Importar revisão",
    "reports.importing": "A importar…",
    "reports.emptyReviews": "Ainda não há revisões importadas.",
    "reports.loadFailed": "falha ao carregar os relatórios",
    "reports.generateFailed": "falha ao gerar o relatório",
    "reports.importFailed": "falha ao importar a revisão",
    "learn.title": "Aprender",
    "learn.intro":
      "Beacon é o sistema operativo à volta dos seus agentes. As pessoas mantêm o brief e o quadro. Os agentes locais pegam no trabalho Ready.",
    "learn.start": "Comece aqui",
    "learn.startBody":
      "Crie ou escolha um projeto no cabeçalho. Escreva o brief vivo em Contexto. Ponha o trabalho Ready no Quadro. Crie um token de projeto em Agentes e execute o setup na máquina do agente.",
    "learn.screens": "Para que serve cada ecrã",
    "learn.home":
      "Início é o instantâneo atual: brief, marcos, estado do índice e trabalho pronto ou em curso.",
    "learn.board":
      "O quadro é a fila viva. Arraste um cartão para mudar o estado. Ready é o que os agentes locais podem começar.",
    "learn.backlog":
      "O backlog é o mesmo trabalho em lista. Use-o para rever ou mudar estados depressa.",
    "learn.roadmap":
      "O roteiro agrupa o trabalho por marco. As dependências mostram que tarefa tem de terminar antes de outra começar.",
    "learn.context":
      "Contexto é o AGENTS.md vivo. Compile um brief de sessão daqui ou de uma tarefa. O índice de código é opcional.",
    "learn.agents":
      "Em Agentes cria um token de projeto e vê sessões. O Beacon não executa um agente de código alojado.",
    "learn.decisions":
      "Decisões e restrições são as regras duradouras que os agentes não devem contornar.",
    "learn.reports":
      "Relatórios é um ponto de controlo do quadro mais revisões importadas. Os agentes podem listá-las e criar tarefas.",
    "learn.settings":
      "Definições guarda o id do projeto, membros e etiquetas de área. As áreas são prefixos, não chips livres.",
    "learn.check": "Como verificar uma tarefa concluída",
    "learn.checkBody":
      "Abra a tarefa. Leia How to check. Siga esses passos na app. Se as notas estiverem vazias, peça ao agente que as escreva em finish_work.",
    "learn.multi": "Mais do que um projeto",
    "learn.multiBody":
      "Cada projeto Beacon precisa do seu próprio token. Volte a executar beacon connect ou beacon setup com esse id. O MCP local muda com --project, BEACON_PROJECT ou project_id numa chamada de ferramenta.",
    "learn.guide": "Primeira hora",
    "learn.guideOne": "1. Escreva Goals e Definition of Done em Contexto.",
    "learn.guideTwo": "2. Crie uma tarefa Ready com How to check preenchido.",
    "learn.guideThree": "3. Crie um token em Agentes e execute setup.cmd ou beacon setup.",
    "learn.guideFour": "4. Deixe o agente chamar start_work e confirme a alteração em How to check.",
    "settings.language": "Idioma",
    "settings.languageHint": "Escolha o idioma das etiquetas, estados vazios e desta página Aprender.",
    "home.welcome":
      "Crie um projeto para começar o quadro. O Beacon adicionará um primeiro marco e algumas tarefas iniciais.",
  },
  pl: {
    "nav.home": "Start",
    "nav.board": "Tablica",
    "nav.backlog": "Backlog",
    "nav.roadmap": "Mapa drogowa",
    "nav.context": "Kontekst",
    "nav.files": "Pliki",
    "nav.agents": "Agenci",
    "nav.decisions": "Decyzje",
    "nav.reports": "Raporty",
    "nav.settings": "Ustawienia",
    "nav.learn": "Poznaj",
    "common.loading": "Ładowanie…",
    "common.selectProject": "Wybierz projekt w nagłówku.",
    "common.language": "Język",
    "common.org": "Org",
    "common.project": "Projekt",
    "common.logout": "Wyloguj",
    "common.failedSession": "nie udało się wczytać sesji",
    "common.failedProjects": "nie udało się wczytać projektów",
    "common.logoutFailed": "nie udało się wylogować",
    "reports.title": "Raporty",
    "reports.intro":
      "Wygeneruj migawkę bieżącej tablicy albo zaimportuj recenzję, żeby agenci mogli ją przeczytać i utworzyć zadania.",
    "reports.generate": "Wygeneruj raport",
    "reports.generating": "Generowanie…",
    "reports.snapshots": "Migawki",
    "reports.emptyReports":
      "Nie ma jeszcze raportów. Wygeneruj jeden, gdy chcesz punkt kontrolny czytelny dla agentów.",
    "reports.reviews": "Zaimportowane recenzje",
    "reports.reviewsIntro":
      "Wklej recenzję, audyt lub notatkę. Agenci mogą je listować i zamieniać ustalenia na zadania.",
    "reports.reviewTitle": "Tytuł (opcjonalnie)",
    "reports.reviewBody": "Markdown recenzji",
    "reports.import": "Importuj recenzję",
    "reports.importing": "Import…",
    "reports.emptyReviews": "Nie zaimportowano jeszcze recenzji.",
    "reports.loadFailed": "nie udało się wczytać raportów",
    "reports.generateFailed": "nie udało się wygenerować raportu",
    "reports.importFailed": "nie udało się zaimportować recenzji",
    "learn.title": "Poznaj",
    "learn.intro":
      "Beacon to system operacyjny wokół Twoich agentów. Ludzie utrzymują brief i tablicę. Lokalni agenci biorą pracę Ready.",
    "learn.start": "Zacznij tutaj",
    "learn.startBody":
      "Utwórz lub wybierz projekt w nagłówku. Napisz żywy brief w Kontekście. Ustaw pracę na Tablicy jako Ready. Wystaw token projektu w Agentach i uruchom setup na maszynie agenta.",
    "learn.screens": "Do czego służy każdy ekran",
    "learn.home":
      "Start to bieżąca migawka: brief, kamienie milowe, stan indeksu oraz praca gotowa lub w toku.",
    "learn.board":
      "Tablica to żywa kolejka. Przeciągnij kartę, aby zmienić status. Ready to to, co mogą zacząć lokalni agenci.",
    "learn.backlog":
      "Backlog to ta sama praca na liście. Używaj go do szybkiego przeglądu lub zmiany statusu.",
    "learn.roadmap":
      "Mapa drogowa grupuje pracę według kamieni milowych. Zależności pokazują, które zadanie musi skończyć się wcześniej.",
    "learn.context":
      "Kontekst to żywy AGENTS.md. Skompiluj brief sesji stąd lub z zadania. Indeks kodu jest opcjonalny.",
    "learn.agents":
      "W Agentach wystawiasz token projektu i oglądasz sesje. Beacon nie uruchamia hostowanego agenta kodującego.",
    "learn.decisions":
      "Decyzje i ograniczenia to trwałe reguły, których agenci nie powinni obchodzić.",
    "learn.reports":
      "Raporty to punkt kontrolny tablicy plus zaimportowane recenzje. Agenci mogą je listować i tworzyć zadania.",
    "learn.settings":
      "Ustawienia trzymają id projektu, członków i etykiety obszarów. Obszary to prefiksy, nie swobodne chipy.",
    "learn.check": "Jak sprawdzić ukończone zadanie",
    "learn.checkBody":
      "Otwórz zadanie. Przeczytaj How to check. Wykonaj te kroki w aplikacji. Jeśli notatki są puste, poproś agenta, żeby zapisał je w finish_work.",
    "learn.multi": "Więcej niż jeden projekt",
    "learn.multiBody":
      "Każdy projekt Beacon potrzebuje własnego tokenu. Uruchom ponownie beacon connect lub beacon setup z tym id. Lokalne MCP przełącza się przez --project, BEACON_PROJECT lub project_id przy wywołaniu narzędzia.",
    "learn.guide": "Pierwsza godzina",
    "learn.guideOne": "1. Napisz Goals i Definition of Done w Kontekście.",
    "learn.guideTwo": "2. Utwórz zadanie Ready z wypełnionym How to check.",
    "learn.guideThree": "3. Wystaw token w Agentach i uruchom setup.cmd albo beacon setup.",
    "learn.guideFour": "4. Niech agent wywoła start_work, potem potwierdź zmianę według How to check.",
    "settings.language": "Język",
    "settings.languageHint": "Wybierz język etykiet, pustych stanów i tej strony Poznaj.",
    "home.welcome":
      "Utwórz projekt, aby zacząć tablicę. Beacon doda pierwszy kamień milowy i kilka zadań startowych.",
  },
  it: {
    "nav.home": "Home",
    "nav.board": "Bacheca",
    "nav.backlog": "Backlog",
    "nav.roadmap": "Roadmap",
    "nav.context": "Contesto",
    "nav.files": "File",
    "nav.agents": "Agenti",
    "nav.decisions": "Decisioni",
    "nav.reports": "Report",
    "nav.settings": "Impostazioni",
    "nav.learn": "Scopri",
    "common.loading": "Caricamento…",
    "common.selectProject": "Seleziona un progetto dall’intestazione.",
    "common.language": "Lingua",
    "common.org": "Org",
    "common.project": "Progetto",
    "common.logout": "Esci",
    "common.failedSession": "impossibile caricare la sessione",
    "common.failedProjects": "impossibile caricare i progetti",
    "common.logoutFailed": "disconnessione non riuscita",
    "reports.title": "Report",
    "reports.intro":
      "Genera uno snapshot della bacheca attuale oppure importa una review così gli agenti possono leggerla e creare attività di follow-up.",
    "reports.generate": "Genera report",
    "reports.generating": "Generazione…",
    "reports.snapshots": "Snapshot",
    "reports.emptyReports":
      "Nessun report ancora. Generane uno quando vuoi un checkpoint che gli agenti possano leggere.",
    "reports.reviews": "Review importate",
    "reports.reviewsIntro":
      "Incolla una review, un audit o una nota. Gli agenti possono elencarli e trasformare i rilievi in attività.",
    "reports.reviewTitle": "Titolo (facoltativo)",
    "reports.reviewBody": "Markdown della review",
    "reports.import": "Importa review",
    "reports.importing": "Importazione…",
    "reports.emptyReviews": "Nessuna review importata ancora.",
    "reports.loadFailed": "impossibile caricare i report",
    "reports.generateFailed": "impossibile generare il report",
    "reports.importFailed": "impossibile importare la review",
    "learn.title": "Scopri",
    "learn.intro":
      "Beacon è il sistema operativo intorno ai tuoi agenti. Le persone tengono il brief e la bacheca. Gli agenti locali prendono il lavoro Ready.",
    "learn.start": "Inizia qui",
    "learn.startBody":
      "Crea o scegli un progetto nell’intestazione. Scrivi il brief vivo in Contesto. Metti il lavoro Ready sulla Bacheca. Crea un token di progetto in Agenti, poi esegui setup sulla macchina dell’agente.",
    "learn.screens": "A cosa serve ogni schermata",
    "learn.home":
      "Home è lo snapshot attuale: brief, milestone, stato dell’indice e lavoro pronto o in corso.",
    "learn.board":
      "La bacheca è la coda viva. Trascina una scheda per cambiare stato. Ready è ciò che gli agenti locali possono iniziare.",
    "learn.backlog":
      "Il backlog è lo stesso lavoro in elenco. Usalo per scorrere o cambiare stato in fretta.",
    "learn.roadmap":
      "La roadmap raggruppa il lavoro per milestone. Le dipendenze indicano quale attività deve finire prima che un’altra inizi.",
    "learn.context":
      "Contesto è l’AGENTS.md vivo. Compila un brief di sessione da qui o da un’attività. L’indice del codice è facoltativo.",
    "learn.agents":
      "In Agenti crei un token di progetto e osservi le sessioni. Beacon non esegue un agente di codice ospitato.",
    "learn.decisions":
      "Decisioni e vincoli sono le regole durature che gli agenti non devono aggirare.",
    "learn.reports":
      "Report è un checkpoint della bacheca più le review importate. Gli agenti possono elencarli e creare attività.",
    "learn.settings":
      "Impostazioni contiene l’id del progetto, i membri e le etichette di area. Le aree sono prefissi, non chip liberi.",
    "learn.check": "Come verificare un’attività finita",
    "learn.checkBody":
      "Apri l’attività. Leggi How to check. Segui quei passi nell’app. Se le note sono vuote, chiedi all’agente di scriverle in finish_work.",
    "learn.multi": "Più di un progetto",
    "learn.multiBody":
      "Ogni progetto Beacon ha bisogno del proprio token. Esegui di nuovo beacon connect o beacon setup con quell’id. L’MCP locale cambia con --project, BEACON_PROJECT o project_id su una chiamata strumento.",
    "learn.guide": "Prima ora",
    "learn.guideOne": "1. Scrivi Goals e Definition of Done in Contesto.",
    "learn.guideTwo": "2. Crea un’attività Ready con How to check compilato.",
    "learn.guideThree": "3. Crea un token in Agenti ed esegui setup.cmd o beacon setup.",
    "learn.guideFour": "4. Lascia che l’agente chiami start_work, poi conferma il cambiamento da How to check.",
    "settings.language": "Lingua",
    "settings.languageHint": "Scegli la lingua di etichette, stati vuoti e di questa pagina Scopri.",
    "home.welcome":
      "Crea un progetto per avviare la bacheca. Beacon aggiungerà una prima milestone e alcune attività iniziali.",
  },
  ja: {
    "nav.home": "ホーム",
    "nav.board": "ボード",
    "nav.backlog": "バックログ",
    "nav.roadmap": "ロードマップ",
    "nav.context": "コンテキスト",
    "nav.files": "ファイル",
    "nav.agents": "エージェント",
    "nav.decisions": "決定",
    "nav.reports": "レポート",
    "nav.settings": "設定",
    "nav.learn": "学ぶ",
    "common.loading": "読み込み中…",
    "common.selectProject": "ヘッダーからプロジェクトを選んでください。",
    "common.language": "言語",
    "common.org": "組織",
    "common.project": "プロジェクト",
    "common.logout": "ログアウト",
    "common.failedSession": "セッションを読み込めませんでした",
    "common.failedProjects": "プロジェクトを読み込めませんでした",
    "common.logoutFailed": "ログアウトに失敗しました",
    "reports.title": "レポート",
    "reports.intro":
      "現在のボードのスナップショットを生成するか、レビューを取り込んでエージェントが読み、フォローアップタスクを作れるようにします。",
    "reports.generate": "レポートを生成",
    "reports.generating": "生成中…",
    "reports.snapshots": "スナップショット",
    "reports.emptyReports":
      "まだレポートはありません。エージェントが読めるチェックポイントが必要なときに生成してください。",
    "reports.reviews": "取り込んだレビュー",
    "reports.reviewsIntro":
      "レビュー、監査、メモを貼り付けます。エージェントは一覧し、指摘をタスクにできます。",
    "reports.reviewTitle": "タイトル（任意）",
    "reports.reviewBody": "レビューの Markdown",
    "reports.import": "レビューを取り込む",
    "reports.importing": "取り込み中…",
    "reports.emptyReviews": "まだ取り込んだレビューはありません。",
    "reports.loadFailed": "レポートを読み込めませんでした",
    "reports.generateFailed": "レポートを生成できませんでした",
    "reports.importFailed": "レビューを取り込めませんでした",
    "learn.title": "学ぶ",
    "learn.intro":
      "Beacon はエージェントの周りのオペレーティングシステムです。人がブリーフとボードを保ち、ローカルエージェントが Ready の仕事を取ります。",
    "learn.start": "ここから始める",
    "learn.startBody":
      "ヘッダーでプロジェクトを作成または選択します。コンテキストに生きたブリーフを書きます。ボードで仕事を Ready にします。エージェントでプロジェクトトークンを発行し、エージェントがあるマシンで setup を実行します。",
    "learn.screens": "各画面の役割",
    "learn.home":
      "ホームは現在のスナップショットです。ブリーフ、マイルストーン、インデックス状態、Ready または進行中の仕事。",
    "learn.board":
      "ボードは生きたキューです。カードをドラッグして状態を変えます。Ready はローカルエージェントが開始できる仕事です。",
    "learn.backlog":
      "バックログは同じ仕事のリストです。素早く眺めたり状態を変えたりするときに使います。",
    "learn.roadmap":
      "ロードマップはマイルストーンごとに仕事をまとめます。依存関係は、どのタスクが先に終わるべきかを示します。",
    "learn.context":
      "コンテキストは生きた AGENTS.md です。ここまたはタスクからセッションブリーフをコンパイルします。コードインデックスは任意です。",
    "learn.agents":
      "エージェントではプロジェクトトークンを発行し、セッションを見ます。Beacon はホスト型コーディングエージェントを実行しません。",
    "learn.decisions":
      "決定と制約は、エージェントが勝手に迂回してはいけない持続的なルールです。",
    "learn.reports":
      "レポートはボードのチェックポイントと取り込んだレビューです。エージェントは一覧し、指摘をタスクにできます。",
    "learn.settings":
      "設定にはプロジェクト ID、メンバー、エリアラベルがあります。エリアは接頭辞であり、自由なチップではありません。",
    "learn.check": "完了したタスクの確認方法",
    "learn.checkBody":
      "タスクを開きます。How to check を読み、アプリでその手順を実行します。メモが空なら、エージェントに finish_work で書いてもらってください。",
    "learn.multi": "複数のプロジェクト",
    "learn.multiBody":
      "Beacon の各プロジェクトには独自のトークンが必要です。そのプロジェクト ID で beacon connect または beacon setup を再実行します。ローカル MCP は --project、BEACON_PROJECT、またはツール呼び出しの project_id で切り替えます。",
    "learn.guide": "最初の1時間",
    "learn.guideOne": "1. コンテキストに Goals と Definition of Done を書く。",
    "learn.guideTwo": "2. How to check を埋めた Ready タスクを作る。",
    "learn.guideThree": "3. エージェントでトークンを発行し、setup.cmd または beacon setup を実行する。",
    "learn.guideFour": "4. エージェントに start_work を呼ばせ、How to check で変更を確認する。",
    "settings.language": "言語",
    "settings.languageHint": "ラベル、空の状態、この学ぶページの言語を選びます。",
    "home.welcome":
      "プロジェクトを作成してボードを始めます。Beacon が最初のマイルストーンといくつかの開始タスクを追加します。",
  },
  zh: {
    "nav.home": "首页",
    "nav.board": "看板",
    "nav.backlog": "待办",
    "nav.roadmap": "路线图",
    "nav.context": "上下文",
    "nav.files": "文件",
    "nav.agents": "智能体",
    "nav.decisions": "决策",
    "nav.reports": "报告",
    "nav.settings": "设置",
    "nav.learn": "了解",
    "common.loading": "加载中…",
    "common.selectProject": "请在页眉中选择一个项目。",
    "common.language": "语言",
    "common.org": "组织",
    "common.project": "项目",
    "common.logout": "退出登录",
    "common.failedSession": "无法加载会话",
    "common.failedProjects": "无法加载项目",
    "common.logoutFailed": "退出登录失败",
    "reports.title": "报告",
    "reports.intro":
      "生成当前看板的快照，或导入评审，让智能体阅读并创建跟进任务。",
    "reports.generate": "生成报告",
    "reports.generating": "正在生成…",
    "reports.snapshots": "快照",
    "reports.emptyReports": "还没有报告。需要智能体可读的检查点时再生成。",
    "reports.reviews": "已导入的评审",
    "reports.reviewsIntro":
      "粘贴评审、审计或备注。智能体可以列出它们，并把发现变成任务。",
    "reports.reviewTitle": "标题（可选）",
    "reports.reviewBody": "评审 Markdown",
    "reports.import": "导入评审",
    "reports.importing": "正在导入…",
    "reports.emptyReviews": "还没有导入评审。",
    "reports.loadFailed": "无法加载报告",
    "reports.generateFailed": "无法生成报告",
    "reports.importFailed": "无法导入评审",
    "learn.title": "了解",
    "learn.intro":
      "Beacon 是围绕智能体的操作系统。人维护简报和看板。本地智能体领取 Ready 工作。",
    "learn.start": "从这里开始",
    "learn.startBody":
      "在页眉创建或选择项目。在上下文中写活的简报。把工作放到看板上并设为 Ready。在智能体页签发项目令牌，然后在托管智能体的机器上运行 setup。",
    "learn.screens": "每个页面的用途",
    "learn.home":
      "首页是当前快照：简报、里程碑、索引状态，以及就绪或进行中的工作。",
    "learn.board":
      "看板是活的队列。拖动卡片即可改状态。Ready 是本地智能体可以开始的工作。",
    "learn.backlog": "待办是同一批工作的列表。适合快速浏览或改状态。",
    "learn.roadmap":
      "路线图按里程碑分组。依赖关系显示哪项任务必须先完成。",
    "learn.context":
      "上下文是活的 AGENTS.md。可从这里或从任务编译会话简报。代码索引是可选的。",
    "learn.agents":
      "在智能体页签发项目令牌并查看会话。Beacon 不运行托管的编码智能体。",
    "learn.decisions": "决策和约束是智能体不应绕开的持久规则。",
    "learn.reports":
      "报告是看板检查点加上导入的评审。智能体可以列出它们并把发现变成任务。",
    "learn.settings":
      "设置保存项目 ID、成员和区域标签。区域是路径前缀，不是自由标签。",
    "learn.check": "如何检查已完成的任务",
    "learn.checkBody":
      "打开任务。阅读 How to check。在应用中按这些步骤检查。如果备注为空，请让智能体在 finish_work 时写上。",
    "learn.multi": "多个项目",
    "learn.multiBody":
      "每个 Beacon 项目都需要自己的令牌。用该项目 ID 再次运行 beacon connect 或 beacon setup。本地 MCP 可通过 --project、BEACON_PROJECT 或工具调用上的 project_id 切换。",
    "learn.guide": "第一个小时",
    "learn.guideOne": "1. 在上下文中写 Goals 和 Definition of Done。",
    "learn.guideTwo": "2. 创建一个填好 How to check 的 Ready 任务。",
    "learn.guideThree": "3. 在智能体页签发令牌，并运行 setup.cmd 或 beacon setup。",
    "learn.guideFour": "4. 让智能体调用 start_work，然后按 How to check 确认改动。",
    "settings.language": "语言",
    "settings.languageHint": "选择标签、空状态和本了解页的语言。",
    "home.welcome": "创建一个项目以开始看板。Beacon 会添加第一个里程碑和若干起始任务。",
  },
  ru: {
    "nav.home": "Главная",
    "nav.board": "Доска",
    "nav.backlog": "Бэклог",
    "nav.roadmap": "Дорожная карта",
    "nav.context": "Контекст",
    "nav.files": "Файлы",
    "nav.agents": "Агенты",
    "nav.decisions": "Решения",
    "nav.reports": "Отчёты",
    "nav.settings": "Настройки",
    "nav.learn": "Справка",
    "common.loading": "Загрузка…",
    "common.selectProject": "Выберите проект в шапке.",
    "common.language": "Язык",
    "common.org": "Орг",
    "common.project": "Проект",
    "common.logout": "Выйти",
    "common.failedSession": "не удалось загрузить сессию",
    "common.failedProjects": "не удалось загрузить проекты",
    "common.logoutFailed": "не удалось выйти",
    "reports.title": "Отчёты",
    "reports.intro":
      "Сделайте снимок текущей доски или импортируйте ревью, чтобы агенты прочитали его и создали задачи.",
    "reports.generate": "Сгенерировать отчёт",
    "reports.generating": "Генерация…",
    "reports.snapshots": "Снимки",
    "reports.emptyReports":
      "Отчётов ещё нет. Сгенерируйте контрольную точку, когда агентам нужно что-то прочитать.",
    "reports.reviews": "Импортированные ревью",
    "reports.reviewsIntro":
      "Вставьте ревью, аудит или заметку. Агенты могут читать их и превращать находки в задачи.",
    "reports.reviewTitle": "Заголовок (необязательно)",
    "reports.reviewBody": "Markdown ревью",
    "reports.import": "Импортировать ревью",
    "reports.importing": "Импорт…",
    "reports.emptyReviews": "Импортированных ревью ещё нет.",
    "reports.loadFailed": "не удалось загрузить отчёты",
    "reports.generateFailed": "не удалось сгенерировать отчёт",
    "reports.importFailed": "не удалось импортировать ревью",
    "learn.title": "Справка",
    "learn.intro":
      "Beacon — операционная система вокруг ваших агентов. Люди ведут бриф и доску. Локальные агенты берут Ready-работу.",
    "learn.start": "Начните здесь",
    "learn.startBody":
      "Создайте или выберите проект в шапке. Напишите живой бриф в Контексте. Поставьте работу Ready на Доске. Выпустите токен проекта в Агентах и запустите setup на машине агента.",
    "learn.screens": "Для чего каждый экран",
    "learn.home":
      "Главная — текущий снимок: бриф, вехи, состояние индекса и работа, которая готова или уже идёт.",
    "learn.board":
      "Доска — живая очередь. Перетащите карточку, чтобы сменить статус. Ready — то, что могут начать локальные агенты.",
    "learn.backlog":
      "Бэклог — та же работа списком. Удобно быстро просмотреть или сменить статус.",
    "learn.roadmap":
      "Дорожная карта группирует работу по вехам. Зависимости показывают, какая задача должна закончиться раньше.",
    "learn.context":
      "Контекст — живой AGENTS.md. Соберите сессионный бриф отсюда или из задачи. Индекс кода необязателен.",
    "learn.agents":
      "В Агентах выпускают токен проекта и смотрят сессии. Beacon не запускает размещённого агента кодирования.",
    "learn.decisions":
      "Решения и ограничения — устойчивые правила, которые агенты не должны обходить.",
    "learn.reports":
      "Отчёты — контрольная точка доски плюс импортированные ревью. Агенты могут читать их и создавать задачи.",
    "learn.settings":
      "Настройки хранят id проекта, участников и метки областей. Области — префиксы путей, не свободные чипы.",
    "learn.check": "Как проверить завершённую задачу",
    "learn.checkBody":
      "Откройте задачу. Прочитайте How to check. Выполните эти шаги в приложении. Если заметки пусты, попросите агента записать их в finish_work.",
    "learn.multi": "Несколько проектов",
    "learn.multiBody":
      "Каждому проекту Beacon нужен свой токен. Снова запустите beacon connect или beacon setup с этим id. Локальный MCP переключается через --project, BEACON_PROJECT или project_id в вызове инструмента.",
    "learn.guide": "Первый час",
    "learn.guideOne": "1. Напишите Goals и Definition of Done в Контексте.",
    "learn.guideTwo": "2. Создайте Ready-задачу с заполненным How to check.",
    "learn.guideThree": "3. Выпустите токен в Агентах и запустите setup.cmd или beacon setup.",
    "learn.guideFour": "4. Пусть агент вызовет start_work, затем проверьте изменение по How to check.",
    "settings.language": "Язык",
    "settings.languageHint": "Выберите язык подписей, пустых состояний и этой страницы справки.",
    "home.welcome":
      "Создайте проект, чтобы начать доску. Beacon добавит первую веху и несколько стартовых задач.",
  },
  lt: {
    "nav.home": "Pradžia",
    "nav.board": "Lenta",
    "nav.backlog": "Backlogas",
    "nav.roadmap": "Planas",
    "nav.context": "Kontekstas",
    "nav.files": "Failai",
    "nav.agents": "Agentai",
    "nav.decisions": "Sprendimai",
    "nav.reports": "Ataskaitos",
    "nav.settings": "Nustatymai",
    "nav.learn": "Sužinoti",
    "common.loading": "Kraunama…",
    "common.selectProject": "Pasirinkite projektą antraštėje.",
    "common.language": "Kalba",
    "common.org": "Org",
    "common.project": "Projektas",
    "common.logout": "Atsijungti",
    "common.failedSession": "nepavyko įkelti sesijos",
    "common.failedProjects": "nepavyko įkelti projektų",
    "common.logoutFailed": "nepavyko atsijungti",
    "reports.title": "Ataskaitos",
    "reports.intro":
      "Sukurkite dabartinės lentos momentinę kopiją arba importuokite peržiūrą, kad agentai ją perskaitytų ir sukurtų tolesnes užduotis.",
    "reports.generate": "Kurti ataskaitą",
    "reports.generating": "Kuriama…",
    "reports.snapshots": "Momentinės kopijos",
    "reports.emptyReports":
      "Ataskaitų dar nėra. Sukurkite vieną, kai reikia kontrolinio taško, kurį agentai gali skaityti.",
    "reports.reviews": "Importuotos peržiūros",
    "reports.reviewsIntro":
      "Įklijuokite peržiūrą, auditą ar pastabą. Agentai gali jas išvardyti ir paversti radinius užduotimis.",
    "reports.reviewTitle": "Pavadinimas (neprivaloma)",
    "reports.reviewBody": "Peržiūros „Markdown“",
    "reports.import": "Importuoti peržiūrą",
    "reports.importing": "Importuojama…",
    "reports.emptyReviews": "Importuotų peržiūrų dar nėra.",
    "reports.loadFailed": "nepavyko įkelti ataskaitų",
    "reports.generateFailed": "nepavyko sukurti ataskaitos",
    "reports.importFailed": "nepavyko importuoti peržiūros",
    "learn.title": "Sužinoti",
    "learn.intro":
      "Beacon yra operacinė sistema aplink jūsų agentus. Žmonės prižiūri brifą ir lentą. Vietiniai agentai ima Ready darbą.",
    "learn.start": "Pradėkite čia",
    "learn.startBody":
      "Sukurkite arba pasirinkite projektą antraštėje. Parašykite gyvą brifą Kontekste. Padėkite darbą Lentoje kaip Ready. Agentuose išduokite projekto žetoną ir paleiskite setup agento mašinoje.",
    "learn.screens": "Kam skirtas kiekvienas ekranas",
    "learn.home":
      "Pradžia – dabartinė momentinė kopija: brifas, etapai, indekso būsena ir darbas, kuris paruoštas arba vykdomas.",
    "learn.board":
      "Lenta – gyva eilė. Vilkite kortelę, kad pakeistumėte būseną. Ready – tai, ką gali pradėti vietiniai agentai.",
    "learn.backlog":
      "Backlogas – tas pats darbas sąrašu. Naudokite, kai reikia greitai peržiūrėti ar pakeisti būseną.",
    "learn.roadmap":
      "Planas grupuoja darbą pagal etapus. Priklausomybės rodo, kuri užduotis turi baigtis anksčiau.",
    "learn.context":
      "Kontekstas – gyvas AGENTS.md. Sudarykite sesijos brifą čia arba iš užduoties. Kodo indeksas neprivalomas.",
    "learn.agents":
      "Agentuose išduodate projekto žetoną ir stebite sesijas. Beacon nepaleidžia talpinamo kodo agento.",
    "learn.decisions":
      "Sprendimai ir apribojimai – tvarios taisyklės, kurių agentai neturėtų apeiti.",
    "learn.reports":
      "Ataskaitos – lentos kontrolinis taškas ir importuotos peržiūros. Agentai gali jas skaityti ir kurti užduotis.",
    "learn.settings":
      "Nustatymai saugo projekto id, narius ir sričių žymas. Sritys – kelio priešdėliai, ne laisvi žymekliai.",
    "learn.check": "Kaip patikrinti baigtą užduotį",
    "learn.checkBody":
      "Atidarykite užduotį. Perskaitykite How to check. Atlikite tuos veiksmus programoje. Jei pastabos tuščios, paprašykite agento jas įrašyti finish_work.",
    "learn.multi": "Daugiau nei vienas projektas",
    "learn.multiBody":
      "Kiekvienam Beacon projektui reikia savo žetono. Vėl paleiskite beacon connect arba beacon setup su tuo id. Vietinis MCP persijungia su --project, BEACON_PROJECT arba project_id įrankio kvietime.",
    "learn.guide": "Pirma valanda",
    "learn.guideOne": "1. Kontekste parašykite Goals ir Definition of Done.",
    "learn.guideTwo": "2. Sukurkite Ready užduotį su užpildytu How to check.",
    "learn.guideThree": "3. Agentuose išduokite žetoną ir paleiskite setup.cmd arba beacon setup.",
    "learn.guideFour": "4. Tegul agentas kviečia start_work, tada patvirtinkite pakeitimą pagal How to check.",
    "settings.language": "Kalba",
    "settings.languageHint": "Pasirinkite etikečių, tuščių būsenų ir šio Sužinoti puslapio kalbą.",
    "home.welcome":
      "Sukurkite projektą, kad pradėtumėte lentą. Beacon pridės pirmą etapą ir kelias pradines užduotis.",
  },
  lv: {
    "nav.home": "Sākums",
    "nav.board": "Dēlis",
    "nav.backlog": "Backlog",
    "nav.roadmap": "Ceļa karte",
    "nav.context": "Konteksts",
    "nav.files": "Faili",
    "nav.agents": "Aģenti",
    "nav.decisions": "Lēmumi",
    "nav.reports": "Pārskati",
    "nav.settings": "Iestatījumi",
    "nav.learn": "Uzzināt",
    "common.loading": "Ielādē…",
    "common.selectProject": "Izvēlieties projektu galvenē.",
    "common.language": "Valoda",
    "common.org": "Org",
    "common.project": "Projekts",
    "common.logout": "Iziet",
    "common.failedSession": "neizdevās ielādēt sesiju",
    "common.failedProjects": "neizdevās ielādēt projektus",
    "common.logoutFailed": "neizdevās iziet",
    "reports.title": "Pārskati",
    "reports.intro":
      "Izveidojiet pašreizējā dēļa momentuzņēmumu vai importējiet pārskatu, lai aģenti to izlasītu un izveidotu turpmākos uzdevumus.",
    "reports.generate": "Ģenerēt pārskatu",
    "reports.generating": "Ģenerē…",
    "reports.snapshots": "Momentuzņēmumi",
    "reports.emptyReports":
      "Pārskatu vēl nav. Ģenerējiet vienu, kad vajadzīgs kontrolpunkts, ko aģenti var lasīt.",
    "reports.reviews": "Importētās recenzijas",
    "reports.reviewsIntro":
      "Ielīmējiet recenziju, auditu vai piezīmi. Aģenti var tās uzskaitīt un pārvērst atklājumus uzdevumos.",
    "reports.reviewTitle": "Virsraksts (neobligāti)",
    "reports.reviewBody": "Recenzijas Markdown",
    "reports.import": "Importēt recenziju",
    "reports.importing": "Importē…",
    "reports.emptyReviews": "Importētu recenziju vēl nav.",
    "reports.loadFailed": "neizdevās ielādēt pārskatus",
    "reports.generateFailed": "neizdevās ģenerēt pārskatu",
    "reports.importFailed": "neizdevās importēt recenziju",
    "learn.title": "Uzzināt",
    "learn.intro":
      "Beacon ir operētājsistēma ap jūsu aģentiem. Cilvēki uztur brīfu un dēli. Vietējie aģenti ņem Ready darbu.",
    "learn.start": "Sāciet šeit",
    "learn.startBody":
      "Izveidojiet vai izvēlieties projektu galvenē. Uzrakstiet dzīvo brīfu Kontekstā. Ielieciet darbu Dēlī kā Ready. Aģentos izsniedziet projekta marķieri un palaidiet setup aģenta mašīnā.",
    "learn.screens": "Kam paredzēts katrs ekrāns",
    "learn.home":
      "Sākums ir pašreizējais momentuzņēmums: brīfs, atskaites punkti, indeksa stāvoklis un darbs, kas gatavs vai jau notiek.",
    "learn.board":
      "Dēlis ir dzīvā rinda. Velciet kartīti, lai mainītu statusu. Ready ir tas, ko vietējie aģenti var sākt.",
    "learn.backlog":
      "Backlog ir tas pats darbs sarakstā. Izmantojiet, lai ātri pārskatītu vai mainītu statusu.",
    "learn.roadmap":
      "Ceļa karte grupē darbu pēc atskaites punktiem. Atkarības rāda, kuram uzdevumam jābeidzas agrāk.",
    "learn.context":
      "Konteksts ir dzīvais AGENTS.md. Kompilējiet sesijas brīfu šeit vai no uzdevuma. Koda indekss nav obligāts.",
    "learn.agents":
      "Aģentos jūs izsniedzat projekta marķieri un skatāt sesijas. Beacon nepalaiz mitinātu koda aģentu.",
    "learn.decisions":
      "Lēmumi un ierobežojumi ir noturīgi noteikumi, kurus aģentiem nevajadzētu apiet.",
    "learn.reports":
      "Pārskati ir dēļa kontrolpunkts plus importētās recenzijas. Aģenti var tās lasīt un veidot uzdevumus.",
    "learn.settings":
      "Iestatījumi glabā projekta id, dalībniekus un apgabalu iezīmes. Apgabali ir prefiksi, nevis brīvas birkas.",
    "learn.check": "Kā pārbaudīt pabeigtu uzdevumu",
    "learn.checkBody":
      "Atveriet uzdevumu. Izlasiet How to check. Izpildiet šos soļus lietotnē. Ja piezīmes ir tukšas, lūdziet aģentam tās ierakstīt finish_work.",
    "learn.multi": "Vairāk nekā viens projekts",
    "learn.multiBody":
      "Katram Beacon projektam vajadzīgs savs marķieris. Atkārtoti palaidiet beacon connect vai beacon setup ar šo id. Vietējais MCP pārslēdzas ar --project, BEACON_PROJECT vai project_id rīka izsaukumā.",
    "learn.guide": "Pirmā stunda",
    "learn.guideOne": "1. Kontekstā uzrakstiet Goals un Definition of Done.",
    "learn.guideTwo": "2. Izveidojiet Ready uzdevumu ar aizpildītu How to check.",
    "learn.guideThree": "3. Aģentos izsniedziet marķieri un palaidiet setup.cmd vai beacon setup.",
    "learn.guideFour": "4. Ļaujiet aģentam izsaukt start_work, tad apstipriniet izmaiņu pēc How to check.",
    "settings.language": "Valoda",
    "settings.languageHint": "Izvēlieties etiķešu, tukšo stāvokļu un šīs Uzzināt lapas valodu.",
    "home.welcome":
      "Izveidojiet projektu, lai sāktu dēli. Beacon pievienos pirmo atskaites punktu un dažus sākuma uzdevumus.",
  },
  be: {
    "nav.home": "Галоўная",
    "nav.board": "Дошка",
    "nav.backlog": "Бэклог",
    "nav.roadmap": "Дарожная карта",
    "nav.context": "Кантэкст",
    "nav.files": "Файлы",
    "nav.agents": "Агенты",
    "nav.decisions": "Рашэнні",
    "nav.reports": "Справаздачы",
    "nav.settings": "Налады",
    "nav.learn": "Даведка",
    "common.loading": "Загрузка…",
    "common.selectProject": "Абярыце праект у шапцы.",
    "common.language": "Мова",
    "common.org": "Арг",
    "common.project": "Праект",
    "common.logout": "Выйсці",
    "common.failedSession": "не ўдалося загрузіць сесію",
    "common.failedProjects": "не ўдалося загрузіць праекты",
    "common.logoutFailed": "не ўдалося выйсці",
    "reports.title": "Справаздачы",
    "reports.intro":
      "Зрабіце здымак бягучай дошкі або імпартуйце рэўю, каб агенты прачыталі яго і стварылі задачы.",
    "reports.generate": "Згенераваць справаздачу",
    "reports.generating": "Генерацыя…",
    "reports.snapshots": "Здымкі",
    "reports.emptyReports":
      "Справаздач яшчэ няма. Згенеруйце кантрольны пункт, калі агентам трэба штосьці прачытаць.",
    "reports.reviews": "Імпартаваныя рэўю",
    "reports.reviewsIntro":
      "Устаўце рэўю, аўдыт або нататку. Агенты могуць чытаць іх і ператвараць знаходкі ў задачы.",
    "reports.reviewTitle": "Загаловак (неабавязкова)",
    "reports.reviewBody": "Markdown рэўю",
    "reports.import": "Імпартаваць рэўю",
    "reports.importing": "Імпарт…",
    "reports.emptyReviews": "Імпартаваных рэўю яшчэ няма.",
    "reports.loadFailed": "не ўдалося загрузіць справаздачы",
    "reports.generateFailed": "не ўдалося згенераваць справаздачу",
    "reports.importFailed": "не ўдалося імпартаваць рэўю",
    "learn.title": "Даведка",
    "learn.intro":
      "Beacon — аперацыйная сістэма вакол вашых агентаў. Людзі трымаюць брыф і дошку. Лакальныя агенты бяруць Ready-працу.",
    "learn.start": "Пачніце тут",
    "learn.startBody":
      "Стварыце або абярыце праект у шапцы. Напішыце жывы брыф у Кантэксце. Пастаўце працу Ready на Дошцы. Выпусьціце токен праекта ў Агентах і запусціце setup на машыне агента.",
    "learn.screens": "Навошта кожны экран",
    "learn.home":
      "Галоўная — бягучы здымак: брыф, вехі, стан індэкса і праца, якая гатовая або ўжо ідзе.",
    "learn.board":
      "Дошка — жывая чарга. Перацягніце картку, каб змяніць статус. Ready — тое, што могуць пачаць лакальныя агенты.",
    "learn.backlog":
      "Бэклог — тая ж праца спісам. Зручна хутка прагледзець або змяніць статус.",
    "learn.roadmap":
      "Дарожная карта групуе працу па вехах. Залежнасці паказваюць, якая задача мае скончыцца раней.",
    "learn.context":
      "Кантэкст — жывы AGENTS.md. Складзіце сесійны брыф адсюль або з задачы. Індэкс кода неабавязковы.",
    "learn.agents":
      "У Агентах выпускаюць токен праекта і глядзяць сесіі. Beacon не запускае размешчанага агента кадавання.",
    "learn.decisions":
      "Рашэнні і абмежаванні — устойлівыя правілы, якія агенты не павінны абходзіць.",
    "learn.reports":
      "Справаздачы — кантрольны пункт дошкі плюс імпартаваныя рэўю. Агенты могуць чытаць іх і ствараць задачы.",
    "learn.settings":
      "Налады захоўваюць id праекта, удзельнікаў і пазнакі абласцей. Вобласці — прэфіксы шляхоў, не свабодныя чыпы.",
    "learn.check": "Як праверыць завершаную задачу",
    "learn.checkBody":
      "Адкрыйце задачу. Прачытайце How to check. Выканайце гэтыя крокі ў дадатку. Калі нататкі пустыя, папрасіце агента запісаць іх у finish_work.",
    "learn.multi": "Некалькі праектаў",
    "learn.multiBody":
      "Кожнаму праекту Beacon патрэбны свой токен. Зноў запусціце beacon connect або beacon setup з гэтым id. Лакальны MCP пераключаецца праз --project, BEACON_PROJECT або project_id у выкліку інструмента.",
    "learn.guide": "Першая гадзіна",
    "learn.guideOne": "1. Напішыце Goals і Definition of Done ў Кантэксце.",
    "learn.guideTwo": "2. Стварыце Ready-задачу з запоўненым How to check.",
    "learn.guideThree": "3. Выпусьціце токен у Агентах і запусціце setup.cmd або beacon setup.",
    "learn.guideFour": "4. Няхай агент выкліча start_work, потым праверце змяненне па How to check.",
    "settings.language": "Мова",
    "settings.languageHint": "Абярыце мову подпісаў, пустых станаў і гэтай старонкі даведкі.",
    "home.welcome":
      "Стварыце праект, каб пачаць дошку. Beacon дадасць першую веху і некалькі стартавых задач.",
  },
  kk: {
    "nav.home": "Басты бет",
    "nav.board": "Тақта",
    "nav.backlog": "Бэклог",
    "nav.roadmap": "Жол картасы",
    "nav.context": "Контекст",
    "nav.files": "Файлдар",
    "nav.agents": "Агенттер",
    "nav.decisions": "Шешімдер",
    "nav.reports": "Есептер",
    "nav.settings": "Параметрлер",
    "nav.learn": "Анықтама",
    "common.loading": "Жүктелуде…",
    "common.selectProject": "Тақырыпта жобаны таңдаңыз.",
    "common.language": "Тіл",
    "common.org": "Ұйым",
    "common.project": "Жоба",
    "common.logout": "Шығу",
    "common.failedSession": "сессияны жүктеу мүмкін болмады",
    "common.failedProjects": "жобаларды жүктеу мүмкін болмады",
    "common.logoutFailed": "шығу сәтсіз аяқталды",
    "reports.title": "Есептер",
    "reports.intro":
      "Ағымдағы тақтаның суретін жасаңыз немесе шолуды импорттаңыз, сонда агенттер оны оқып, кейінгі тапсырмалар жасайды.",
    "reports.generate": "Есеп жасау",
    "reports.generating": "Жасалуда…",
    "reports.snapshots": "Суреттер",
    "reports.emptyReports":
      "Есептер әлі жоқ. Агенттер оқи алатын бақылау нүктесі керек кезде біреуін жасаңыз.",
    "reports.reviews": "Импортталған шолулар",
    "reports.reviewsIntro":
      "Шолу, аудит немесе жазбаны қойыңыз. Агенттер оларды тізіп, табылғандарды тапсырмаға айналдыра алады.",
    "reports.reviewTitle": "Тақырып (міндетті емес)",
    "reports.reviewBody": "Шолу Markdown",
    "reports.import": "Шолуды импорттау",
    "reports.importing": "Импорт…",
    "reports.emptyReviews": "Импортталған шолулар әлі жоқ.",
    "reports.loadFailed": "есептерді жүктеу мүмкін болмады",
    "reports.generateFailed": "есеп жасау мүмкін болмады",
    "reports.importFailed": "шолуды импорттау мүмкін болмады",
    "learn.title": "Анықтама",
    "learn.intro":
      "Beacon — агенттеріңіздің айналасындағы операциялық жүйе. Адамдар бриф пен тақтаны ұстайды. Жергілікті агенттер Ready жұмысын алады.",
    "learn.start": "Осы жерден бастаңыз",
    "learn.startBody":
      "Тақырыпта жоба жасаңыз немесе таңдаңыз. Контексте тірі бриф жазыңыз. Жұмысты Тақтада Ready етіп қойыңыз. Агенттерде жоба токенін шығарып, агент машинасында setup іске қосыңыз.",
    "learn.screens": "Әр экран не үшін",
    "learn.home":
      "Басты бет — ағымдағы сурет: бриф, кезеңдер, индекс күйі және дайын немесе жүріп жатқан жұмыс.",
    "learn.board":
      "Тақта — тірі кезек. Күйін өзгерту үшін картаны сүйреңіз. Ready — жергілікті агенттер бастай алатын жұмыс.",
    "learn.backlog":
      "Бэклог — сол жұмыс тізім түрінде. Жылдам қарау немесе күйін өзгерту үшін пайдаланыңыз.",
    "learn.roadmap":
      "Жол картасы жұмысты кезең бойынша топтайды. Тәуелділіктер қай тапсырманың бұрын бітуі керектігін көрсетеді.",
    "learn.context":
      "Контекст — тірі AGENTS.md. Сессия брифін осы жерден немесе тапсырмадан құрастырыңыз. Код индексі міндетті емес.",
    "learn.agents":
      "Агенттерде жоба токенін шығарып, сессияларды көресіз. Beacon хостталатын код агентін іске қоспайды.",
    "learn.decisions":
      "Шешімдер мен шектеулер — агенттер айналып өтпеуі тиіс тұрақты ережелер.",
    "learn.reports":
      "Есептер — тақтаның бақылау нүктесі және импортталған шолулар. Агенттер оларды оқып, тапсырма жасай алады.",
    "learn.settings":
      "Параметрлер жоба id-ін, мүшелерді және аймақ белгілерін сақтайды. Аймақтар — жол префикстері, еркін чиптер емес.",
    "learn.check": "Аяқталған тапсырманы қалай тексеру керек",
    "learn.checkBody":
      "Тапсырманы ашыңыз. How to check оқыңыз. Қолданбада сол қадамдарды орындаңыз. Жазбалар бос болса, агенттен finish_work кезінде жазуын сұраңыз.",
    "learn.multi": "Бірнеше жоба",
    "learn.multiBody":
      "Әр Beacon жобасына өз токені керек. Сол idмен beacon connect немесе beacon setup-ты қайта іске қосыңыз. Жергілікті MCP --project, BEACON_PROJECT немесе құрал шақыруындағы project_id арқылы ауысады.",
    "learn.guide": "Бірінші сағат",
    "learn.guideOne": "1. Контексте Goals пен Definition of Done жазыңыз.",
    "learn.guideTwo": "2. How to check толтырылған Ready тапсырмасын жасаңыз.",
    "learn.guideThree": "3. Агенттерде токен шығарып, setup.cmd немесе beacon setup іске қосыңыз.",
    "learn.guideFour": "4. Агент start_work шақырсын, содан кейін How to check бойынша өзгерісті растаңыз.",
    "settings.language": "Тіл",
    "settings.languageHint": "Жапсырмалар, бос күйлер және осы Анықтама бетінің тілін таңдаңыз.",
    "home.welcome":
      "Тақтаны бастау үшін жоба жасаңыз. Beacon бірінші кезең мен бірнеше бастапқы тапсырма қосады.",
  },
  ko: {
    "nav.home": "홈",
    "nav.board": "보드",
    "nav.backlog": "백로그",
    "nav.roadmap": "로드맵",
    "nav.context": "컨텍스트",
    "nav.files": "파일",
    "nav.agents": "에이전트",
    "nav.decisions": "결정",
    "nav.reports": "보고서",
    "nav.settings": "설정",
    "nav.learn": "배우기",
    "common.loading": "불러오는 중…",
    "common.selectProject": "헤더에서 프로젝트를 선택하세요.",
    "common.language": "언어",
    "common.org": "조직",
    "common.project": "프로젝트",
    "common.logout": "로그아웃",
    "common.failedSession": "세션을 불러오지 못했습니다",
    "common.failedProjects": "프로젝트를 불러오지 못했습니다",
    "common.logoutFailed": "로그아웃에 실패했습니다",
    "reports.title": "보고서",
    "reports.intro":
      "현재 보드의 스냅샷을 만들거나 리뷰를 가져와 에이전트가 읽고 후속 작업을 만들 수 있게 하세요.",
    "reports.generate": "보고서 생성",
    "reports.generating": "생성 중…",
    "reports.snapshots": "스냅샷",
    "reports.emptyReports":
      "아직 보고서가 없습니다. 에이전트가 읽을 수 있는 점검 지점이 필요할 때 생성하세요.",
    "reports.reviews": "가져온 리뷰",
    "reports.reviewsIntro":
      "리뷰, 감사 또는 메모를 붙여 넣으세요. 에이전트가 목록을 보고 발견 사항을 작업으로 바꿀 수 있습니다.",
    "reports.reviewTitle": "제목(선택)",
    "reports.reviewBody": "리뷰 Markdown",
    "reports.import": "리뷰 가져오기",
    "reports.importing": "가져오는 중…",
    "reports.emptyReviews": "아직 가져온 리뷰가 없습니다.",
    "reports.loadFailed": "보고서를 불러오지 못했습니다",
    "reports.generateFailed": "보고서를 생성하지 못했습니다",
    "reports.importFailed": "리뷰를 가져오지 못했습니다",
    "learn.title": "배우기",
    "learn.intro":
      "Beacon은 에이전트 주변의 운영 체제입니다. 사람이 브리프와 보드를 유지하고, 로컬 에이전트가 Ready 작업을 가져갑니다.",
    "learn.start": "여기서 시작",
    "learn.startBody":
      "헤더에서 프로젝트를 만들거나 고르세요. 컨텍스트에 살아있는 브리프를 쓰세요. 보드에 Ready 작업을 올리세요. 에이전트에서 프로젝트 토큰을 발급하고 에이전트가 있는 기기에서 setup을 실행하세요.",
    "learn.screens": "각 화면의 용도",
    "learn.home":
      "홈은 현재 스냅샷입니다. 브리프, 마일스톤, 인덱스 상태, 준비되었거나 진행 중인 작업.",
    "learn.board":
      "보드는 살아있는 대기열입니다. 카드를 끌어 상태를 바꿉니다. Ready는 로컬 에이전트가 시작할 수 있는 일입니다.",
    "learn.backlog":
      "백로그는 같은 일의 목록입니다. 빠르게 훑거나 상태를 바꿀 때 사용하세요.",
    "learn.roadmap":
      "로드맵은 마일스톤별로 일을 묶습니다. 의존성은 어떤 작업이 먼저 끝나야 하는지 보여 줍니다.",
    "learn.context":
      "컨텍스트는 살아있는 AGENTS.md입니다. 여기 또는 작업에서 세션 브리프를 컴파일하세요. 코드 인덱스는 선택입니다.",
    "learn.agents":
      "에이전트에서 프로젝트 토큰을 발급하고 세션을 봅니다. Beacon은 호스팅된 코딩 에이전트를 실행하지 않습니다.",
    "learn.decisions":
      "결정과 제약은 에이전트가 둘러서 만들지 말아야 할 지속적인 규칙입니다.",
    "learn.reports":
      "보고서는 보드 점검 지점과 가져온 리뷰입니다. 에이전트가 목록을 보고 발견 사항을 작업으로 바꿀 수 있습니다.",
    "learn.settings":
      "설정에는 프로젝트 id, 구성원, 영역 레이블이 있습니다. 영역은 접두사이며 자유 칩이 아닙니다.",
    "learn.check": "완료된 작업을 확인하는 방법",
    "learn.checkBody":
      "작업을 엽니다. How to check를 읽습니다. 앱에서 그 단계를 따릅니다. 메모가 비어 있으면 에이전트에게 finish_work에 쓰라고 요청하세요.",
    "learn.multi": "프로젝트가 둘 이상일 때",
    "learn.multiBody":
      "각 Beacon 프로젝트에는 자체 토큰이 필요합니다. 해당 프로젝트 id로 beacon connect 또는 beacon setup을 다시 실행하세요. 로컬 MCP는 --project, BEACON_PROJECT 또는 도구 호출의 project_id로 전환합니다.",
    "learn.guide": "첫 한 시간",
    "learn.guideOne": "1. 컨텍스트에 Goals와 Definition of Done을 씁니다.",
    "learn.guideTwo": "2. How to check가 채워진 Ready 작업을 만듭니다.",
    "learn.guideThree": "3. 에이전트에서 토큰을 발급하고 setup.cmd 또는 beacon setup을 실행합니다.",
    "learn.guideFour": "4. 에이전트가 start_work를 호출하게 한 뒤 How to check로 변경을 확인합니다.",
    "settings.language": "언어",
    "settings.languageHint": "레이블, 빈 상태, 이 배우기 페이지의 언어를 선택하세요.",
    "home.welcome":
      "보드를 시작하려면 프로젝트를 만드세요. Beacon이 첫 마일스톤과 몇 가지 시작 작업을 추가합니다.",
  },
} as const;

export type MessageKey = keyof (typeof STRINGS)["en"];
type Catalog = Partial<Record<MessageKey, string>>;
const CATALOGS: Record<Locale, Catalog> = STRINGS;

let currentLocale: Locale = "en";
const listeners = new Set<() => void>();

export function isLocale(value: string): value is Locale {
  return (LOCALES as readonly string[]).includes(value);
}

export function getLocale(): Locale {
  return currentLocale;
}

export function setLocale(locale: Locale): void {
  if (currentLocale === locale) {
    return;
  }
  currentLocale = locale;
  if (typeof window !== "undefined") {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, locale);
    document.documentElement.lang = locale;
  }
  for (const listener of listeners) {
    listener();
  }
}

export function hydrateLocale(): Locale {
  if (typeof window === "undefined") {
    return currentLocale;
  }
  const stored = window.localStorage.getItem(LOCALE_STORAGE_KEY);
  if (stored && isLocale(stored)) {
    currentLocale = stored;
    document.documentElement.lang = stored;
    return stored;
  }
  const nav = window.navigator.language.slice(0, 2).toLowerCase();
  if (isLocale(nav)) {
    currentLocale = nav;
    document.documentElement.lang = nav;
  }
  return currentLocale;
}

export function subscribeLocale(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function t(key: MessageKey, locale: Locale = currentLocale): string {
  return CATALOGS[locale][key] ?? CATALOGS.en[key] ?? key;
}

export function tf(
  key: MessageKey,
  vars: Record<string, string>,
  locale: Locale = currentLocale,
): string {
  return Object.entries(vars).reduce(
    (text, [name, value]) => text.replaceAll(`{${name}}`, value),
    t(key, locale),
  );
}

const ACTIVITY_VERB_KEYS: Record<string, MessageKey> = {
  create: "activity.create",
  update: "activity.update",
  delete: "activity.delete",
  status: "activity.status",
  comment: "activity.comment",
  start_work: "activity.start_work",
  finish_work: "activity.finish_work",
  lock_stolen: "activity.lock_stolen",
  lock_released: "activity.lock_released",
  propose: "activity.propose",
  apply: "activity.apply",
  import: "activity.import",
  github_clone_invalidated: "activity.github_clone_invalidated",
};

export function activityVerbLabel(verb: string, locale: Locale = currentLocale): string {
  const key = ACTIVITY_VERB_KEYS[verb];
  return key ? t(key, locale) : verb.replaceAll("_", " ");
}

export function activityLine(
  verb: string,
  objectType: string,
  locale: Locale = currentLocale,
): string {
  return tf("activity.line", { verb: activityVerbLabel(verb, locale), object: objectType }, locale);
}
