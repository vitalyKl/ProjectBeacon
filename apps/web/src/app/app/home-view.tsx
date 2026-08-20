"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";

import {
  ApiError,
  fetchContextNodes,
  fetchProjectActivity,
  fetchProjectSessions,
  type ContextNode,
  type PublicActivityEvent,
  type PublicAgentSession,
  type PublicRepo,
} from "@/lib/api";
import { pickDisplayBriefNode } from "@/lib/brief";
import {
  countActiveSessions,
  countHomeQueue,
  countOpenMilestones,
  lastActivityEvent,
  peekReadyTasks,
  pickPulseBriefSections,
} from "@/lib/home-pulse";
import { activityVerbLabel } from "@/lib/i18n";
import { fetchDetailedProjectRepos, pickHomeIndexRepo } from "@/lib/index-status";
import { NEW_PROJECT_PATH } from "@/lib/nav";
import { LIVE_POLL_MS } from "@/lib/poll";
import { DEFAULT_TASK_PRIORITY, priorityLabel } from "@/lib/priority";
import {
  fetchProjectMilestones,
  fetchProjectTasks,
  statusLabel,
  type PublicMilestone,
  type PublicTask,
} from "@/lib/roadmap";
import { ensureBeaconSeed } from "@/lib/seed";
import { BUTTON_VARIANT_CLASS, FIELD_ERROR_CLASS } from "@/lib/ui";
import { EmptyState } from "@/lib/ui/empty-state";
import { useInterval } from "@/lib/use-interval";
import { useT, useTf } from "@/lib/use-locale";

import { BriefBlocks } from "./brief-blocks";
import { IndexStatusCard } from "./index-status-card";
import { useSelectedProject } from "./project-context";

