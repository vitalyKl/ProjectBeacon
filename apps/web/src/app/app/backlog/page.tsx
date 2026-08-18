"use client";

import { statusLabel, TASK_STATUSES, type TaskStatus } from "@/lib/roadmap";

import { CreateTaskForm } from "../create-task-form";
import { TaskCard } from "../task-card";
import { useProjectWork } from "../use-project-work";

const BACKLOG_POLL_MS = 10000;

export default function BacklogPage() {
  const { project, tasks, milestones, error, loading, reload, moveTask } =
    useProjectWork(BACKLOG_POLL_MS);

  if (!project) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">Backlog</h1>
        <p className="text-sm text-muted">Select a project from the header.</p>
      </section>
    );
  }

  const sorted = [...tasks].sort((a, b) => {
    const statusDelta = TASK_STATUSES.indexOf(a.status) - TASK_STATUSES.indexOf(b.status);
    if (statusDelta !== 0) {
      return statusDelta;
    }
    return a.title.localeCompare(b.title);
  });

  return (
    <section className="space-y-4">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Backlog</h1>
          <p className="text-sm text-muted">List view. Change status from any row.</p>
        </div>
        <CreateTaskForm
          projectId={project.id}
          milestones={milestones}
          defaultStatus="backlog"
          onCreated={() => reload({ silent: true })}
        />
      </header>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {loading ? <p className="text-sm text-muted">Loading…</p> : null}
      {sorted.length === 0 && !loading ? (
        <p className="text-sm text-muted">No tasks yet. Create one to start the board.</p>
      ) : (
        <ul className="divide-y divide-border rounded-lg border border-border bg-surface">
          {sorted.map((task) => (
            <li key={task.id} className="flex flex-wrap items-center gap-3 px-3 py-2">
              <div className="min-w-0 flex-1">
                <TaskCard task={task} />
              </div>
              <label className="flex items-center gap-2 text-xs text-muted">
                Status
                <select
                  className="h-8 rounded-md border border-border bg-background px-2 text-sm capitalize"
                  aria-label={`Status for ${task.title}`}
                  value={task.status}
                  onChange={(event) => void moveTask(task.id, event.target.value as TaskStatus)}
                >
                  {TASK_STATUSES.map((status) => (
                    <option key={status} value={status}>
                      {statusLabel(status)}
                    </option>
                  ))}
                </select>
              </label>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
