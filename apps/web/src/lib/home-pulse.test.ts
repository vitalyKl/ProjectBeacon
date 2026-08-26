import { describe, expect, it } from "vitest";

import type { ContextSection, PublicActivityEvent, PublicAgentSession } from "./api";
import {
  countActiveSessions,
  countHomeQueue,
  countOpenMilestones,
  HOME_PULSE_SECTION_IDS,
  lastActivityEvent,
  peekReadyTasks,
  pickPulseBriefSections,
} from "./home-pulse";
import type { PublicMilestone, PublicTask } from "./roadmap";

function section(overrides: Partial<ContextSection> = {}): ContextSection {
  return {
    id: "goals",
    title: "Goals",
    body_md: "Keep a living brief.",
    ordinal: 0,
    ...overrides,
  };
}

function task(overrides: Partial<PublicTask> = {}): PublicTask {
  return {
    id: "task-1",
    project_id: "proj-1",
    milestone_id: null,
    parent_id: null,
    title: "Ready work",
    description: "",
    status: "ready",
    priority: 2,
    type: "task",
    version: 1,
    assignee_user_id: null,
    assignee_agent_name: null,
    agent_brief: "",
    how_to_check: "",
    linked_paths: [],
    labels: [],
    github_issue_id: null,
    locked_by_session_id: null,
    lock_expires_at: null,
    deleted_at: null,
    created_at: "2026-08-18T12:00:00.000Z",
    updated_at: "2026-08-18T12:00:00.000Z",
    ...overrides,
  };
}

function milestone(overrides: Partial<PublicMilestone> = {}): PublicMilestone {
  return {
    id: "ms-1",
    project_id: "proj-1",
    title: "First slice",
    description: "",
    status: "open",
    target_date: null,
    sort_order: 0,
    created_at: "2026-08-18T12:00:00.000Z",
    ...overrides,
  };
}

function session(overrides: Partial<PublicAgentSession> = {}): PublicAgentSession {
  return {
    id: "sess-1",
    project_id: "proj-1",
    task_id: null,
    agent: { id: "agent-1", name: "local", host: "dev" },
    status: "active",
    context_revision_id: null,
    started_at: "2026-08-18T12:00:00.000Z",
    finished_at: null,
    lock_expires_at: null,
    last_heartbeat_at: "2026-08-18T12:00:00.000Z",
    ...overrides,
  };
}

function activity(overrides: Partial<PublicActivityEvent> = {}): PublicActivityEvent {
  return {
    id: "act-1",
    project_id: "proj-1",
    object_type: "task",
    object_id: "task-1",
    actor_type: "agent",
    actor_id: "agent-1",
    verb: "start_work",
    payload: {},
    created_at: "2026-08-18T12:00:00.000Z",
    ...overrides,
  };
}

describe("pickPulseBriefSections", () => {
  it("picks Goals and Definition of Done by known ids, not the first two sections", () => {
    expect(HOME_PULSE_SECTION_IDS).toEqual(["goals", "definition_of_done"]);
    const architecture = section({
      id: "architecture",
      title: "Architecture",
      body_md: "Do not pick this just because it is first.",
      ordinal: 0,
    });
    const dod = section({
      id: "definition_of_done",
      title: "Definition of Done",
      body_md: "Tests are green.",
      ordinal: 1,
    });
    const emptyGoals = section({ id: "goals", body_md: "  ", ordinal: 2 });
    const goals = section({ id: "goals", body_md: "Ship the pulse.", ordinal: 5 });
    expect(pickPulseBriefSections([architecture, dod, emptyGoals])).toEqual([dod]);
    expect(pickPulseBriefSections([architecture, dod, goals]).map((item) => item.id)).toEqual([
      "goals",
      "definition_of_done",
    ]);
    expect(pickPulseBriefSections([architecture])).toEqual([]);
  });
});

describe("home queue pulse", () => {
  it("counts living queue statuses and peeks at most three ready tasks", () => {
    const readyA = task({ id: "a", priority: 3, updated_at: "2026-08-18T14:00:00.000Z" });
    const readyB = task({ id: "b", priority: 1, updated_at: "2026-08-18T15:00:00.000Z" });
    const readyC = task({ id: "c", priority: 1, updated_at: "2026-08-18T13:00:00.000Z" });
    const readyD = task({ id: "d", priority: 0 });
    const inProgress = task({ id: "ip", status: "in_progress" });
    const inReview = task({ id: "ir", status: "in_review" });
    const deleted = task({
      id: "gone",
      status: "in_progress",
      deleted_at: "2026-08-18T16:00:00.000Z",
    });
    const backlog = task({ id: "backlog", status: "backlog" });
    const lockedReady = task({
      id: "locked",
      locked_by_session_id: "sess-1",
      lock_expires_at: "2099-01-01T00:00:00.000Z",
    });
    const tasks = [
      readyA,
      readyB,
      readyC,
      readyD,
      inProgress,
      inReview,
      deleted,
      backlog,
      lockedReady,
    ];
    expect(countHomeQueue(tasks)).toEqual({ ready: 4, inProgress: 1, inReview: 1 });
    expect(peekReadyTasks(tasks).map((item) => item.id)).toEqual(["d", "b", "c"]);
  });
});

describe("milestone and agent pulse", () => {
  it("counts only open milestones and active sessions, and takes the first activity verb", () => {
    expect(
      countOpenMilestones([
        milestone({ id: "open-1" }),
        milestone({ id: "closed", status: "closed" }),
        milestone({ id: "open-2" }),
      ]),
    ).toBe(2);
    expect(
      countActiveSessions([
        session({ id: "live" }),
        session({ id: "done", status: "finished" }),
        session({ id: "live-2", status: "active" }),
      ]),
    ).toBe(2);
    const first = activity({ id: "first", verb: "start_work" });
    const second = activity({ id: "second", verb: "finish_work" });
    expect(lastActivityEvent([first, second])?.verb).toBe("start_work");
    expect(lastActivityEvent([])).toBeNull();
  });
});
