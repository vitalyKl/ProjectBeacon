"use client";

import { useEffect, useState, type FormEvent } from "react";

import { ApiError, newIdempotencyKey } from "@/lib/api";
import { fetchProjectLabels, toggleLabelId, type PublicLabel } from "@/lib/labels";
import { DEFAULT_TASK_PRIORITY } from "@/lib/priority";
import {
  createTask,
  statusLabel,
  TASK_STATUSES,
  type PublicMilestone,
  type TaskStatus,
} from "@/lib/roadmap";
import { useT } from "@/lib/use-locale";

import { PrioritySelect } from "./priority-select";

export function CreateTaskForm({
  projectId,
  milestones,
  defaultStatus,
  onCreated,
  open: openProp,
  onOpenChange,
}: {
  projectId: string;
  milestones: PublicMilestone[];
  defaultStatus: TaskStatus;
  onCreated: () => Promise<void> | void;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
}) {
  const t = useT();
  const [uncontrolledOpen, setUncontrolledOpen] = useState(false);
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [howToCheck, setHowToCheck] = useState("");
  const [status, setStatus] = useState<TaskStatus>(defaultStatus);
  const [priority, setPriority] = useState(DEFAULT_TASK_PRIORITY);
  const [milestoneId, setMilestoneId] = useState("");
  const [labelIds, setLabelIds] = useState<string[]>([]);
  const [catalog, setCatalog] = useState<PublicLabel[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const open = openProp ?? uncontrolledOpen;

  function setOpen(next: boolean) {
    onOpenChange?.(next);
    if (openProp === undefined) {
      setUncontrolledOpen(next);
    }
  }

  function reset() {
    setTitle("");
    setDescription("");
    setHowToCheck("");
    setStatus(defaultStatus);
    setPriority(DEFAULT_TASK_PRIORITY);
    setMilestoneId("");
    setLabelIds([]);
    setError(null);
  }

  useEffect(() => {
    if (!open) {
      return;
    }
    let cancelled = false;
    void fetchProjectLabels(projectId)
      .then((items) => {
        if (!cancelled) {
          setCatalog(items);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setCatalog([]);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [open, projectId]);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const nextTitle = title.trim();
    if (!nextTitle) {
      setError(t("common.titleRequired"));
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
          how_to_check: howToCheck,
          status,
          priority,
          milestone_id: milestoneId || null,
          label_ids: labelIds,
        },
        newIdempotencyKey(),
      );
      reset();
      setOpen(false);
      await onCreated();
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("task.failedCreate"));
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
        {t("task.new")}
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
        placeholder={t("common.title")}
        value={title}
        onChange={(event) => setTitle(event.target.value)}
        maxLength={200}
        required
      />
      <textarea
        className="min-h-16 rounded-md border border-border bg-background px-2 py-1 text-sm"
        placeholder={t("common.description")}
        value={description}
        onChange={(event) => setDescription(event.target.value)}
        maxLength={8000}
      />
      <textarea
        className="min-h-16 rounded-md border border-border bg-background px-2 py-1 text-sm"
        placeholder={t("task.howToCheckHint")}
        value={howToCheck}
        onChange={(event) => setHowToCheck(event.target.value)}
        maxLength={8000}
        aria-label={t("task.howToCheck")}
      />
      <div className="flex flex-wrap gap-2">
        <select
          className="h-9 rounded-md border border-border bg-background px-2 text-sm"
          aria-label={t("common.status")}
          value={status}
          onChange={(event) => setStatus(event.target.value as TaskStatus)}
        >
          {TASK_STATUSES.map((item) => (
            <option key={item} value={item}>
              {statusLabel(item)}
            </option>
          ))}
        </select>
        <PrioritySelect value={priority} onChange={setPriority} />
        <select
          className="h-9 min-w-40 rounded-md border border-border bg-background px-2 text-sm"
          aria-label={t("home.milestones")}
          value={milestoneId}
          onChange={(event) => setMilestoneId(event.target.value)}
        >
          <option value="">{t("task.noMilestone")}</option>
          {milestones.map((item) => (
            <option key={item.id} value={item.id}>
              {item.title}
            </option>
          ))}
        </select>
      </div>
      {catalog.length > 0 ? (
        <fieldset className="flex flex-wrap gap-2">
          <legend className="sr-only">{t("task.areas")}</legend>
          {catalog.map((label) => (
            <label
              key={label.id}
              className="flex items-center gap-1 rounded-md border border-border px-2 py-1 text-xs"
            >
              <input
                type="checkbox"
                checked={labelIds.includes(label.id)}
                onChange={() => setLabelIds((current) => toggleLabelId(current, label.id))}
              />
              {label.name}
              {label.status === "proposed" ? (
                <span className="text-muted">{t("task.proposed")}</span>
              ) : null}
            </label>
          ))}
        </fieldset>
      ) : null}
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      <div className="flex gap-2">
        <button
          className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
          type="submit"
          disabled={pending}
        >
          {pending ? t("common.saving") : t("common.save")}
        </button>
        <button
          className="rounded-md border border-border px-3 py-1.5 text-sm"
          type="button"
          onClick={() => {
            reset();
            setOpen(false);
          }}
        >
          {t("common.cancel")}
        </button>
      </div>
    </form>
  );
}
