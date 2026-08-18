"use client";

import { useRouter } from "next/navigation";
import { useEffect, useMemo, useState, type FormEvent } from "react";

import {
  ApiError,
  createMilestone,
  createOrgProject,
  createProjectRepo,
  createTask,
  fetchMe,
  fetchProjectMilestones,
  fetchProjectTasks,
  mintProjectToken,
  saveProjectBrief,
  requestRepoDetect,
  type PublicProject,
  type PublicRepo,
  type PublicToken,
} from "@/lib/api";
import { ORG_STORAGE_KEY, PROJECT_STORAGE_KEY, readStoredId, writeStoredId } from "../../selection";

const STEPS = [
  "Create project",
  "Connect code",
  "Detect",
  "First brief",
  "Connect an agent",
] as const;

type DetectStatus = "idle" | "pending" | "later" | "started";
type AgentTab = "stdio" | "http";

function slugFromName(name: string): string {
  const slug = name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 64);
  return slug;
}

export function ProjectWizard({
  workspaceEnabled,
  githubAppEnabled,
}: {
  workspaceEnabled: boolean;
  githubAppEnabled: boolean;
}) {
  const router = useRouter();
  const [step, setStep] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const [orgId, setOrgId] = useState<string | null>(null);
  const [orgName, setOrgName] = useState<string | null>(null);

  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [slugTouched, setSlugTouched] = useState(false);
  const [project, setProject] = useState<PublicProject | null>(null);

  const [repo, setRepo] = useState<PublicRepo | null>(null);
  const [workspacePath, setWorkspacePath] = useState(".");
  const [token, setToken] = useState<PublicToken | null>(null);
  const [connectKind, setConnectKind] = useState<"none" | "cli" | "workspace">("none");

  const [detectStatus, setDetectStatus] = useState<DetectStatus>("idle");

  const [goals, setGoals] = useState("Ship a first working loop for this project.");
  const [nonGoals, setNonGoals] = useState("Do not invent extra scope before the first milestone.");
  const [milestoneTitle, setMilestoneTitle] = useState("First slice");
  const [taskTitle, setTaskTitle] = useState("Confirm repo layout and fill the first brief");
  const [milestoneId, setMilestoneId] = useState<string | null>(null);
  const [taskId, setTaskId] = useState<string | null>(null);
  const [briefSaved, setBriefSaved] = useState(false);

  const [agentTab, setAgentTab] = useState<AgentTab>("stdio");

  const derivedSlug = useMemo(
    () => (slugTouched ? slug : slugFromName(name)),
    [name, slug, slugTouched],
  );

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const me = await fetchMe();
        if (cancelled) {
          return;
        }
        if (!me) {
          router.replace("/login");
          return;
        }
        const stored = readStoredId(ORG_STORAGE_KEY);
        const org =
          (stored ? me.orgs.find((item) => item.id === stored) : undefined) ??
          me.personal_org ??
          me.orgs[0] ??
          null;
        setOrgId(org?.id ?? null);
        setOrgName(org?.name ?? null);
      } catch (caught) {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : "failed to load session");
        }
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [router]);

  async function onCreateProject(event: FormEvent) {
    event.preventDefault();
    if (!orgId) {
      setError("Select an org in the header first.");
      return;
    }
    setPending(true);
    setError(null);
    try {
      const created = await createOrgProject(orgId, { name: name.trim(), slug: derivedSlug });
      setProject(created);
      writeStoredId(PROJECT_STORAGE_KEY, created.id);
      setStep(1);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "failed to create project");
    } finally {
      setPending(false);
    }
  }

  async function mintCliToken() {
    if (!project) {
      return;
    }
    setPending(true);
    setError(null);
    try {
      const minted = await mintProjectToken(project.id, "this-machine");
      setToken(minted);
      setConnectKind("cli");
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "failed to mint token");
    } finally {
      setPending(false);
    }
  }

  async function connectWorkspace() {
    if (!project) {
      return;
    }
    setPending(true);
    setError(null);
    try {
      const created = await createProjectRepo(project.id, {
        provider: "local",
        index_mode: "bind_mount",
        local_root_hint: workspacePath.trim() || ".",
      });
      setRepo(created);
      setConnectKind("workspace");
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "failed to index workspace");
    } finally {
      setPending(false);
    }
  }

  async function startDetect() {
    if (!repo) {
      setDetectStatus("later");
      setStep(3);
      return;
    }
    setPending(true);
    setError(null);
    setDetectStatus("pending");
    try {
      const result = await requestRepoDetect(repo.id);
      setDetectStatus(result === "started" ? "started" : "later");
      setStep(3);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "failed to start detect");
      setDetectStatus("later");
    } finally {
      setPending(false);
    }
  }

  async function saveBrief(event: FormEvent) {
    event.preventDefault();
    if (!project) {
      return;
    }
    setPending(true);
    setError(null);
    try {
      await saveProjectBrief(project.id, [
        { id: "goals", title: "Goals", body_md: goals, ordinal: 0 },
        { id: "non_goals", title: "Non-goals", body_md: nonGoals, ordinal: 1 },
      ]);
      const wantedMilestone = milestoneTitle.trim();
      const wantedTask = taskTitle.trim();
      let nextMilestoneId = milestoneId;
      if (!nextMilestoneId) {
        const existingMilestone = (await fetchProjectMilestones(project.id)).find(
          (item) => item.title === wantedMilestone,
        );
        if (existingMilestone) {
          nextMilestoneId = existingMilestone.id;
        } else {
          const created = await createMilestone(project.id, { title: wantedMilestone });
          nextMilestoneId = created.id;
        }
        setMilestoneId(nextMilestoneId);
      }
      if (!taskId) {
        const existingTask = (await fetchProjectTasks(project.id)).find(
          (item) => item.title === wantedTask && item.milestone_id === nextMilestoneId,
        );
        if (existingTask) {
          setTaskId(existingTask.id);
        } else {
          const created = await createTask(project.id, {
            title: wantedTask,
            milestone_id: nextMilestoneId,
            description: "Proposed first task from the new-project wizard.",
          });
          setTaskId(created.id);
        }
      }
      setBriefSaved(true);
      setStep(4);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "failed to save brief");
    } finally {
      setPending(false);
    }
  }

  function finish() {
    if (project) {
      writeStoredId(PROJECT_STORAGE_KEY, project.id);
    }
    router.replace("/app");
    router.refresh();
  }

  return (
    <section className="mx-auto flex w-full max-w-3xl flex-col gap-6">
      <header className="space-y-2">
        <p className="text-sm text-muted">New project{orgName ? ` · ${orgName}` : ""}</p>
        <h1 className="text-2xl font-semibold tracking-tight">{STEPS[step]}</h1>
        <ol className="flex flex-wrap gap-2 text-xs text-muted">
          {STEPS.map((label, index) => (
            <li
              key={label}
              className={`rounded-full border px-2 py-1 ${
                index === step ? "border-foreground text-foreground" : "border-border"
              }`}
            >
              {index + 1}. {label}
            </li>
          ))}
        </ol>
      </header>

      {error ? <p className="text-sm text-red-600">{error}</p> : null}

      {step === 0 ? (
        <form
          className="flex flex-col gap-4 rounded-2xl border border-border bg-surface p-6"
          onSubmit={onCreateProject}
        >
          <label className="flex flex-col gap-1 text-sm">
            Name
            <input
              className="h-11 rounded-lg border border-border bg-background px-3"
              value={name}
              onChange={(event) => setName(event.target.value)}
              required
              maxLength={120}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            Slug
            <input
              className="h-11 rounded-lg border border-border bg-background px-3 font-mono"
              value={derivedSlug}
              onChange={(event) => {
                setSlugTouched(true);
                setSlug(event.target.value);
              }}
              required
              maxLength={64}
            />
          </label>
          <p className="text-xs leading-5 text-muted">
            Visibility is private. The org comes from the header switcher.
          </p>
          <button
            className="h-11 rounded-lg bg-accent text-sm font-medium text-accent-fg disabled:opacity-60"
            type="submit"
            disabled={pending || !orgId}
          >
            {pending ? "Creating…" : "Create project"}
          </button>
        </form>
      ) : null}

      {step === 1 && project ? (
        <div className="grid gap-4">
          <article className="flex flex-col gap-3 rounded-2xl border border-border bg-surface p-5">
            <h2 className="text-lg font-semibold">GitHub repository</h2>
            {githubAppEnabled ? (
              <p className="text-sm leading-6 text-muted">
                GitHub App import is available later from Settings. Continue with this machine or a
                workspace path.
              </p>
            ) : (
              <p className="text-sm leading-6 text-muted">
                Unavailable on this instance. Coming later.
              </p>
            )}
          </article>

          <article className="flex flex-col gap-3 rounded-2xl border border-border bg-surface p-5">
            <h2 className="text-lg font-semibold">This machine</h2>
            <p className="text-sm leading-6 text-muted">
              Mint a CLI token and run <code className="font-mono">beacon connect</code> on the
              machine that has the code.
            </p>
            <button
              className="h-10 w-fit rounded-lg bg-accent px-4 text-sm font-medium text-accent-fg disabled:opacity-60"
              type="button"
              onClick={() => void mintCliToken()}
              disabled={pending}
            >
              {token ? "Mint another token" : "Mint CLI token"}
            </button>
            {token?.token ? (
              <div className="space-y-2 rounded-lg border border-dashed border-border p-3 text-sm">
                <p className="text-muted">Shown once. Copy it now.</p>
                <pre className="overflow-x-auto font-mono text-xs">{token.token}</pre>
                <pre className="overflow-x-auto font-mono text-xs">
                  beacon connect {token.token}
                </pre>
              </div>
            ) : null}
          </article>

          {workspaceEnabled ? (
            <article className="flex flex-col gap-3 rounded-2xl border border-border bg-surface p-5">
              <h2 className="text-lg font-semibold">Index this Compose workspace</h2>
              <p className="text-sm leading-6 text-muted">
                Relative POSIX path under the workspace volume. Creates a local bind-mount repo.
              </p>
              <label className="flex flex-col gap-1 text-sm">
                Path
                <input
                  className="h-11 rounded-lg border border-border bg-background px-3 font-mono"
                  value={workspacePath}
                  onChange={(event) => setWorkspacePath(event.target.value)}
                />
              </label>
              <button
                className="h-10 w-fit rounded-lg border border-border px-4 text-sm font-medium disabled:opacity-60"
                type="button"
                onClick={() => void connectWorkspace()}
                disabled={pending}
              >
                {repo ? "Connected" : "Index this path"}
              </button>
            </article>
          ) : null}

          <div className="flex justify-end">
            <button
              className="h-11 rounded-lg bg-accent px-4 text-sm font-medium text-accent-fg"
              type="button"
              onClick={() => setStep(2)}
            >
              Continue
            </button>
          </div>
        </div>
      ) : null}

      {step === 2 && project ? (
        <div className="flex flex-col gap-4 rounded-2xl border border-border bg-surface p-6">
          <p className="text-sm leading-6 text-muted">
            Detect runs in the background. You can continue without waiting.
          </p>
          {connectKind === "none" && !repo ? (
            <p className="rounded-lg border border-dashed border-border px-3 py-2 text-sm text-muted">
              No repo connected yet. Detection can run later.
            </p>
          ) : null}
          {detectStatus === "pending" ? (
            <div className="h-16 animate-pulse rounded-lg bg-background" />
          ) : null}
          <div className="flex gap-2">
            <button
              className="h-11 rounded-lg bg-accent px-4 text-sm font-medium text-accent-fg disabled:opacity-60"
              type="button"
              onClick={() => void startDetect()}
              disabled={pending}
            >
              {repo ? "Start detect" : "Run later"}
            </button>
            <button
              className="h-11 rounded-lg border border-border px-4 text-sm font-medium"
              type="button"
              onClick={() => {
                setDetectStatus("later");
                setStep(3);
              }}
            >
              Skip for now
            </button>
          </div>
        </div>
      ) : null}

      {step === 3 && project ? (
        <form
          className="flex flex-col gap-4 rounded-2xl border border-border bg-surface p-6"
          onSubmit={saveBrief}
        >
          {detectStatus === "later" || detectStatus === "pending" ? (
            <p className="rounded-lg border border-dashed border-border px-3 py-2 text-sm text-muted">
              Detection is pending. You can run it later and keep editing this brief.
            </p>
          ) : null}
          <label className="flex flex-col gap-1 text-sm">
            Goals
            <textarea
              className="min-h-24 rounded-lg border border-border bg-background px-3 py-2"
              value={goals}
              onChange={(event) => setGoals(event.target.value)}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            Non-goals
            <textarea
              className="min-h-24 rounded-lg border border-border bg-background px-3 py-2"
              value={nonGoals}
              onChange={(event) => setNonGoals(event.target.value)}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            First milestone
            <input
              className="h-11 rounded-lg border border-border bg-background px-3"
              value={milestoneTitle}
              onChange={(event) => setMilestoneTitle(event.target.value)}
              required
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            First task
            <input
              className="h-11 rounded-lg border border-border bg-background px-3"
              value={taskTitle}
              onChange={(event) => setTaskTitle(event.target.value)}
              required
            />
          </label>
          <button
            className="h-11 rounded-lg bg-accent text-sm font-medium text-accent-fg disabled:opacity-60"
            type="submit"
            disabled={pending}
          >
            {pending ? "Saving…" : briefSaved ? "Saved — continue" : "Save brief"}
          </button>
        </form>
      ) : null}

      {step === 4 && project ? (
        <div className="flex flex-col gap-4 rounded-2xl border border-border bg-surface p-6">
          <div className="flex gap-2">
            <button
              className={`h-9 rounded-md px-3 text-sm ${
                agentTab === "stdio" ? "bg-background font-medium" : "text-muted"
              }`}
              type="button"
              onClick={() => setAgentTab("stdio")}
            >
              stdio / beacon mcp
            </button>
            <button
              className={`h-9 rounded-md px-3 text-sm ${
                agentTab === "http" ? "bg-background font-medium" : "text-muted"
              }`}
              type="button"
              onClick={() => setAgentTab("http")}
            >
              HTTP MCP
            </button>
          </div>
          {agentTab === "stdio" ? (
            <div className="space-y-3 text-sm leading-6">
              <p>
                Local agents get context and code through{" "}
                <code className="font-mono">beacon mcp</code>.
              </p>
              {token?.token ? (
                <pre className="overflow-x-auto rounded-lg border border-border bg-background p-3 font-mono text-xs">
                  {`beacon connect ${token.token}\nbeacon mcp`}
                </pre>
              ) : (
                <pre className="overflow-x-auto rounded-lg border border-border bg-background p-3 font-mono text-xs">
                  beacon mcp
                </pre>
              )}
            </div>
          ) : (
            <p className="text-sm leading-6 text-muted">
              Control plane (context &amp; tasks). Code tools need a local sidecar, a self-host
              bind-mount, or an enabled hosted clone.
            </p>
          )}
          <button
            className="h-11 w-fit rounded-lg bg-accent px-4 text-sm font-medium text-accent-fg"
            type="button"
            onClick={finish}
          >
            Open project
          </button>
        </div>
      ) : null}
    </section>
  );
}
