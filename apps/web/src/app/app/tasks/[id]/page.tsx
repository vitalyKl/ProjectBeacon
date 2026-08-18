"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";

import { ApiError, newIdempotencyKey } from "@/lib/api";
import {
  compileTaskBrief,
  createTaskComment,
  fetchTask,
  fetchTaskActivity,
  fetchTaskComments,
  patchTask,
  setTaskStatus,
  statusLabel,
  TASK_STATUSES,
  taskFromConflict,
  type PublicActivity,
  type PublicComment,
  type PublicTask,
  type SessionBriefPreview,
  type TaskStatus,
} from "@/lib/roadmap";
import { useInterval } from "@/lib/use-interval";

import { LockBadge } from "../../lock-badge";
import { useToast } from "../../toast";

const DETAIL_POLL_MS = 10000;

export default function TaskDetailPage() {
  const params = useParams<{ id: string }>();
  const taskId = params.id;
  const { toast } = useToast();
  const [task, setTask] = useState<PublicTask | null>(null);
  const [comments, setComments] = useState<PublicComment[]>([]);
  const [activity, setActivity] = useState<PublicActivity[]>([]);
  const [brief, setBrief] = useState<SessionBriefPreview | null>(null);
  const [briefError, setBriefError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [saving, setSaving] = useState(false);
  const [commentBody, setCommentBody] = useState("");
  const [commentPending, setCommentPending] = useState(false);
  const savedText = useRef({ title: "", description: "" });
  const draftText = useRef({ title: "", description: "" });
  const requestSeq = useRef(0);

  const applyServerText = useCallback((next: PublicTask) => {
    setTitle(next.title);
    setDescription(next.description);
    savedText.current = { title: next.title, description: next.description };
    draftText.current = { title: next.title, description: next.description };
  }, []);

  const applyTaskMeta = useCallback((next: PublicTask, opts?: { text?: boolean }) => {
    setTask(next);
    if (opts?.text) {
      applyServerText(next);
    }
  }, [applyServerText]);

  const mergePolledTask = useCallback(
    (next: PublicTask) => {
      setTask((current) => {
        if (!current || current.id !== next.id) {
          applyServerText(next);
          return next;
        }
        const dirty =
          draftText.current.title !== savedText.current.title ||
          draftText.current.description !== savedText.current.description;
        if (!dirty) {
          applyServerText(next);
          return next;
        }
        return {
          ...next,
          title: current.title,
          description: current.description,
        };
      });
    },
    [applyServerText],
  );

  const reload = useCallback(async () => {
    if (!taskId || Array.isArray(taskId)) {
      return;
    }
    const seq = ++requestSeq.current;
    try {
      const next = await fetchTask(taskId);
      if (seq !== requestSeq.current) {
        return;
      }
      mergePolledTask(next);
      const [nextComments, nextActivity] = await Promise.all([
        fetchTaskComments(next.id),
        fetchTaskActivity(next.project_id, next.id),
      ]);
      if (seq !== requestSeq.current) {
        return;
      }
      setComments(nextComments);
      setActivity(nextActivity.filter((item) => item.object_id === next.id));
      setError(null);
    } catch (caught) {
      if (seq === requestSeq.current) {
        setError(caught instanceof ApiError ? caught.message : "task not found");
      }
    } finally {
      if (seq === requestSeq.current) {
        setLoading(false);
      }
    }
  }, [mergePolledTask, taskId]);

  useEffect(() => {
    if (!taskId || Array.isArray(taskId)) {
      return;
    }
    const selectedId = taskId;
    let cancelled = false;
    async function load() {
      try {
        const next = await fetchTask(selectedId);
        if (cancelled) {
          return;
        }
        applyTaskMeta(next, { text: true });
        const [nextComments, nextActivity] = await Promise.all([
          fetchTaskComments(next.id),
          fetchTaskActivity(next.project_id, next.id),
        ]);
        if (cancelled) {
          return;
        }
        setComments(nextComments);
        setActivity(nextActivity.filter((item) => item.object_id === next.id));
        setError(null);
      } catch (caught) {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : "task not found");
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [applyTaskMeta, taskId]);

  useInterval(
    () => {
      void reload();
    },
    taskId && !Array.isArray(taskId) ? DETAIL_POLL_MS : null,
  );

  const briefTaskId = task?.id ?? null;
  const briefProjectId = task?.project_id ?? null;

  useEffect(() => {
    if (!briefTaskId || !briefProjectId) {
      return;
    }
    const selectedProjectId = briefProjectId;
    const selectedTaskId = briefTaskId;
    let cancelled = false;
    async function loadBrief() {
      try {
        const compiled = await compileTaskBrief(selectedProjectId, selectedTaskId);
        if (!cancelled) {
          setBrief(compiled);
          setBriefError(null);
        }
      } catch (caught) {
        if (!cancelled) {
          setBrief(null);
          setBriefError(caught instanceof ApiError ? caught.message : "brief unavailable");
        }
      }
    }
    void loadBrief();
    return () => {
      cancelled = true;
    };
  }, [briefProjectId, briefTaskId]);

  async function handleConflict(error: unknown, fallback: PublicTask): Promise<boolean> {
    if (error instanceof ApiError && error.code === "version_conflict") {
      const server = taskFromConflict(error);
      if (server) {
        applyTaskMeta(server, { text: true });
        toast("Updated elsewhere — reapplied.");
        return true;
      }
    }
    applyTaskMeta(fallback, { text: true });
    toast(error instanceof ApiError ? error.message : "failed to update task");
    return false;
  }

  async function onSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!task) {
      return;
    }
    const previous = task;
    const optimistic: PublicTask = { ...task, title: title.trim() || task.title, description };
    applyTaskMeta(optimistic, { text: true });
    setSaving(true);
    try {
      const updated = await patchTask(task.id, {
        expected_version: task.version,
        title: optimistic.title,
        description: optimistic.description,
      });
      applyTaskMeta(updated, { text: true });
    } catch (caught) {
      await handleConflict(caught, previous);
    } finally {
      setSaving(false);
    }
  }

  async function onStatusChange(status: TaskStatus) {
    if (!task || task.status === status) {
      return;
    }
    const previous = task;
    applyTaskMeta({ ...task, status });
    try {
      const updated = await setTaskStatus(task.id, status, task.version);
      applyTaskMeta(updated);
    } catch (caught) {
      await handleConflict(caught, previous);
    }
  }

  async function onComment(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!task) {
      return;
    }
    const body = commentBody.trim();
    if (!body) {
      return;
    }
    setCommentPending(true);
    try {
      await createTaskComment(task.id, body, newIdempotencyKey());
      setCommentBody("");
      await reload();
    } catch (caught) {
      toast(caught instanceof ApiError ? caught.message : "failed to add comment");
    } finally {
      setCommentPending(false);
    }
  }

  if (loading && !task) {
    return <p className="text-sm text-muted">Loading…</p>;
  }

  if (!task) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">Task</h1>
        <p className="text-sm text-red-600">{error ?? "task not found"}</p>
        <Link className="text-sm underline" href="/app/board">
          Back to board
        </Link>
      </section>
    );
  }

  const agentEvents = activity.filter(
    (item) => item.actor_type === "token" || item.actor_type === "agent",
  );

  return (
    <section className="space-y-6">
      <div className="flex flex-wrap items-center gap-3 text-sm text-muted">
        <Link className="underline" href="/app/board">
          Board
        </Link>
        <span>/</span>
        <Link className="underline" href="/app/backlog">
          Backlog
        </Link>
      </div>

      <form className="space-y-3" onSubmit={onSave}>
        <div className="flex flex-wrap items-start justify-between gap-3">
          <input
            className="min-w-0 flex-1 bg-transparent text-2xl font-semibold tracking-tight outline-none"
            value={title}
            onChange={(event) => {
              const value = event.target.value;
              draftText.current = { ...draftText.current, title: value };
              setTitle(value);
            }}
            maxLength={200}
            aria-label="Title"
          />
          <LockBadge task={task} />
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <select
            className="h-9 rounded-md border border-border bg-background px-2 text-sm capitalize"
            aria-label="Status"
            value={task.status}
            onChange={(event) => void onStatusChange(event.target.value as TaskStatus)}
          >
            {TASK_STATUSES.map((status) => (
              <option key={status} value={status}>
                {statusLabel(status)}
              </option>
            ))}
          </select>
          {task.assignee_agent_name ? (
            <span className="text-xs text-muted">Agent {task.assignee_agent_name}</span>
          ) : null}
        </div>
        <textarea
          className="min-h-32 w-full rounded-md border border-border bg-surface px-3 py-2 text-sm"
          value={description}
          onChange={(event) => {
            const value = event.target.value;
            draftText.current = { ...draftText.current, description: value };
            setDescription(value);
          }}
          maxLength={8000}
          aria-label="Description"
        />
        {error ? <p className="text-sm text-red-600">{error}</p> : null}
        <button
          className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
          type="submit"
          disabled={saving}
        >
          {saving ? "Saving…" : "Save"}
        </button>
      </form>

      <article className="space-y-2 rounded-lg border border-border bg-surface p-4">
        <h2 className="text-sm font-semibold tracking-wide uppercase">Session brief</h2>
        {briefError ? (
          <p className="text-sm text-muted">Brief unavailable. {briefError}</p>
        ) : !brief ? (
          <p className="text-sm text-muted">Compiling…</p>
        ) : (
          <div className="space-y-2 text-sm">
            {brief.milestone ? (
              <p className="text-muted">Milestone: {brief.milestone.title}</p>
            ) : null}
            {brief.handoff ? <p>{brief.handoff.summary}</p> : null}
            {(brief.sections ?? []).slice(0, 4).map((section) => (
              <div key={section.title}>
                <p className="font-medium">{section.title}</p>
                <p className="whitespace-pre-wrap text-muted">{section.body_md || "—"}</p>
              </div>
            ))}
            {brief.budget?.dropped && brief.budget.dropped.length > 0 ? (
              <p className="text-xs text-muted">Dropped: {brief.budget.dropped.join(", ")}</p>
            ) : null}
          </div>
        )}
      </article>

      <div className="grid gap-4 lg:grid-cols-2">
        <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
          <h2 className="text-sm font-semibold tracking-wide uppercase">Comments</h2>
          {comments.length === 0 ? <p className="text-sm text-muted">No comments yet.</p> : null}
          <ul className="space-y-3">
            {comments.map((comment) => (
              <li key={comment.id} className="text-sm">
                <p className="text-xs text-muted">
                  {comment.author_type} · {new Date(comment.created_at).toLocaleString()}
                </p>
                <p className="whitespace-pre-wrap">{comment.body}</p>
              </li>
            ))}
          </ul>
          <form className="flex flex-col gap-2" onSubmit={onComment}>
            <textarea
              className="min-h-20 rounded-md border border-border bg-background px-2 py-1 text-sm"
              value={commentBody}
              onChange={(event) => setCommentBody(event.target.value)}
              placeholder="Write a comment"
              maxLength={8000}
            />
            <button
              className="self-start rounded-md border border-border px-3 py-1.5 text-sm disabled:opacity-60"
              type="submit"
              disabled={commentPending}
            >
              {commentPending ? "Posting…" : "Comment"}
            </button>
          </form>
        </article>

        <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
          <h2 className="text-sm font-semibold tracking-wide uppercase">Agent activity</h2>
          {agentEvents.length === 0 ? (
            <p className="text-sm text-muted">No agent events on this task yet.</p>
          ) : (
            <ul className="space-y-2 text-sm">
              {agentEvents.map((event) => (
                <li key={event.id}>
                  <p className="font-medium">{event.verb.replaceAll("_", " ")}</p>
                  <p className="text-xs text-muted">
                    {new Date(event.created_at).toLocaleString()}
                  </p>
                </li>
              ))}
            </ul>
          )}
        </article>
      </div>
    </section>
  );
}
