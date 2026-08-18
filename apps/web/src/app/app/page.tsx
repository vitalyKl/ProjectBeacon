"use client";

import Link from "next/link";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";

import { ApiError, createOrgProject } from "@/lib/api";
import {
  fetchProjectMilestones,
  fetchProjectTasks,
  isTaskLocked,
  statusLabel,
  type PublicMilestone,
  type PublicTask,
} from "@/lib/roadmap";
import { ensureBeaconSeed } from "@/lib/seed";
import { useInterval } from "@/lib/use-interval";
import { useNow } from "@/lib/use-now";

import { LockBadge } from "./lock-badge";
import { useSelectedProject } from "./project-context";

const HOME_POLL_MS = 5000;

export default function AppHomePage() {
  const { org, project, loading, reloadProjects } = useSelectedProject();
  const [milestones, setMilestones] = useState<PublicMilestone[]>([]);
  const [tasks, setTasks] = useState<PublicTask[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [ready, setReady] = useState(false);
  const projectId = project?.id ?? null;
  const requestSeq = useRef(0);
  const now = useNow(1000);

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
      const [nextMilestones, nextTasks] = await Promise.all([
        fetchProjectMilestones(selectedId),
        fetchProjectTasks(selectedId),
      ]);
      if (seq !== requestSeq.current) {
        return;
      }
      setMilestones(nextMilestones);
      setTasks(nextTasks);
      setError(null);
    } catch (caught) {
      if (seq === requestSeq.current) {
        setError(caught instanceof ApiError ? caught.message : "failed to load home");
      }
    } finally {
      if (seq === requestSeq.current) {
        setReady(true);
      }
    }
  }, [projectId]);

  useEffect(() => {
    if (!projectId) {
      const id = window.setTimeout(() => {
        setMilestones([]);
        setTasks([]);
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
    projectId ? HOME_POLL_MS : null,
  );

  if (loading) {
    return <p className="text-sm text-muted">Loading…</p>;
  }

  if (!org) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">Home</h1>
        <p className="text-sm text-muted">Select an org to continue.</p>
      </section>
    );
  }

  if (!project) {
    return <FirstProjectForm orgId={org.id} onCreated={reloadProjects} />;
  }

  const openMilestones = milestones.filter((item) => item.status === "open");
  const locked = tasks.filter((task) => isTaskLocked(task, now));
  const inFlight = tasks.filter(
    (task) => task.status === "in_progress" || task.status === "in_review",
  );

  return (
    <section className="space-y-6">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight">Home</h1>
        <p className="text-sm text-muted">{project.name}</p>
      </header>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {!ready ? <p className="text-sm text-muted">Loading…</p> : null}

      <div className="grid gap-4 lg:grid-cols-2">
        <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
          <h2 className="text-sm font-semibold tracking-wide uppercase">Milestones</h2>
          {openMilestones.length === 0 ? (
            <p className="text-sm text-muted">No open milestones yet.</p>
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
          <h2 className="text-sm font-semibold tracking-wide uppercase">Index</h2>
          <dl className="grid grid-cols-2 gap-2 text-sm">
            <dt className="text-muted">Mode</dt>
            <dd>Not connected</dd>
            <dt className="text-muted">Sidecar</dt>
            <dd>Offline</dd>
            <dt className="text-muted">Worker</dt>
            <dd>Offline</dd>
            <dt className="text-muted">Last indexed</dt>
            <dd>—</dd>
          </dl>
        </article>
      </div>

      <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
        <div className="flex items-center justify-between">
          <h2 className="text-sm font-semibold tracking-wide uppercase">In flight</h2>
          <Link className="text-sm underline" href="/app/board">
            Board
          </Link>
        </div>
        {inFlight.length === 0 && locked.length === 0 ? (
          <p className="text-sm text-muted">
            Nothing in progress. Open the backlog to pick up work.
          </p>
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
      setError(caught instanceof ApiError ? caught.message : "failed to create project");
    } finally {
      setPending(false);
    }
  }

  return (
    <section className="space-y-4">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight">Home</h1>
        <p className="max-w-xl text-sm leading-6 text-muted">
          Create a project to start the board. Beacon will add a first milestone and a few starter
          tasks.
        </p>
      </header>
      <form className="flex max-w-md flex-col gap-3" onSubmit={onSubmit}>
        <label className="flex flex-col gap-1 text-sm">
          Name
          <input
            className="h-9 rounded-md border border-border bg-background px-2"
            value={name}
            onChange={(event) => setName(event.target.value)}
            required
            maxLength={120}
          />
        </label>
        <label className="flex flex-col gap-1 text-sm">
          Slug
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
          {pending ? "Creating…" : "Create project"}
        </button>
      </form>
    </section>
  );
}
