"use client";

import { tf } from "@/lib/i18n";
import { sortTasksByPriority } from "@/lib/priority";
import { statusLabel, TASK_STATUSES, type TaskStatus } from "@/lib/roadmap";
import { useT } from "@/lib/use-locale";

import { CreateTaskForm } from "../create-task-form";
import { TaskCard } from "../task-card";
import { useProjectWork } from "../use-project-work";
import { taskMatchesArea, useWorkFilters } from "../use-work-filters";
import { WorkHeader } from "../work-header";

const BACKLOG_POLL_MS = 10000;

export default function BacklogPage() {
  const t = useT();
  const { project, tasks, milestones, error, loading, reload, moveTask } =
    useProjectWork(BACKLOG_POLL_MS);
  const { labelId, setLabelId, catalog } = useWorkFilters(project?.id ?? null);

  if (!project) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">{t("nav.backlog")}</h1>
        <p className="text-sm text-muted">{t("common.selectProject")}</p>
      </section>
    );
  }

  const sorted = TASK_STATUSES.flatMap((status) =>
    sortTasksByPriority(
      tasks.filter((task) => task.status === status && taskMatchesArea(task, labelId)),
    ),
  );

  return (
    <section className="space-y-4">
      <WorkHeader
        surface="backlog"
        title={t("nav.backlog")}
        description={t("backlog.hint")}
        catalog={catalog}
        labelId={labelId}
        onLabelIdChange={setLabelId}
        actions={
          <CreateTaskForm
            projectId={project.id}
            milestones={milestones}
            defaultStatus="backlog"
            onCreated={() => reload({ silent: true })}
          />
        }
      />
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {loading ? <p className="text-sm text-muted">{t("common.loading")}</p> : null}
      {sorted.length === 0 && !loading ? (
        <p className="text-sm text-muted">{t("backlog.empty")}</p>
      ) : (
        <ul className="divide-y divide-border rounded-lg border border-border bg-surface">
          {sorted.map((task) => (
            <li key={task.id} className="flex flex-wrap items-center gap-3 px-3 py-2">
              <div className="min-w-0 flex-1">
                <TaskCard task={task} from="backlog" />
              </div>
              <label className="flex items-center gap-2 text-xs text-muted">
                {t("common.status")}
                <select
                  className="h-8 rounded-md border border-border bg-background px-2 text-sm capitalize"
                  aria-label={tf("backlog.statusFor", { title: task.title })}
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
