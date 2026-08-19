import type { ContextNode, ContextSection } from "./api";
import { compareTaskPriority } from "./priority";
import { isTaskLocked, type PublicTask, type TaskStatus } from "./roadmap";

/** Agents pick work up with start_work once the task is ready and unlocked. */
export const AGENT_STARTABLE_STATUSES = ["ready"] as const satisfies readonly TaskStatus[];

export type AgentStartableStatus = (typeof AGENT_STARTABLE_STATUSES)[number];

export function isAgentStartableStatus(status: string): status is AgentStartableStatus {
  return (AGENT_STARTABLE_STATUSES as readonly string[]).includes(status);
}

export function isOfferedToAgents(task: PublicTask, now = Date.now()): boolean {
  if (task.deleted_at) {
    return false;
  }
  if (!isAgentStartableStatus(task.status)) {
    return false;
  }
  return !isTaskLocked(task, now);
}

export function offeredTasks(tasks: readonly PublicTask[], now = Date.now()): PublicTask[] {
  return tasks
    .filter((task) => isOfferedToAgents(task, now))
    .slice()
    .sort(compareTaskPriority);
}

export function sectionsWithBody(sections: readonly ContextSection[]): ContextSection[] {
  return sections
    .filter((section) => section.body_md.trim().length > 0)
    .slice()
    .sort((left, right) => left.ordinal - right.ordinal);
}

export function pickDisplayBriefNode(nodes: readonly ContextNode[]): ContextNode | null {
  const withBody = nodes.filter((node) => sectionsWithBody(node.sections).length > 0);
  if (withBody.length === 0) {
    return null;
  }
  const projectScoped = withBody.filter((node) => node.scope_type === "project");
  const pool = projectScoped.length > 0 ? projectScoped : withBody;
  const reviewed = pool.filter((node) => node.review_state === "reviewed");
  const ranked = (reviewed.length > 0 ? reviewed : pool).slice().sort((left, right) => {
    if (left.updated_at !== right.updated_at) {
      return left.updated_at < right.updated_at ? 1 : -1;
    }
    return left.id < right.id ? 1 : left.id > right.id ? -1 : 0;
  });
  return ranked[0] ?? null;
}
