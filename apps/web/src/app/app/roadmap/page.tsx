"use client";

import Link from "next/link";
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from "react";

import { ApiError, newIdempotencyKey } from "@/lib/api";
import {
  createTaskDependency,
  fetchProjectDependencies,
  fetchProjectMilestones,
  fetchProjectTasks,
  isTaskLocked,
  milestoneStatusLabel,
  statusLabel,
  type DependencyType,
  type PublicDependency,
  type PublicMilestone,
  type PublicTask,
  type TaskStatus,
} from "@/lib/roadmap";
import { t } from "@/lib/i18n";
import { ensureBeaconSeed } from "@/lib/seed";
import { useInterval } from "@/lib/use-interval";
import { useT } from "@/lib/use-locale";

import { LockBadge } from "../lock-badge";
import { useSelectedProject } from "../project-context";
import { useToast } from "../toast";

const ROADMAP_POLL_MS = 10000;

const STATUS_DOT: Record<TaskStatus, string> = {
  backlog: "bg-zinc-400",
  ready: "bg-sky-500",
  in_progress: "bg-amber-500",
  blocked: "bg-red-500",
  in_review: "bg-violet-500",
  done: "bg-emerald-500",
  canceled: "bg-zinc-500",
};

const STATUS_FILL: Record<TaskStatus, string> = {
  backlog: "#a1a1aa",
  ready: "#0ea5e9",
  in_progress: "#f59e0b",
  blocked: "#ef4444",
  in_review: "#8b5cf6",
  done: "#10b981",
  canceled: "#71717a",
};

type RoadmapView = "timeline" | "graph";

