"use client";

import Link from "next/link";
import { useCallback, useEffect, useState, type FormEvent } from "react";

import {
  ApiError,
  createOrgProject,
  fetchContextNodes,
  type ContextNode,
  type PublicRepo,
} from "@/lib/api";
import { offeredTasks, pickDisplayBriefNode, sectionsWithBody } from "@/lib/brief";
import {
  connectionLabel,
  fetchDetailedProjectRepos,
  formatIndexWhen,
  indexModeLabel,
  pickHomeIndexRepo,
} from "@/lib/index-status";
import { DEFAULT_TASK_PRIORITY, priorityLabel } from "@/lib/priority";
import {
  fetchProjectMilestones,
  fetchProjectTasks,
  statusLabel,
  type PublicMilestone,
  type PublicTask,
} from "@/lib/roadmap";
import { ensureBeaconSeed } from "@/lib/seed";
import { useInterval } from "@/lib/use-interval";
import { useT } from "@/lib/use-locale";

import { BriefBlocks } from "./brief-blocks";
import { LockBadge } from "./lock-badge";
import { useSelectedProject } from "./project-context";

const HOME_POLL_MS = 5000;

export default function AppHomePage() {
  const t = useT();
  const { org, project, loading, reloadProjects } = useSelectedProject();
  const [milestones, setMilestones] = useState<PublicMilestone[]>([]);
  const [tasks, setTasks] = useState<PublicTask[]>([]);
  const [repos, setRepos] = useState<PublicRepo[] | null>([]);
  const [briefNode, setBriefNode] = useState<ContextNode | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [ready, setReady] = useState(false);
  const projectId = project?.id ?? null;

  const reload = useCallback(async () => {
    if (!projectId) {
      return;
    }
    try {
      await ensureBeaconSeed(projectId);
      const [nextMilestones, nextTasks, nextRepos, nextNodes] = await Promise.all([
        fetchProjectMilestones(projectId),
        fetchProjectTasks(projectId),
        fetchDetailedProjectRepos(projectId),
        fetchContextNodes(projectId),
      ]);
      setMilestones(nextMilestones);
      setTasks(nextTasks);
      setRepos(nextRepos);
      setBriefNode(pickDisplayBriefNode(nextNodes));
      setError(null);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("common.failedLoadHome"));
    } finally {
      setReady(true);
    }
  }, [projectId, t]);

  useEffect(() => {
    if (!projectId) {
      const id = window.setTimeout(() => {
        setMilestones([]);
        setTasks([]);
        setRepos([]);
        setBriefNode(null);
        setReady(true);
      }, 0);
      return () => window.clearTimeout(id);
    }
    const selectedId = projectId;
    let cancelled = false;
    async function load() {
      try {
        await ensureBeaconSeed(selectedId);
        if (cancelled) {
          return;
        }
        const [nextMilestones, nextTasks, nextRepos, nextNodes] = await Promise.all([
          fetchProjectMilestones(selectedId),
          fetchProjectTasks(selectedId),
          fetchDetailedProjectRepos(selectedId),
          fetchContextNodes(selectedId),
        ]);
        if (cancelled) {
          return;
        }
        setMilestones(nextMilestones);
        setTasks(nextTasks);
        setRepos(nextRepos);
        setBriefNode(pickDisplayBriefNode(nextNodes));
        setError(null);
      } catch (caught) {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : t("common.failedLoadHome"));
        }
      } finally {
        if (!cancelled) {
          setReady(true);
        }
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [projectId]);

  useInterval(
    () => {
      void reload();
    },
    projectId ? HOME_POLL_MS : null,
  );

  if (loading) {
    return <p className="text-sm text-muted">{t("common.loading")}</p>;
  }

  if (!org) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">{t("nav.home")}</h1>
        <p className="text-sm text-muted">{t("common.selectOrg")}</p>
      </section>
    );
  }

  if (!project) {
    return <FirstProjectForm orgId={org.id} onCreated={reloadProjects} />;
  }

  const openMilestones = milestones.filter((item) => item.status === "open");
  const locked = tasks.filter((task) => Boolean(task.locked_by_session_id && task.lock_expires_at));
  const inFlight = tasks.filter(
    (task) => task.status === "in_progress" || task.status === "in_review",
  );
  const offered = offeredTasks(tasks);
  const briefSections = briefNode ? sectionsWithBody(briefNode.sections) : [];
  const indexRepo = pickHomeIndexRepo(repos, project.default_repo_id);

  return (
    <section className="space-y-6">
      <header className="space-y-2">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="space-y-1">
            <h1 className="text-2xl font-semibold tracking-tight">{t("nav.home")}</h1>
            <p className="text-sm text-muted">{project.name}</p>
          </div>
          <div className="flex flex-wrap gap-2">
            <Link
              className="rounded-md border border-border bg-surface px-3 py-1.5 text-sm"
              href="/app/learn"
            >
              {t("nav.learn")}
            </Link>
            <Link
              className="rounded-md border border-border bg-surface px-3 py-1.5 text-sm"
              href="/app/context"
            >
              {t("common.compileBrief")}
            </Link>
          </div>
        </div>
        {project.description.trim() ? (
          <p className="max-w-3xl text-sm leading-6">{project.description}</p>
        ) : null}
        <p className="max-w-3xl text-sm leading-6 text-muted">
          {t("home.newHereBefore")}{" "}
          <Link className="underline underline-offset-2" href="/app/learn">
            {t("nav.learn")}
          </Link>{" "}
          {t("home.newHereAfter")}
        </p>
      </header>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {!ready ? <p className="text-sm text-muted">{t("common.loading")}</p> : null}

      <article className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-sm font-semibold tracking-wide uppercase">{t("home.brief")}</h2>
          <Link className="text-sm underline" href="/app/context">
            {t("home.editInContext")}
          </Link>
        </div>
        {briefSections.length === 0 ? (
          <p className="text-sm text-muted">{t("home.noBrief")}</p>
        ) : (
          <BriefBlocks sections={briefSections} />
        )}
      </article>

      <div className="grid gap-4 lg:grid-cols-2">
        <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
          <h2 className="text-sm font-semibold tracking-wide uppercase">{t("home.milestones")}</h2>
          {openMilestones.length === 0 ? (
            <p className="text-sm text-muted">{t("home.noMilestones")}</p>
          ) : (
            <ul className="space-y-2">
              {openMilestones.map((item) => (
                <li key={item.id} className="text-sm">
                  <p className="font-medium">{item.title}</p>
                  {item.target_date ? (
                    <p className="text-xs text-muted">{item.target_date}</p>
                  ) : null}
                </li>
              ))}
            </ul>
          )}
        </article>

        <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
          <h2 className="text-sm font-semibold tracking-wide uppercase">{t("index.title")}</h2>
          <dl className="grid grid-cols-2 gap-2 text-sm">
            <dt className="text-muted">{t("index.mode")}</dt>
            <dd>{indexModeLabel(indexRepo?.index_mode)}</dd>
            <dt className="text-muted">{t("index.sidecarLabel")}</dt>
            <dd className="capitalize">{connectionLabel(indexRepo?.sidecar_connected)}</dd>
            <dt className="text-muted">{t("index.worker")}</dt>
            <dd className="capitalize">{connectionLabel(indexRepo?.worker_index_connected)}</dd>
            <dt className="text-muted">{t("index.lastIndexed")}</dt>
            <dd>{indexRepo ? formatIndexWhen(indexRepo.last_indexed_at) : "—"}</dd>
          </dl>
        </article>
      </div>

      <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
        <div className="flex items-center justify-between">
          <h2 className="text-sm font-semibold tracking-wide uppercase">{t("home.ready")}</h2>
          <Link className="text-sm underline" href="/app/agents">
            {t("nav.agents")}
          </Link>
        </div>
        {offered.length === 0 ? (
          <p className="text-sm text-muted">{t("home.noReady")}</p>
        ) : (
          <ul className="space-y-2">
            {offered.map((task) => (
              <li key={task.id}>
                <Link
                  className="flex items-center justify-between gap-3 text-sm"
                  href={`/app/tasks/${task.id}`}
                >
                  <span>{task.title}</span>
                  <span className="text-xs text-muted capitalize">
                    {statusLabel(task.status)}
                    {task.priority !== DEFAULT_TASK_PRIORITY
                      ? ` · ${priorityLabel(task.priority)}`
                      : ""}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </article>

      <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
        <div className="flex items-center justify-between">
          <h2 className="text-sm font-semibold tracking-wide uppercase">{t("home.inFlight")}</h2>
          <Link className="text-sm underline" href="/app/board">
            {t("nav.board")}
          </Link>
        </div>
        {inFlight.length === 0 && locked.length === 0 ? (
          <p className="text-sm text-muted">{t("home.noInFlight")}</p>
        ) : (
          <ul className="space-y-2">
            {[
              ...inFlight,
              ...locked.filter((task) => !inFlight.some((item) => item.id === task.id)),
            ].map((task) => (
              <li key={task.id}>
                <Link
                  className="flex items-center justify-between gap-3 text-sm"
                  href={`/app/tasks/${task.id}`}
                >
                  <span>{task.title}</span>
                  <span className="flex items-center gap-2 text-xs text-muted capitalize">
                    {statusLabel(task.status)}
                    <LockBadge task={task} />
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </article>
    </section>
  );
}

function FirstProjectForm({ orgId, onCreated }: { orgId: string; onCreated: () => Promise<void> }) {
  const t = useT();
  const [name, setName] = useState("Beacon");
  const [slug, setSlug] = useState("beacon");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPending(true);
    setError(null);
    try {
      await createOrgProject(orgId, { name: name.trim(), slug: slug.trim() });
      await onCreated();
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("common.failedCreateProject"));
    } finally {
      setPending(false);
    }
  }

  return (
    <section className="space-y-4">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight">{t("nav.home")}</h1>
        <p className="max-w-xl text-sm leading-6 text-muted">{t("home.welcome")}</p>
      </header>
      <form className="flex max-w-md flex-col gap-3" onSubmit={onSubmit}>
        <label className="flex flex-col gap-1 text-sm">
          {t("common.name")}
          <input
            className="h-9 rounded-md border border-border bg-background px-2"
            value={name}
            onChange={(event) => setName(event.target.value)}
            required
            maxLength={120}
          />
        </label>
        <label className="flex flex-col gap-1 text-sm">
          {t("common.slug")}
          <input
            className="h-9 rounded-md border border-border bg-background px-2 font-mono"
            value={slug}
            onChange={(event) => setSlug(event.target.value)}
            required
            maxLength={64}
          />
        </label>
        {error ? <p className="text-sm text-red-600">{error}</p> : null}
        <button
          className="h-10 rounded-md bg-accent text-sm font-medium text-accent-fg disabled:opacity-60"
          type="submit"
          disabled={pending}
        >
          {pending ? t("common.creating") : t("common.createProject")}
        </button>
      </form>
    </section>
  );
}
