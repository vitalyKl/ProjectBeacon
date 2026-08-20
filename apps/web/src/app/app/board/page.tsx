"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";

import { isNewTaskQuery } from "@/lib/command-palette";
import { sortTasksByPriority } from "@/lib/priority";
import { statusLabel, TASK_STATUSES, type PublicTask, type TaskStatus } from "@/lib/roadmap";
import { useT } from "@/lib/use-locale";

import { CreateTaskForm } from "../create-task-form";
import { TaskCard } from "../task-card";
import { useProjectWork } from "../use-project-work";
import { taskMatchesArea, useWorkFilters } from "../use-work-filters";
import { WorkHeader } from "../work-header";

const BOARD_POLL_MS = 10000;

export default function BoardPage() {
  return (
    <Suspense fallback={<BoardFallback />}>
      <BoardView />
    </Suspense>
  );
}

function BoardFallback() {
  const t = useT();
  return <p className="text-sm text-muted">{t("common.loading")}</p>;
}

function BoardView() {
  const t = useT();
  const router = useRouter();
  const searchParams = useSearchParams();
  const { project, tasks, milestones, error, loading, reload, moveTask } =
    useProjectWork(BOARD_POLL_MS);
  const { labelId, setLabelId, catalog } = useWorkFilters(project?.id ?? null);
  const [draggingId, setDraggingId] = useState<string | null>(null);
  const [createOpen, setCreateOpen] = useState(() => isNewTaskQuery(searchParams.toString()));

  useEffect(() => {
    if (!isNewTaskQuery(searchParams.toString())) {
      return;
    }
    const id = window.setTimeout(() => {
      if (project) {
        setCreateOpen(true);
      }
      router.replace("/app/board", { scroll: false });
    }, 0);
    return () => window.clearTimeout(id);
  }, [project, router, searchParams]);

  if (!project) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">{t("nav.board")}</h1>
        <p className="text-sm text-muted">{t("common.selectProject")}</p>
      </section>
    );
  }

  return (
    <section className="space-y-4">
      <WorkHeader
        surface="board"
        title={t("nav.board")}
        description={t("board.hint")}
        catalog={catalog}
        labelId={labelId}
        onLabelIdChange={setLabelId}
        actions={
          <CreateTaskForm
            projectId={project.id}
            milestones={milestones}
            defaultStatus="ready"
            open={createOpen}
            onOpenChange={setCreateOpen}
            onCreated={() => reload({ silent: true })}
          />
        }
      />
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {loading ? <p className="text-sm text-muted">{t("common.loading")}</p> : null}
      <div className="flex min-h-[28rem] gap-3 overflow-x-auto pb-2">
        {TASK_STATUSES.map((status) => (
          <BoardColumn
            key={status}
            status={status}
            tasks={sortTasksByPriority(
              tasks.filter((task) => task.status === status && taskMatchesArea(task, labelId)),
            )}
            active={draggingId !== null}
            onDropTask={(taskId) => {
              setDraggingId(null);
              void moveTask(taskId, status);
            }}
            onDragStart={(taskId) => setDraggingId(taskId)}
            onDragEnd={() => setDraggingId(null)}
          />
        ))}
      </div>
    </section>
  );
}

function BoardColumn({
  status,
  tasks,
  active,
  onDropTask,
  onDragStart,
  onDragEnd,
}: {
  status: TaskStatus;
  tasks: PublicTask[];
  active: boolean;
  onDropTask: (taskId: string) => void;
  onDragStart: (taskId: string) => void;
  onDragEnd: () => void;
}) {
  return (
    <div
      className={`flex w-64 shrink-0 flex-col gap-2 rounded-lg border bg-background p-2 ${
        active ? "border-dashed border-foreground/40" : "border-border"
      }`}
      onDragOver={(event) => {
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
      }}
      onDrop={(event) => {
        event.preventDefault();
        const taskId = event.dataTransfer.getData("text/task-id");
        if (taskId) {
          onDropTask(taskId);
        }
      }}
    >
      <div className="flex items-center justify-between px-1 py-1">
        <h2 className="text-xs font-semibold tracking-wide text-muted uppercase">
          {statusLabel(status)}
        </h2>
        <span className="text-xs text-muted">{tasks.length}</span>
      </div>
      <div className="flex flex-col gap-2">
        {tasks.map((task) => (
          <TaskCard
            key={task.id}
            task={task}
            draggable
            onDragStart={(item) => onDragStart(item.id)}
            onDragEnd={onDragEnd}
          />
        ))}
      </div>
    </div>
  );
}
