import type { ContextSection, PublicActivityEvent, PublicAgentSession } from "./api";
import { offeredTasks } from "./brief";
import type { PublicMilestone, PublicTask } from "./roadmap";

export const HOME_PULSE_SECTION_IDS = ["goals", "definition_of_done"] as const;

export const HOME_READY_PEEK = 3;

export type HomeQueueCounts = {
  ready: number;
  inProgress: number;
  inReview: number;
};

export function pickPulseBriefSections(sections: readonly ContextSection[]): ContextSection[] {
  const byId = new Map(sections.map((section) => [section.id, section]));
  return HOME_PULSE_SECTION_IDS.flatMap((id) => {
    const section = byId.get(id);
    if (!section || section.body_md.trim().length === 0) {
      return [];
    }
    return [section];
  });
}

export function countHomeQueue(tasks: readonly PublicTask[], now = Date.now()): HomeQueueCounts {
  return {
    ready: offeredTasks(tasks, now).length,
    inProgress: tasks.filter((task) => !task.deleted_at && task.status === "in_progress").length,
    inReview: tasks.filter((task) => !task.deleted_at && task.status === "in_review").length,
  };
}

export function peekReadyTasks(
  tasks: readonly PublicTask[],
  limit = HOME_READY_PEEK,
  now = Date.now(),
): PublicTask[] {
  return offeredTasks(tasks, now).slice(0, limit);
}

export function countOpenMilestones(milestones: readonly PublicMilestone[]): number {
  return milestones.filter((item) => item.status === "open").length;
}

export function countActiveSessions(sessions: readonly PublicAgentSession[]): number {
  return sessions.filter((session) => session.status === "active").length;
}

export function lastActivityEvent(
  activity: readonly PublicActivityEvent[],
): PublicActivityEvent | null {
  return activity[0] ?? null;
}
