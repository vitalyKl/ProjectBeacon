"use client";

import Link from "next/link";
import { useParams, useSearchParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useRef, useState, type FormEvent } from "react";

import { ApiError, newIdempotencyKey, fetchTaskGithubPrs, type PublicGithubPull } from "@/lib/api";
import { isOfferedToAgents } from "@/lib/brief";
import { briefDroppedItems } from "@/lib/brief-preview";
import { activityVerbLabel, t } from "@/lib/i18n";
import {
  fetchProjectLabels,
  setTaskLabels,
  toggleLabelId,
  type PublicLabel,
} from "@/lib/labels";
import { DEFAULT_TASK_PRIORITY } from "@/lib/priority";
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
import {
  isTaskDetailFromBacklog,
  taskDetailCrumbs,
  taskDetailFallbackHref,
} from "@/lib/task-detail";
import { FIELD_ERROR_CLASS } from "@/lib/ui";
import { useInterval } from "@/lib/use-interval";
import { useT, useTf } from "@/lib/use-locale";

import { BriefBlocks } from "../../brief-blocks";
import { LockBadge } from "../../lock-badge";
import { PrioritySelect } from "../../priority-select";
import { useToast } from "../../toast";

const DETAIL_POLL_MS = 10000;

export default function TaskDetailPage() {
  return (
    <Suspense fallback={<TaskDetailFallback />}>
      <TaskDetailView />
    </Suspense>
  );
}

function TaskDetailFallback() {
  const label = useT();
  return <p className="text-sm text-muted">{label("common.loading")}</p>;
}