export default function RoadmapPage() {
  const label = useT();
  const { project } = useSelectedProject();
  const { toast } = useToast();
  const [view, setView] = useState<RoadmapView>("timeline");
  const [tasks, setTasks] = useState<PublicTask[]>([]);
  const [milestones, setMilestones] = useState<PublicMilestone[]>([]);
  const [dependencies, setDependencies] = useState<PublicDependency[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const projectId = project?.id ?? null;
  const requestSeq = useRef(0);

  const reload = useCallback(
    async (opts?: { silent?: boolean }) => {
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
        const [nextTasks, nextMilestones, nextDependencies] = await Promise.all([
          fetchProjectTasks(selectedId),
          fetchProjectMilestones(selectedId),
          fetchProjectDependencies(selectedId),
        ]);
        if (seq !== requestSeq.current) {
          return;
        }
        setTasks(nextTasks);
        setMilestones(nextMilestones);
        setDependencies(nextDependencies);
        setError(null);
      } catch (caught) {
        if (seq === requestSeq.current) {
          setError(caught instanceof ApiError ? caught.message : t("roadmap.failedLoad"));
        }
      } finally {
        if (!opts?.silent && seq === requestSeq.current) {
          setLoading(false);
        }
      }
    },
    [projectId],
  );

  useEffect(() => {
    if (!projectId) {
      const id = window.setTimeout(() => {
        setTasks([]);
        setMilestones([]);
        setDependencies([]);
        setError(null);
        setLoading(false);
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
      void reload({ silent: true });
    },
    projectId ? ROADMAP_POLL_MS : null,
  );

  async function onCreated() {
    requestSeq.current += 1;
    await reload({ silent: true });
  }

  if (!project) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">{label("nav.roadmap")}</h1>
        <p className="text-sm text-muted">{label("common.selectProject")}</p>
      </section>
    );
  }

  return (
    <section className="space-y-4">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">{label("nav.roadmap")}</h1>
          <p className="text-sm text-muted">{label("roadmap.hint")}</p>
        </div>
        <div className="flex rounded-md border border-border bg-surface p-0.5 text-sm">
          <ViewTab
            label={label("roadmap.timeline")}
            active={view === "timeline"}
            onClick={() => setView("timeline")}
          />
          <ViewTab
            label={label("roadmap.dependencies")}
            active={view === "graph"}
            onClick={() => setView("graph")}
          />
        </div>
      </header>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {loading ? <p className="text-sm text-muted">{label("common.loading")}</p> : null}
      {view === "timeline" ? (
        <TimelineView milestones={milestones} tasks={tasks} />
      ) : (
        <DependencyView
          tasks={tasks}
          dependencies={dependencies}
          onCreated={() => void onCreated()}
          onCycle={(message) => toast(message)}
        />
      )}
    </section>
  );
}

function ViewTab({
  label,
  active,
  onClick,
}: {
  label: string;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      className={`rounded px-3 py-1.5 ${active ? "bg-background font-medium" : "text-muted"}`}
      type="button"
      onClick={onClick}
    >
      {label}
    </button>
  );
}

function TimelineView({
  milestones,
  tasks,
}: {
  milestones: PublicMilestone[];
  tasks: PublicTask[];
}) {
  const buckets = useMemo(() => {
    const sorted = [...milestones].sort((a, b) => {
      const dateDelta = compareOptionalDates(a.target_date, b.target_date);
      if (dateDelta !== 0) {
        return dateDelta;
      }
      return a.sort_order - b.sort_order || a.title.localeCompare(b.title);
    });
    const byMilestone = new Map<string, PublicTask[]>();
    const unscheduled: PublicTask[] = [];
    for (const task of tasks) {
      if (!task.milestone_id) {
        unscheduled.push(task);
        continue;
      }
      const group = byMilestone.get(task.milestone_id) ?? [];
      group.push(task);
      byMilestone.set(task.milestone_id, group);
    }
    for (const group of byMilestone.values()) {
      group.sort((a, b) => a.title.localeCompare(b.title));
    }
    unscheduled.sort((a, b) => a.title.localeCompare(b.title));
    return { sorted, byMilestone, unscheduled };
  }, [milestones, tasks]);

  const text = t;
  if (milestones.length === 0 && tasks.length === 0) {
    return <p className="text-sm text-muted">{text("roadmap.empty")}</p>;
  }

  return (
    <ol className="relative space-y-4 border-l border-border pl-6">
      {buckets.sorted.map((milestone) => (
        <li key={milestone.id} className="relative">
          <span className="absolute top-1.5 -left-[1.7rem] h-3 w-3 rounded-full border border-border bg-surface" />
          <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
            <header className="flex flex-wrap items-start justify-between gap-2">
              <div>
                <h2 className="font-medium">{milestone.title}</h2>
                {milestone.description ? (
                  <p className="mt-1 text-sm text-muted">{milestone.description}</p>
                ) : null}
              </div>
              <div className="flex flex-wrap items-center gap-2 text-xs text-muted">
                <span className="rounded-full border border-border px-2 py-0.5 capitalize">
                  {milestoneStatusLabel(milestone.status)}
                </span>
                {milestone.target_date ? <span>{milestone.target_date}</span> : null}
              </div>
            </header>
            <TaskList tasks={buckets.byMilestone.get(milestone.id) ?? []} empty={text("roadmap.noTasks")} />
          </article>
        </li>
      ))}
      <li className="relative">
        <span className="absolute top-1.5 -left-[1.7rem] h-3 w-3 rounded-full border border-dashed border-border bg-background" />
        <article className="space-y-3 rounded-lg border border-dashed border-border bg-surface p-4">
          <h2 className="font-medium">{text("roadmap.unscheduled")}</h2>
          <TaskList tasks={buckets.unscheduled} empty={text("roadmap.noUnscheduled")} />
        </article>
      </li>
    </ol>
  );
}

function TaskList({ tasks, empty }: { tasks: PublicTask[]; empty: string }) {
  if (tasks.length === 0) {
    return <p className="text-sm text-muted">{empty}</p>;
  }
  return (
    <ul className="space-y-2">
      {tasks.map((task) => (
        <li key={task.id}>
          <Link
            className="flex items-center justify-between gap-3 rounded-md border border-border bg-background px-3 py-2 text-sm hover:border-foreground/20"
            href={`/app/tasks/${task.id}`}
          >
            <span className="flex min-w-0 items-center gap-2">
              <span className={`h-2 w-2 shrink-0 rounded-full ${STATUS_DOT[task.status]}`} />
              <span className="truncate">{task.title}</span>
            </span>
            <span className="flex items-center gap-2 text-xs text-muted capitalize">
              {statusLabel(task.status)}
              <LockBadge task={task} />
            </span>
          </Link>
        </li>
      ))}
    </ul>
  );
}

function DependencyView({
  tasks,
  dependencies,
  onCreated,
  onCycle,
}: {
  tasks: PublicTask[];
  dependencies: PublicDependency[];
  onCreated: () => void;
  onCycle: (message: string) => void;
}) {
  const layout = useMemo(() => layoutGraph(tasks, dependencies), [tasks, dependencies]);

  const text = t;
  return (
    <div className="space-y-4">
      {dependencies.length === 0 ? (
        <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
          <p className="text-sm text-muted">{text("roadmap.noLinks")}</p>
          <AddDependencyForm
            key={tasks[0]?.project_id ?? "none"}
            tasks={tasks}
            onCreated={onCreated}
            onCycle={onCycle}
          />
        </article>
      ) : (
        <>
          <div className="overflow-x-auto rounded-lg border border-border bg-surface p-4">
            <p className="mb-3 text-sm text-muted">{text("roadmap.graphHint")}</p>
            <svg
              role="img"
              aria-label={text("roadmap.graphLabel")}
              viewBox={`0 0 ${layout.width} ${layout.height}`}
              className="h-auto min-h-72 w-full min-w-[36rem]"
            >
              <defs>
                <marker
                  id="arrow-blocks"
                  viewBox="0 0 10 10"
                  refX="9"
                  refY="5"
                  markerWidth="6"
                  markerHeight="6"
                  orient="auto-start-reverse"
                >
                  <path d="M 0 0 L 10 5 L 0 10 z" className="fill-red-500" />
                </marker>
                <marker
                  id="arrow-relates"
                  viewBox="0 0 10 10"
                  refX="9"
                  refY="5"
                  markerWidth="6"
                  markerHeight="6"
                  orient="auto-start-reverse"
                >
                  <path d="M 0 0 L 10 5 L 0 10 z" className="fill-zinc-400" />
                </marker>
              </defs>
              {layout.edges.map((edge) => (
                <line
                  key={`${edge.from}-${edge.to}-${edge.type}`}
                  x1={edge.x1}
                  y1={edge.y1}
                  x2={edge.x2}
                  y2={edge.y2}
                  className={
                    edge.type === "blocks"
                      ? "stroke-red-500"
                      : "stroke-zinc-400 [stroke-dasharray:6_4]"
                  }
                  strokeWidth={edge.type === "blocks" ? 2 : 1.5}
                  markerEnd={edge.type === "blocks" ? "url(#arrow-blocks)" : "url(#arrow-relates)"}
                />
              ))}
              {layout.nodes.map((node) => (
                <g key={node.task.id} transform={`translate(${node.x} ${node.y})`}>
                  <a href={`/app/tasks/${node.task.id}`}>
                    <rect
                      width={node.width}
                      height={node.height}
                      rx={8}
                      className="fill-[var(--surface)] stroke-[var(--border)]"
                    />
                    <circle
                      cx={14}
                      cy={node.height / 2}
                      r={5}
                      fill={STATUS_FILL[node.task.status]}
                    />
                    <text
                      x={26}
                      y={22}
                      className="fill-[var(--foreground)] text-[12px] font-medium"
                    >
                      {truncateLabel(node.task.title, 28)}
                    </text>
                    <text x={26} y={38} className="fill-[var(--muted)] text-[10px]">
                      {statusLabel(node.task.status)}
                      {node.locked ? text("lock.suffix") : ""}
                    </text>
                  </a>
                </g>
              ))}
            </svg>
          </div>
          <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
            <h2 className="text-sm font-semibold tracking-wide uppercase">{text("roadmap.links")}</h2>
            <ul className="space-y-2 text-sm">
              {dependencies.map((edge) => {
                const from = tasks.find((task) => task.id === edge.from_task_id);
                const to = tasks.find((task) => task.id === edge.to_task_id);
                return (
                  <li
                    key={`${edge.from_task_id}-${edge.to_task_id}-${edge.type}`}
                    className="flex flex-wrap items-baseline gap-x-2 gap-y-1"
                  >
                    <Link className="font-medium underline-offset-2 hover:underline" href={`/app/tasks/${edge.from_task_id}`}>
                      {from?.title ?? edge.from_task_id}
                    </Link>
                    <span className="text-muted">
                      {edge.type === "blocks" ? text("roadmap.blocks") : text("roadmap.relatesTo")}
                    </span>
                    <Link className="font-medium underline-offset-2 hover:underline" href={`/app/tasks/${edge.to_task_id}`}>
                      {to?.title ?? edge.to_task_id}
                    </Link>
                  </li>
                );
              })}
            </ul>
            <h2 className="text-sm font-semibold tracking-wide uppercase">{text("roadmap.addLink")}</h2>
            <AddDependencyForm
              key={tasks[0]?.project_id ?? "none"}
              tasks={tasks}
              onCreated={onCreated}
              onCycle={onCycle}
            />
          </article>
        </>
      )}
    </div>
  );
}

function AddDependencyForm({
  tasks,
  onCreated,
  onCycle,
}: {
  tasks: PublicTask[];
  onCreated: () => void;
  onCycle: (message: string) => void;
}) {
  const label = useT();
  const ids = new Set(tasks.map((task) => task.id));
  const [fromTaskId, setFromTaskId] = useState(tasks[0]?.id ?? "");
  const [toTaskId, setToTaskId] = useState(tasks[1]?.id ?? tasks[0]?.id ?? "");
  const [type, setType] = useState<DependencyType>("blocks");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const fromValue = ids.has(fromTaskId) ? fromTaskId : (tasks[0]?.id ?? "");
  const toValue = ids.has(toTaskId) ? toTaskId : (tasks[1]?.id ?? tasks[0]?.id ?? "");

  if (tasks.length < 2) {
    return <p className="text-sm text-muted">{label("roadmap.needTwo")}</p>;
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!fromValue || !toValue) {
      setError(t("roadmap.chooseTwo"));
      return;
    }
    setPending(true);
    setError(null);
    try {
      await createTaskDependency(fromValue, toValue, type, newIdempotencyKey());
      onCreated();
    } catch (caught) {
      if (caught instanceof ApiError && caught.code === "dependency_cycle") {
        onCycle(t("roadmap.cycle"));
        setError(t("roadmap.cycle"));
      } else {
        setError(caught instanceof ApiError ? caught.message : t("roadmap.failedAdd"));
      }
    } finally {
      setPending(false);
    }
  }

  return (
    <form className="flex flex-wrap items-end gap-2" onSubmit={onSubmit}>
      <label className="flex min-w-40 flex-1 flex-col gap-1 text-xs text-muted">
        {label("roadmap.thisTask")}
        <select
          className="h-9 rounded-md border border-border bg-background px-2 text-sm text-foreground"
          value={fromValue}
          onChange={(event) => setFromTaskId(event.target.value)}
        >
          {tasks.map((task) => (
            <option key={task.id} value={task.id}>
              {task.title}
            </option>
          ))}
        </select>
      </label>
      <label className="flex min-w-28 flex-col gap-1 text-xs text-muted">
        {label("roadmap.type")}
        <select
          className="h-9 rounded-md border border-border bg-background px-2 text-sm text-foreground capitalize"
          value={type}
          onChange={(event) => setType(event.target.value as DependencyType)}
        >
          <option value="blocks">{label("roadmap.blocks")}</option>
          <option value="relates">{label("roadmap.relates")}</option>
        </select>
      </label>
      <label className="flex min-w-40 flex-1 flex-col gap-1 text-xs text-muted">
        {label("roadmap.thatTask")}
        <select
          className="h-9 rounded-md border border-border bg-background px-2 text-sm text-foreground"
          value={toValue}
          onChange={(event) => setToTaskId(event.target.value)}
        >
          {tasks.map((task) => (
            <option key={task.id} value={task.id}>
              {task.title}
            </option>
          ))}
        </select>
      </label>
      <button
        className="h-9 rounded-md bg-accent px-3 text-sm font-medium text-accent-fg disabled:opacity-60"
        type="submit"
        disabled={pending}
      >
        {pending ? label("common.saving") : label("roadmap.addLink")}
      </button>
      {error ? <p className="w-full text-sm text-red-600">{error}</p> : null}
    </form>
  );
}