export function HomeView({ hostedClone }: { hostedClone: boolean }) {
  const t = useT();
  const format = useTf();
  const { org, project, loading } = useSelectedProject();
  const [milestones, setMilestones] = useState<PublicMilestone[]>([]);
  const [tasks, setTasks] = useState<PublicTask[]>([]);
  const [repos, setRepos] = useState<PublicRepo[] | null>([]);
  const [briefNode, setBriefNode] = useState<ContextNode | null>(null);
  const [sessions, setSessions] = useState<PublicAgentSession[]>([]);
  const [activity, setActivity] = useState<PublicActivityEvent[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [ready, setReady] = useState(false);
  const projectId = project?.id ?? null;
  const requestSeq = useRef(0);

  const reload = useCallback(async () => {
    if (!projectId) {
      return;
    }
    const seq = ++requestSeq.current;
    const selectedId = projectId;
    try {
      await ensureBeaconSeed(selectedId);
      if (seq !== requestSeq.current) {
        return;
      }
      const [nextMilestones, nextTasks, nextRepos, nextNodes, nextSessions, nextActivity] =
        await Promise.all([
          fetchProjectMilestones(selectedId),
          fetchProjectTasks(selectedId),
          fetchDetailedProjectRepos(selectedId),
          fetchContextNodes(selectedId),
          fetchProjectSessions(selectedId),
          fetchProjectActivity(selectedId),
        ]);
      if (seq !== requestSeq.current) {
        return;
      }
      setMilestones(nextMilestones);
      setTasks(nextTasks);
      setRepos(nextRepos);
      setBriefNode(pickDisplayBriefNode(nextNodes));
      setSessions(nextSessions);
      setActivity(nextActivity);
      setError(null);
    } catch (caught) {
      if (seq === requestSeq.current) {
        setError(caught instanceof ApiError ? caught.message : t("common.failedLoadHome"));
      }
    } finally {
      if (seq === requestSeq.current) {
        setReady(true);
      }
    }
  }, [projectId, t]);

  useEffect(() => {
    if (!projectId) {
      requestSeq.current += 1;
      const id = window.setTimeout(() => {
        setMilestones([]);
        setTasks([]);
        setRepos([]);
        setBriefNode(null);
        setSessions([]);
        setActivity([]);
        setReady(true);
      }, 0);
      return () => window.clearTimeout(id);
    }
    const id = window.setTimeout(() => {
      void reload();
    }, 0);
    return () => {
      window.clearTimeout(id);
      requestSeq.current += 1;
    };
  }, [projectId, reload]);

  useInterval(
    () => {
      void reload();
    },
    projectId ? LIVE_POLL_MS : null,
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
    return (
      <section className="space-y-4">
        <h1 className="text-2xl font-semibold tracking-tight">{t("nav.home")}</h1>
        <EmptyState
          description={t("home.welcome")}
          action={
            <Link className={BUTTON_VARIANT_CLASS.primary} href={NEW_PROJECT_PATH}>
              {t("home.emptyAction")}
            </Link>
          }
        />
      </section>
    );
  }

  const pulseSections = briefNode ? pickPulseBriefSections(briefNode.sections) : [];
  const queue = countHomeQueue(tasks);
  const readyPeek = peekReadyTasks(tasks);
  const openMilestoneCount = countOpenMilestones(milestones);
  const activeAgents = countActiveSessions(sessions);
  const lastEvent = lastActivityEvent(activity);
  const indexRepo = pickHomeIndexRepo(repos, project.default_repo_id);

  return (
    <section className="space-y-6">
      <header className="space-y-2">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="space-y-1">
            <h1 className="text-2xl font-semibold tracking-tight">{t("nav.home")}</h1>
            <p className="text-sm text-muted">{project.name}</p>
          </div>
          <Link
            className="rounded-md border border-border bg-surface px-3 py-1.5 text-sm"
            href="/app/learn"
          >
            {t("nav.learn")}
          </Link>
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
      {error ? <p className={FIELD_ERROR_CLASS}>{error}</p> : null}
      {!ready ? <p className="text-sm text-muted">{t("common.loading")}</p> : null}

      <article className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-sm font-semibold tracking-wide uppercase">{t("home.brief")}</h2>
          <Link className="text-sm underline" href="/app/context">
            {t("home.editInContext")}
          </Link>
        </div>
        {pulseSections.length === 0 ? (
          <p className="text-sm text-muted">{t("home.noBrief")}</p>
        ) : (
          <BriefBlocks sections={pulseSections} />
        )}
      </article>

      <div className="grid gap-4 lg:grid-cols-2">
        <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold tracking-wide uppercase">{t("home.queue")}</h2>
            <Link className="text-sm underline" href="/app/board">
              {t("home.queueOpen")}
            </Link>
          </div>
          <dl className="grid grid-cols-3 gap-2 text-sm">
            <div>
              <dt className="text-muted">{t("home.queueReady")}</dt>
              <dd className="font-medium">{queue.ready}</dd>
            </div>
            <div>
              <dt className="text-muted">{t("home.queueInProgress")}</dt>
              <dd className="font-medium">{queue.inProgress}</dd>
            </div>
            <div>
              <dt className="text-muted">{t("home.queueInReview")}</dt>
              <dd className="font-medium">{queue.inReview}</dd>
            </div>
          </dl>
          {readyPeek.length === 0 ? (
            <p className="text-sm text-muted">{t("home.noReady")}</p>
          ) : (
            <ul className="space-y-2">
              {readyPeek.map((task) => (
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
          <h2 className="text-sm font-semibold tracking-wide uppercase">{t("home.milestones")}</h2>
          {openMilestoneCount === 0 ? (
            <p className="text-sm text-muted">{t("home.noMilestones")}</p>
          ) : (
            <Link className="text-sm font-medium underline" href="/app/roadmap">
              {format("home.openMilestones", { count: String(openMilestoneCount) })}
            </Link>
          )}
        </article>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold tracking-wide uppercase">{t("home.agents")}</h2>
            <Link className="text-sm underline" href="/app/agents">
              {t("nav.agents")}
            </Link>
          </div>
          <p className="text-sm font-medium">
            {format("home.agentsActive", { count: String(activeAgents) })}
          </p>
          {lastEvent ? (
            <p className="text-sm text-muted">
              {format("home.agentsLast", { verb: activityVerbLabel(lastEvent.verb) })}
            </p>
          ) : (
            <p className="text-sm text-muted">{t("home.noActivity")}</p>
          )}
        </article>

        <IndexStatusCard repo={indexRepo} hostedClone={hostedClone} />
      </div>
    </section>
  );
}