function TaskDetailView() {
  const params = useParams<{ id: string }>();
  const searchParams = useSearchParams();
  const taskId = params.id;
  const label = useT();
  const format = useTf();
  const { toast } = useToast();
  const [task, setTask] = useState<PublicTask | null>(null);
  const [comments, setComments] = useState<PublicComment[]>([]);
  const [activity, setActivity] = useState<PublicActivity[]>([]);
  const [brief, setBrief] = useState<SessionBriefPreview | null>(null);
  const [briefError, setBriefError] = useState<string | null>(null);
  const [briefPending, setBriefPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [howToCheck, setHowToCheck] = useState("");
  const [saving, setSaving] = useState(false);
  const [commentBody, setCommentBody] = useState("");
  const [commentPending, setCommentPending] = useState(false);
  const [catalog, setCatalog] = useState<PublicLabel[]>([]);
  const [labelPending, setLabelPending] = useState(false);
  const [githubPrs, setGithubPrs] = useState<PublicGithubPull[]>([]);
  const savedText = useRef({ title: "", description: "", howToCheck: "" });
  const draftText = useRef({ title: "", description: "", howToCheck: "" });
  const requestSeq = useRef(0);

  const applyServerText = useCallback((next: PublicTask) => {
    setTitle(next.title);
    setDescription(next.description);
    setHowToCheck(next.how_to_check ?? "");
    savedText.current = {
      title: next.title,
      description: next.description,
      howToCheck: next.how_to_check ?? "",
    };
    draftText.current = {
      title: next.title,
      description: next.description,
      howToCheck: next.how_to_check ?? "",
    };
  }, []);

  const applyTaskMeta = useCallback((next: PublicTask, opts?: { text?: boolean }) => {
    setTask(next);
    if (opts?.text) {
      applyServerText(next);
    }
  }, [applyServerText]);

  const mergePolledTask = useCallback(
    (next: PublicTask, expectedId: string) => {
      if (next.id !== expectedId) {
        return;
      }
      setTask((current) => {
        if (current && current.id !== next.id) {
          return current;
        }
        if (current && next.version < current.version) {
          return current;
        }
        if (!current) {
          applyServerText(next);
          return next;
        }
        const dirty =
          draftText.current.title !== savedText.current.title ||
          draftText.current.description !== savedText.current.description ||
          draftText.current.howToCheck !== savedText.current.howToCheck;
        if (!dirty) {
          applyServerText(next);
          return next;
        }
        return {
          ...next,
          title: current.title,
          description: current.description,
          how_to_check: current.how_to_check,
        };
      });
    },
    [applyServerText],
  );

  const reload = useCallback(async () => {
    if (!taskId || Array.isArray(taskId)) {
      return;
    }
    const selectedId = taskId;
    const seq = ++requestSeq.current;
    try {
      const next = await fetchTask(selectedId);
      if (seq !== requestSeq.current || next.id !== selectedId) {
        return;
      }
      mergePolledTask(next, selectedId);
      const [nextComments, nextActivity, nextLabels] = await Promise.all([
        fetchTaskComments(next.id),
        fetchTaskActivity(next.project_id, next.id),
        fetchProjectLabels(next.project_id),
      ]);
      if (seq !== requestSeq.current || next.id !== selectedId) {
        return;
      }
      setComments(nextComments);
      setActivity(nextActivity.filter((item) => item.object_id === selectedId));
      setCatalog(nextLabels);
      try {
        const prs = await fetchTaskGithubPrs(next.id);
        if (seq === requestSeq.current && next.id === selectedId) {
          setGithubPrs(prs);
        }
      } catch {
        // PR fetch is non-critical
      }
      setError(null);
    } catch (caught) {
      if (seq === requestSeq.current) {
        setError(caught instanceof ApiError ? caught.message : t("task.notFound"));
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
        const [nextComments, nextActivity, nextLabels] = await Promise.all([
          fetchTaskComments(next.id),
          fetchTaskActivity(next.project_id, next.id),
          fetchProjectLabels(next.project_id),
        ]);
        if (cancelled) {
          return;
        }
        setComments(nextComments);
        setActivity(nextActivity.filter((item) => item.object_id === next.id));
        setCatalog(nextLabels);
        setError(null);
      } catch (caught) {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : t("task.notFound"));
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
      requestSeq.current += 1;
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

  const loadBrief = useCallback(async () => {
    if (!briefTaskId || !briefProjectId) {
      return;
    }
    setBriefPending(true);
    try {
      const compiled = await compileTaskBrief(briefProjectId, briefTaskId);
      setBrief(compiled);
      setBriefError(null);
    } catch (caught) {
      setBrief(null);
      setBriefError(caught instanceof ApiError ? caught.message : t("task.briefUnavailableShort"));
    } finally {
      setBriefPending(false);
    }
  }, [briefProjectId, briefTaskId]);

  async function handleConflict(error: unknown, fallback: PublicTask): Promise<boolean> {
    if (error instanceof ApiError && error.code === "version_conflict") {
      const server = taskFromConflict(error);
      if (server) {
        requestSeq.current += 1;
        applyTaskMeta(server, { text: true });
        toast(t("common.conflictReapplied"));
        return true;
      }
    }
    requestSeq.current += 1;
    applyTaskMeta(fallback, { text: true });
    toast(error instanceof ApiError ? error.message : t("common.failedUpdateTask"));
    return false;
  }

  async function onSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!task) {
      return;
    }
    const previous = task;
    const optimistic: PublicTask = {
      ...task,
      title: title.trim() || task.title,
      description,
      how_to_check: howToCheck,
    };
    applyTaskMeta(optimistic, { text: true });
    setSaving(true);
    try {
      const updated = await patchTask(task.id, {
        expected_version: task.version,
        title: optimistic.title,
        description: optimistic.description,
        how_to_check: optimistic.how_to_check,
      });
      requestSeq.current += 1;
      applyTaskMeta(updated, { text: true });
    } catch (caught) {
      await handleConflict(caught, previous);
    } finally {
      setSaving(false);
    }
  }

  async function onToggleLabel(labelId: string) {
    if (!task) {
      return;
    }
    const previous = task;
    const nextIds = toggleLabelId(
      (task.labels ?? []).map((item) => item.id),
      labelId,
    );
    const optimistic: PublicTask = {
      ...task,
      labels: catalog
        .filter((item) => nextIds.includes(item.id))
        .map((item) => ({
          id: item.id,
          slug: item.slug,
          name: item.name,
          color: item.color,
          status: item.status,
        })),
    };
    applyTaskMeta(optimistic);
    setLabelPending(true);
    try {
      const updated = await setTaskLabels(task.id, nextIds);
      requestSeq.current += 1;
      applyTaskMeta(updated);
    } catch (caught) {
      applyTaskMeta(previous);
      toast(caught instanceof ApiError ? caught.message : t("task.failedLabels"));
    } finally {
      setLabelPending(false);
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
      requestSeq.current += 1;
      applyTaskMeta(updated);
    } catch (caught) {
      await handleConflict(caught, previous);
    }
  }

  async function onPriorityChange(priority: number) {
    if (!task || task.priority === priority) {
      return;
    }
    const previous = task;
    applyTaskMeta({ ...task, priority });
    try {
      const updated = await patchTask(task.id, {
        expected_version: task.version,
        priority,
      });
      requestSeq.current += 1;
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
      toast(caught instanceof ApiError ? caught.message : t("task.failedComment"));
    } finally {
      setCommentPending(false);
    }
  }

  if (loading && !task) {
    return <p className="text-sm text-muted">{label("common.loading")}</p>;
  }

  if (!task) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">{label("task.title")}</h1>
        <p className={FIELD_ERROR_CLASS}>{error ?? label("task.notFound")}</p>
        <Link className="text-sm underline" href={taskDetailFallbackHref()}>
          {label("task.backToBoard")}
        </Link>
      </section>
    );
  }

  const agentEvents = activity.filter(
    (item) => item.actor_type === "token" || item.actor_type === "agent",
  );
  const fromBacklog = isTaskDetailFromBacklog(searchParams);
  const crumbs = taskDetailCrumbs(title.trim() || task.title, { fromBacklog });
  const dropped = briefDroppedItems(brief?.budget?.dropped);

  return (
    <section className="space-y-6">
      <nav className="flex flex-wrap items-center gap-2 text-sm text-muted">
        {crumbs.map((crumb, index) => (
          <span
            key={crumb.type === "link" ? crumb.surface : "title"}
            className="flex items-center gap-2"
          >
            {index > 0 ? <span>/</span> : null}
            {crumb.type === "link" ? (
              <Link className="underline" href={crumb.href}>
                {label(crumb.surface === "board" ? "nav.board" : "nav.backlog")}
              </Link>
            ) : (
              <span className="text-foreground">{crumb.text}</span>
            )}
          </span>
        ))}
      </nav>

      <div className="grid gap-6 lg:grid-cols-2">
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
              aria-label={label("common.title")}
            />
            <LockBadge task={task} />
          </div>
          <div className="flex flex-wrap items-center gap-3">
            <select
              className="h-9 rounded-md border border-border bg-background px-2 text-sm capitalize"
              aria-label={label("common.status")}
              value={task.status}
              onChange={(event) => void onStatusChange(event.target.value as TaskStatus)}
            >
              {TASK_STATUSES.map((status) => (
                <option key={status} value={status}>
                  {statusLabel(status)}
                </option>
              ))}
            </select>
            <PrioritySelect
              value={task.priority ?? DEFAULT_TASK_PRIORITY}
              onChange={(priority) => void onPriorityChange(priority)}
            />
            {task.assignee_agent_name ? (
              <span className="text-xs text-muted">
                {format("task.agent", { name: task.assignee_agent_name })}
              </span>
            ) : null}
          </div>
          <div className="space-y-2">
            <h2 className="text-xs font-semibold tracking-wide text-muted uppercase">{label("task.areas")}</h2>
            {catalog.length === 0 ? (
              <p className="text-sm text-muted">{label("task.noAreas")}</p>
            ) : (
              <fieldset className="flex flex-wrap gap-2" disabled={labelPending}>
                <legend className="sr-only">{label("task.areas")}</legend>
                {catalog.map((area) => (
                  <label
                    key={area.id}
                    className="flex items-center gap-1 rounded-md border border-border px-2 py-1 text-xs"
                  >
                    <input
                      type="checkbox"
                      checked={(task.labels ?? []).some((item) => item.id === area.id)}
                      onChange={() => void onToggleLabel(area.id)}
                    />
                    {area.name}
                    {area.status === "proposed" ? (
                      <span className="text-muted">{label("task.proposed")}</span>
                    ) : null}
                  </label>
                ))}
              </fieldset>
            )}
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
            aria-label={label("common.description")}
          />
          <label className="flex flex-col gap-1 text-sm">
            <span className="text-xs font-semibold tracking-wide text-muted uppercase">
              {label("task.howToCheck")}
            </span>
            <span className="text-xs text-muted">{label("task.howToCheckHelp")}</span>
            <textarea
              className="min-h-24 w-full rounded-md border border-border bg-surface px-3 py-2 text-sm"
              value={howToCheck}
              onChange={(event) => {
                const value = event.target.value;
                draftText.current = { ...draftText.current, howToCheck: value };
                setHowToCheck(value);
              }}
              maxLength={8000}
              placeholder={label("task.howToCheckPlaceholder")}
              aria-label={label("task.howToCheck")}
            />
          </label>
          {error ? <p className={FIELD_ERROR_CLASS}>{error}</p> : null}
          <button
            className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
            type="submit"
            disabled={saving}
          >
            {saving ? label("common.saving") : label("common.save")}
          </button>
        </form>

        <article className="min-w-0 space-y-3 rounded-lg border border-border bg-surface p-4">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="space-y-1">
              <h2 className="text-sm font-semibold tracking-wide uppercase">{label("task.sessionBrief")}</h2>
              <p className="text-sm text-muted">{label("task.sessionBriefHint")}</p>
            </div>
            <button
              className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
              type="button"
              disabled={briefPending}
              onClick={() => void loadBrief()}
            >
              {briefPending
                ? label("common.compiling")
                : brief
                  ? label("task.compileAgain")
                  : label("common.compileBrief")}
            </button>
          </div>
          {isOfferedToAgents(task) ? (
            <p className="text-sm text-muted">{label("task.offered")}</p>
          ) : null}
          {briefError ? (
            <p className={FIELD_ERROR_CLASS}>{format("task.briefUnavailable", { error: briefError })}</p>
          ) : null}
          {!brief && !briefError && !briefPending ? (
            <p className="text-sm text-muted">{label("task.compileWhen")}</p>
          ) : null}
          {brief ? (
            <div className="space-y-3">
              {brief.milestone ? (
                <p className="text-sm text-muted">
                  {format("task.milestone", { title: brief.milestone.title })}
                </p>
              ) : null}
              {brief.task?.how_to_check ? (
                <div className="space-y-1">
                  <h3 className="text-xs font-semibold tracking-wide text-muted uppercase">
                    {label("task.howToCheck")}
                  </h3>
                  <p className="text-sm whitespace-pre-wrap">{brief.task.how_to_check}</p>
                </div>
              ) : null}
              {brief.handoff ? (
                <p className="text-sm whitespace-pre-wrap">{brief.handoff.summary}</p>
              ) : null}
              <BriefBlocks
                sections={brief.sections ?? []}
                empty={label("task.noBriefSections")}
              />
              {dropped.length > 0 ? (
                <p className="text-xs text-muted">
                  {format("task.dropped", { items: dropped.join(", ") })}
                </p>
              ) : null}
            </div>
          ) : null}
        </article>

        <div className="grid gap-4 lg:col-span-2 lg:grid-cols-2">
          <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
            <h2 className="text-sm font-semibold tracking-wide uppercase">{label("task.githubPrs")}</h2>
            <p className="text-xs text-muted">{label("task.githubPrsHint")}</p>
            {githubPrs.length === 0 ? (
              <p className="text-sm text-muted">{label("task.githubPrsEmpty")}</p>
            ) : (
              <ul className="space-y-2">
                {githubPrs.map((pr) => (
                  <li key={pr.id} className="rounded-md border border-border bg-background px-3 py-2 text-sm">
                    <div className="flex items-center justify-between">
                      <a
                        className="font-medium hover:underline"
                        href={pr.html_url}
                        target="_blank"
                        rel="noreferrer"
                      >
                        #{pr.number} {pr.title}
                      </a>
                      <span className="text-xs text-muted">{pr.state}</span>
                    </div>
                    {pr.files !== undefined ? (
                      <p className="mt-1 text-xs text-muted">
                        {pr.files} {label("task.githubPrsFiles")} ·{" "}
                        {pr.additions !== undefined && pr.additions > 0
                          ? `${pr.additions} ${label("task.githubPrsAdditions")}`
                          : ""}
                        {pr.deletions !== undefined && pr.deletions > 0
                          ? ` · ${pr.deletions} ${label("task.githubPrsDeletions")}`
                          : ""}
                      </p>
                    ) : null}
                    {pr.matched_files && pr.matched_files.length > 0 ? (
                      <div className="mt-2">
                        <p className="text-xs font-medium text-accent">
                          {pr.matched_files.length} {label("task.githubPrsMatched")}: {pr.matched_files.slice(0, 5).join(", ")}{pr.matched_files.length > 5 ? ` (+${pr.matched_files.length - 5})` : ""}
                        </p>
                      </div>
                    ) : null}
                    {pr.diff_url ? (
                      <a
                        className="mt-1 block text-xs font-mono text-accent hover:underline"
                        href={`https://github.com${pr.diff_url}`}
                        target="_blank"
                        rel="noreferrer"
                      >
                        {label("task.githubPrsDiff")}
                      </a>
                    ) : null}
                  </li>
                ))}
              </ul>
            )}
          </article>

          <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
            <h2 className="text-sm font-semibold tracking-wide uppercase">{label("task.comments")}</h2>
            {comments.length === 0 ? <p className="text-sm text-muted">{label("task.noComments")}</p> : null}
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
                placeholder={label("task.writeComment")}
                maxLength={8000}
              />
              <button
                className="self-start rounded-md border border-border px-3 py-1.5 text-sm disabled:opacity-60"
                type="submit"
                disabled={commentPending}
              >
                {commentPending ? label("task.posting") : label("task.comment")}
              </button>
            </form>
          </article>

          <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
            <h2 className="text-sm font-semibold tracking-wide uppercase">{label("task.agentActivity")}</h2>
            {agentEvents.length === 0 ? (
              <p className="text-sm text-muted">{label("task.noAgentEvents")}</p>
            ) : (
              <ul className="space-y-2 text-sm">
                {agentEvents.map((event) => (
                  <li key={event.id}>
                    <p className="font-medium">{activityVerbLabel(event.verb)}</p>
                    <p className="text-xs text-muted">
                      {new Date(event.created_at).toLocaleString()}
                    </p>
                  </li>
                ))}
              </ul>
            )}
          </article>
        </div>
      </div>
    </section>
  );
}