type LaidOutNode = {
  task: PublicTask;
  x: number;
  y: number;
  width: number;
  height: number;
  locked: boolean;
};

type LaidOutEdge = {
  from: string;
  to: string;
  type: DependencyType;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
};

function layoutGraph(tasks: PublicTask[], dependencies: PublicDependency[]) {
  const nodeWidth = 220;
  const nodeHeight = 52;
  const columnGap = 48;
  const rowGap = 20;
  const padding = 24;
  const byId = new Map(tasks.map((task) => [task.id, task]));
  const nodes = [...tasks].sort((a, b) => a.title.localeCompare(b.title));

  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, number>();
  for (const node of nodes) {
    outgoing.set(node.id, []);
    incoming.set(node.id, 0);
  }
  for (const edge of dependencies) {
    if (
      edge.type !== "blocks" ||
      !incoming.has(edge.from_task_id) ||
      !incoming.has(edge.to_task_id)
    ) {
      continue;
    }
    outgoing.get(edge.from_task_id)?.push(edge.to_task_id);
    incoming.set(edge.to_task_id, (incoming.get(edge.to_task_id) ?? 0) + 1);
  }

  const columns: string[][] = [];
  const remaining = new Set(nodes.map((node) => node.id));
  let frontier = nodes.filter((node) => (incoming.get(node.id) ?? 0) === 0).map((node) => node.id);
  while (remaining.size > 0) {
    if (frontier.length === 0) {
      const next = [...remaining].sort((a, b) => {
        const left = byId.get(a)?.title ?? a;
        const right = byId.get(b)?.title ?? b;
        return left.localeCompare(right);
      })[0];
      if (!next) {
        break;
      }
      frontier = [next];
    }
    columns.push(frontier);
    const nextFrontier: string[] = [];
    for (const id of frontier) {
      remaining.delete(id);
      for (const child of outgoing.get(id) ?? []) {
        if (!remaining.has(child)) {
          continue;
        }
        const nextCount = (incoming.get(child) ?? 1) - 1;
        incoming.set(child, nextCount);
        if (nextCount <= 0) {
          nextFrontier.push(child);
        }
      }
    }
    frontier = [...new Set(nextFrontier)].sort((a, b) => {
      const left = byId.get(a)?.title ?? a;
      const right = byId.get(b)?.title ?? b;
      return left.localeCompare(right);
    });
  }

  const positions = new Map<string, LaidOutNode>();
  columns.forEach((column, columnIndex) => {
    column.forEach((id, rowIndex) => {
      const task = byId.get(id);
      if (!task) {
        return;
      }
      positions.set(id, {
        task,
        x: padding + columnIndex * (nodeWidth + columnGap),
        y: padding + rowIndex * (nodeHeight + rowGap),
        width: nodeWidth,
        height: nodeHeight,
        locked: isTaskLocked(task),
      });
    });
  });

  const laidNodes = [...positions.values()];
  const edges: LaidOutEdge[] = [];
  for (const edge of dependencies) {
    const from = positions.get(edge.from_task_id);
    const to = positions.get(edge.to_task_id);
    if (!from || !to) {
      continue;
    }
    edges.push({
      from: edge.from_task_id,
      to: edge.to_task_id,
      type: edge.type,
      x1: from.x + from.width,
      y1: from.y + from.height / 2,
      x2: to.x,
      y2: to.y + to.height / 2,
    });
  }

  const width = Math.max(...laidNodes.map((node) => node.x + node.width), nodeWidth) + padding;
  const height = Math.max(...laidNodes.map((node) => node.y + node.height), nodeHeight) + padding;
  return { nodes: laidNodes, edges, width, height };
}

function compareOptionalDates(left: string | null, right: string | null): number {
  if (left && right) {
    return left.localeCompare(right);
  }
  if (left) {
    return -1;
  }
  if (right) {
    return 1;
  }
  return 0;
}

function truncateLabel(value: string, max: number): string {
  if (value.length <= max) {
    return value;
  }
  return `${value.slice(0, max - 1)}…`;
}
