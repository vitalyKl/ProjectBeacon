"use client";

import { useState, type FormEvent } from "react";

import { ApiError, newIdempotencyKey } from "@/lib/api";
import { createTask, TASK_STATUSES, type PublicMilestone, type TaskStatus } from "@/lib/roadmap";

export function CreateTaskForm({
  projectId,
  milestones,
  defaultStatus,
  onCreated,
}: {
  projectId: string;
  milestones: PublicMilestone[];
  defaultStatus: TaskStatus;
  onCreated: () => Promise<void> | void;
}) {
  const [open, setOpen] = useState(false);
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [status, setStatus] = useState<TaskStatus>(defaultStatus);
  const [milestoneId, setMilestoneId] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  function reset() {
    setTitle("");
    setDescription("");
    setStatus(defaultStatus);
    setMilestoneId("");
    setError(null);
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const nextTitle = title.trim();
    if (!nextTitle) {
      setError("title is required");
      return;
    }
    setPending(true);
    setError(null);
    try {
      await createTask(
        projectId,
        {
          title: nextTitle,
          description,
          status,
          milestone_id: milestoneId || null,
        },
        newIdempotencyKey(),
      );
      reset();
      setOpen(false);
      await onCreated();
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "failed to create task");
    } finally {
      setPending(false);
    }
  }

  if (!open) {
    return (
      <button
        className="rounded-md border border-border px-3 py-1.5 text-sm"
        type="button"
        onClick={() => {
          reset();
          setOpen(true);
        }}
      >
        New task
      </button>
    );
  }

  return (
    <form
      className="flex max-w-xl flex-col gap-2 rounded-md border border-border bg-surface p-3"
      onSubmit={onSubmit}
    >
      <input
        className="h-9 rounded-md border border-border bg-background px-2 text-sm"
        placeholder="Title"
        value={title}
        onChange={(event) => setTitle(event.target.value)}
        maxLength={200}
        required
      />
      <textarea
        className="min-h-16 rounded-md border border-border bg-background px-2 py-1 text-sm"
        placeholder="Description"
        value={description}
        onChange={(event) => setDescription(event.target.value)}
        maxLength={8000}
      />
      <div className="flex flex-wrap gap-2">
        <select
          className="h-9 rounded-md border border-border bg-background px-2 text-sm"
          aria-label="Status"
          value={status}
          onChange={(event) => setStatus(event.target.value as TaskStatus)}
        >
          {TASK_STATUSES.map((item) => (
            <option key={item} value={item}>
              {item.replaceAll("_", " ")}
            </option>
          ))}
        </select>
        <select
          className="h-9 min-w-40 rounded-md border border-border bg-background px-2 text-sm"
          aria-label="Milestone"
          value={milestoneId}
          onChange={(event) => setMilestoneId(event.target.value)}
        >
          <option value="">No milestone</option>
          {milestones.map((item) => (
            <option key={item.id} value={item.id}>
              {item.title}
            </option>
          ))}
        </select>
      </div>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      <div className="flex gap-2">
        <button
          className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
          type="submit"
          disabled={pending}
        >
          {pending ? "Saving…" : "Save"}
        </button>
        <button
          className="rounded-md border border-border px-3 py-1.5 text-sm"
          type="button"
          onClick={() => {
            reset();
            setOpen(false);
          }}
        >
          Cancel
        </button>
      </div>
    </form>
  );
}
