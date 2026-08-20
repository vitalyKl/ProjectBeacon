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
import {
  fetchProjectLabels,
  sortLabels,
  toggleLabelId,
  type PublicLabel,
} from "@/lib/labels";
import { t } from "@/lib/i18n";
import { POST_LOGIN_PATH } from "@/lib/nav";
import { FIELD_ERROR_CLASS } from "@/lib/ui";
import { useT } from "@/lib/use-locale";

import { ORG_STORAGE_KEY, PROJECT_STORAGE_KEY, readStoredId, writeStoredId } from "../../selection";

const STEP_KEYS = [
  "wizard.stepCreate",
  "wizard.stepConnect",
  "wizard.stepDetect",
  "wizard.stepBrief",
  "wizard.stepAgent",
] as const;

type DetectStatus = "idle" | "pending" | "later" | "started";

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
  const label = useT();
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

  const [goals, setGoals] = useState(() => t("wizard.defaultGoals"));
  const [nonGoals, setNonGoals] = useState(() => t("wizard.defaultNonGoals"));
  const [stack, setStack] = useState(() => t("wizard.defaultStack"));
  const [commands, setCommands] = useState(() => t("wizard.defaultCommands"));
  const [definitionOfDone, setDefinitionOfDone] = useState(() => t("wizard.defaultDod"));
  const [milestoneTitle, setMilestoneTitle] = useState(() => t("wizard.defaultMilestone"));
  const [taskTitle, setTaskTitle] = useState(() => t("wizard.defaultTask"));
  const [milestoneId, setMilestoneId] = useState<string | null>(null);
  const [taskId, setTaskId] = useState<string | null>(null);
  const [briefSaved, setBriefSaved] = useState(false);
  const [labels, setLabels] = useState<PublicLabel[]>([]);
  const [firstTaskLabelIds, setFirstTaskLabelIds] = useState<string[]>([]);

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
          setError(caught instanceof ApiError ? caught.message : t("common.failedSession"));
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
      setError(t("wizard.selectOrg"));
      return;
    }
    setPending(true);
    setError(null);
    try {
      const created = await createOrgProject(orgId, { name: name.trim(), slug: derivedSlug });
      setProject(created);
      writeStoredId(PROJECT_STORAGE_KEY, created.id);
      try {
        setLabels(sortLabels(await fetchProjectLabels(created.id)));
      } catch {
        setLabels([]);
      }
      setStep(1);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("common.failedCreateProject"));
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
      setError(caught instanceof ApiError ? caught.message : t("wizard.failedMint"));
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
      setError(caught instanceof ApiError ? caught.message : t("wizard.failedIndex"));
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
      setError(caught instanceof ApiError ? caught.message : t("wizard.failedDetect"));
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
        { id: "stack", title: "Tech stack", body_md: stack, ordinal: 2 },
        { id: "commands", title: "Commands", body_md: commands, ordinal: 3 },
        {
          id: "definition_of_done",
          title: "Definition of Done",
          body_md: definitionOfDone,
          ordinal: 4,
        },
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
            description: t("wizard.firstTaskDescription"),
            label_ids: firstTaskLabelIds,
          });
          setTaskId(created.id);
        }
      }
      setBriefSaved(true);
      setStep(4);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("wizard.failedBrief"));
    } finally {
      setPending(false);
    }
  }

  function finish() {
    if (project) {
      writeStoredId(PROJECT_STORAGE_KEY, project.id);
    }
    router.replace(POST_LOGIN_PATH);
    router.refresh();
  }

  return (
    <section className="mx-auto flex w-full max-w-3xl flex-col gap-6">
      <header className="space-y-2">
        <p className="text-sm text-muted">
          {orgName ? `${label("wizard.newProject")} · ${orgName}` : label("wizard.newProject")}
        </p>
        <h1 className="text-2xl font-semibold tracking-tight">{label(STEP_KEYS[step] ?? "wizard.stepCreate")}</h1>
        <ol className="flex flex-wrap gap-2 text-xs text-muted">
          {STEP_KEYS.map((key, index) => (
            <li
              key={key}
              className={`rounded-full border px-2 py-1 ${
                index === step ? "border-foreground text-foreground" : "border-border"
              }`}
            >
              {index + 1}. {label(key)}
            </li>
          ))}
        </ol>
      </header>

      {error ? <p className={FIELD_ERROR_CLASS}>{error}</p> : null}

      {step === 0 ? (
        <form
          className="flex flex-col gap-4 rounded-2xl border border-border bg-surface p-6"
          onSubmit={onCreateProject}
        >
          <label className="flex flex-col gap-1 text-sm">
            {label("common.name")}
            <input
              className="h-11 rounded-lg border border-border bg-background px-3"
              value={name}
              onChange={(event) => setName(event.target.value)}
              required
              maxLength={120}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            {label("common.slug")}
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
          <p className="text-xs leading-5 text-muted">{label("wizard.starterHint")}</p>
          <button
            className="h-11 rounded-lg bg-accent text-sm font-medium text-accent-fg disabled:opacity-60"
            type="submit"
            disabled={pending || !orgId}
          >
            {pending ? label("common.creating") : label("common.createProject")}
          </button>
        </form>
      ) : null}

      {step === 1 && project ? (
        <div className="grid gap-4">
          <article className="flex flex-col gap-3 rounded-2xl border border-border bg-surface p-5">
            <h2 className="text-lg font-semibold">{label("wizard.github")}</h2>
            {githubAppEnabled ? (
              <p className="text-sm leading-6 text-muted">{label("wizard.githubLater")}</p>
            ) : (
              <p className="text-sm leading-6 text-muted">{label("wizard.githubUnavailable")}</p>
            )}
          </article>

          <article className="flex flex-col gap-3 rounded-2xl border border-border bg-surface p-5">
            <h2 className="text-lg font-semibold">{label("wizard.thisMachine")}</h2>
            <p className="text-sm leading-6 text-muted">{label("wizard.mintHint")}</p>
            <button
              className="h-10 w-fit rounded-lg bg-accent px-4 text-sm font-medium text-accent-fg disabled:opacity-60"
              type="button"
              onClick={() => void mintCliToken()}
              disabled={pending}
            >
              {token ? label("wizard.mintAnother") : label("wizard.mintCli")}
            </button>
            {token?.token ? (
              <div className="space-y-2 rounded-lg border border-dashed border-border p-3 text-sm">
                <p className="text-muted">{label("wizard.shownOnce")}</p>
                <pre className="overflow-x-auto font-mono text-xs">{token.token}</pre>
                <pre className="overflow-x-auto font-mono text-xs">
                  beacon connect {token.token}
                </pre>
              </div>
            ) : null}
          </article>

          {workspaceEnabled ? (
            <article className="flex flex-col gap-3 rounded-2xl border border-border bg-surface p-5">
              <h2 className="text-lg font-semibold">{label("wizard.indexWorkspace")}</h2>
              <p className="text-sm leading-6 text-muted">{label("wizard.indexHint")}</p>
              <label className="flex flex-col gap-1 text-sm">
                {label("common.path")}
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
                {repo ? label("wizard.connected") : label("wizard.indexPath")}
              </button>
            </article>
          ) : null}

          <div className="flex justify-end">
            <button
              className="h-11 rounded-lg bg-accent px-4 text-sm font-medium text-accent-fg"
              type="button"
              onClick={() => setStep(2)}
            >
              {label("common.continue")}
            </button>
          </div>
        </div>
      ) : null}

      {step === 2 && project ? (
        <div className="flex flex-col gap-4 rounded-2xl border border-border bg-surface p-6">
          <p className="text-sm leading-6 text-muted">{label("wizard.detectHint")}</p>
          {connectKind === "none" && !repo ? (
            <p className="rounded-lg border border-dashed border-border px-3 py-2 text-sm text-muted">
              {label("wizard.noRepo")}
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
              {repo ? label("wizard.startDetect") : label("wizard.runLater")}
            </button>
            <button
              className="h-11 rounded-lg border border-border px-4 text-sm font-medium"
              type="button"
              onClick={() => {
                setDetectStatus("later");
                setStep(3);
              }}
            >
              {label("wizard.skip")}
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
              {label("wizard.detectPending")}
            </p>
          ) : null}
          <label className="flex flex-col gap-1 text-sm">
            {label("context.section.goals")}
            <textarea
              className="min-h-24 rounded-lg border border-border bg-background px-3 py-2"
              value={goals}
              onChange={(event) => setGoals(event.target.value)}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            {label("context.section.non_goals")}
            <textarea
              className="min-h-24 rounded-lg border border-border bg-background px-3 py-2"
              value={nonGoals}
              onChange={(event) => setNonGoals(event.target.value)}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            {label("context.section.stack")}
            <textarea
              className="min-h-24 rounded-lg border border-border bg-background px-3 py-2"
              value={stack}
              onChange={(event) => setStack(event.target.value)}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            {label("context.section.commands")}
            <textarea
              className="min-h-24 rounded-lg border border-border bg-background px-3 py-2"
              value={commands}
              onChange={(event) => setCommands(event.target.value)}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            {label("context.section.definition_of_done")}
            <textarea
              className="min-h-32 rounded-lg border border-border bg-background px-3 py-2"
              value={definitionOfDone}
              onChange={(event) => setDefinitionOfDone(event.target.value)}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            {label("wizard.firstMilestone")}
            <input
              className="h-11 rounded-lg border border-border bg-background px-3"
              value={milestoneTitle}
              onChange={(event) => setMilestoneTitle(event.target.value)}
              required
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            {label("wizard.firstTask")}
            <input
              className="h-11 rounded-lg border border-border bg-background px-3"
              value={taskTitle}
              onChange={(event) => setTaskTitle(event.target.value)}
              required
            />
          </label>
          {labels.length > 0 ? (
            <fieldset className="space-y-2">
              <legend className="text-sm">{label("wizard.attachAreas")}</legend>
              <p className="text-xs leading-5 text-muted">{label("wizard.attachHint")}</p>
              <div className="flex flex-wrap gap-2">
                {labels.map((label) => (
                  <label
                    key={label.id}
                    className="flex items-center gap-1 rounded-md border border-border px-2 py-1 text-xs"
                  >
                    <input
                      type="checkbox"
                      checked={firstTaskLabelIds.includes(label.id)}
                      onChange={() =>
                        setFirstTaskLabelIds((current) => toggleLabelId(current, label.id))
                      }
                    />
                    {label.name}
                  </label>
                ))}
              </div>
            </fieldset>
          ) : null}
          <button
            className="h-11 rounded-lg bg-accent text-sm font-medium text-accent-fg disabled:opacity-60"
            type="submit"
            disabled={pending}
          >
            {pending
              ? label("common.saving")
              : briefSaved
                ? label("wizard.savedContinue")
                : label("wizard.saveBrief")}
          </button>
        </form>
      ) : null}

      {step === 4 && project ? (
        <div className="flex flex-col gap-4 rounded-2xl border border-border bg-surface p-6">
          <div className="space-y-3 text-sm leading-6">
            <p>{label("wizard.stdioHint")}</p>
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
          <button
            className="h-11 w-fit rounded-lg bg-accent px-4 text-sm font-medium text-accent-fg"
            type="button"
            onClick={finish}
          >
            {label("wizard.openProject")}
          </button>
        </div>
      ) : null}
    </section>
  );
}
