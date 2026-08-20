"use client";

import Link from "next/link";

import { DEFAULT_TASK_PRIORITY, priorityLabel } from "@/lib/priority";
import { statusLabel, type PublicTask } from "@/lib/roadmap";
import { taskDetailHref } from "@/lib/task-detail";

import { LockBadge } from "./lock-badge";

export function TaskCard({
  task,
  from,
  draggable = false,
  onDragStart,
  onDragEnd,
}: {
  task: PublicTask;
  from?: "backlog";
  draggable?: boolean;
  onDragStart?: (task: PublicTask) => void;
  onDragEnd?: () => void;
}) {
  return (
    <Link
      href={taskDetailHref(task.id, from)}
      className="block rounded-md border border-border bg-surface px-3 py-2 text-sm shadow-sm hover:border-foreground/20"
      draggable={draggable}
      onDragStart={(event) => {
        if (!draggable) {
          return;
        }
        event.dataTransfer.setData("text/task-id", task.id);
        event.dataTransfer.effectAllowed = "move";
        onDragStart?.(task);
      }}
      onDragEnd={() => onDragEnd?.()}
    >
      <div className="flex items-start justify-between gap-2">
        <p className="font-medium leading-5">{task.title}</p>
        <LockBadge task={task} />
      </div>
      <p className="mt-1 text-xs text-muted capitalize">
        {statusLabel(task.status)}
        {task.priority !== DEFAULT_TASK_PRIORITY ? ` · ${priorityLabel(task.priority)}` : ""}
      </p>
      {(task.labels ?? []).length > 0 ? (
        <ul className="mt-2 flex flex-wrap gap-1">
          {(task.labels ?? []).map((label) => (
            <li
              key={label.id}
              className="rounded-full border border-border px-2 py-0.5 text-[10px] tracking-wide uppercase"
              style={label.color ? { borderColor: label.color, color: label.color } : undefined}
            >
              {label.name}
            </li>
          ))}
        </ul>
      ) : null}
    </Link>
  );
}
