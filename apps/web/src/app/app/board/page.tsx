"use client";

import { useState } from "react";

import { statusLabel, TASK_STATUSES, type PublicTask, type TaskStatus } from "@/lib/roadmap";

import { CreateTaskForm } from "../create-task-form";
import { TaskCard } from "../task-card";
import { useProjectWork } from "../use-project-work";

const BOARD_POLL_MS = 10000;

export default function BoardPage() {
  const { project, tasks, milestones, error, loading, reload, moveTask } =
    useProjectWork(BOARD_POLL_MS);
  const [draggingId, setDraggingId] = useState<string | null>(null);

  if (!project) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">Board</h1>
        <p className="text-sm text-muted">Select a project from the header.</p>
      </section>
    );
  }

  return (
    <section className="space-y-4">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Board</h1>
          <p className="text-sm text-muted">
            Drag a card to any status. A human move releases an agent lock.
          </p>
        </div>
        <CreateTaskForm
          projectId={project.id}
          milestones={milestones}
          defaultStatus="ready"
          onCreated={() => reload({ silent: true })}
        />
      </header>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {loading ? <p className="text-sm text-muted">Loading…</p> : null}
      <div className="flex min-h-[28rem] gap-3 overflow-x-auto pb-2">
        {TASK_STATUSES.map((status) => (
          <BoardColumn
            key={status}
            status={status}
            tasks={tasks.filter((task) => task.status === status)}
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
