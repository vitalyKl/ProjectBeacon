import { describe, expect, it } from "vitest";

import { finishWorkActivities, type AgentSessionRecord } from "./types.js";
import type { TaskRecord } from "../roadmap/types.js";

const NOW = new Date("2026-01-01T00:00:00.000Z");

const session: AgentSessionRecord = {
  id: "018f1e2c-3d4e-7000-8000-0000000000a1",
  projectId: "018f1e2c-3d4e-7000-8000-0000000000a2",
  taskId: "018f1e2c-3d4e-7000-8000-0000000000a3",
  tokenId: null,
  agentName: "agent",
  agentHost: "custom",
  status: "finished",
  contextRevisionId: null,
  startedAt: NOW,
  finishedAt: NOW,
  lockExpiresAt: null,
  lastHeartbeatAt: NOW,
};

const task = {
  id: "018f1e2c-3d4e-7000-8000-0000000000a3",
  status: "done",
} as TaskRecord;

describe("finishWorkActivities", () => {
  it("writes finish, status, and lock_released together", () => {
    const events = finishWorkActivities({
      session,
      task,
      previousStatus: "ready",
      lockReleased: true,
      taskStatus: "done",
      actorType: "token",
      actorId: "018f1e2c-3d4e-7000-8000-0000000000a4",
      now: NOW,
    });
    expect(events.map((event) => event.verb)).toEqual(["finish_work", "status", "lock_released"]);
    expect(events.every((event) => event.projectId === session.projectId)).toBe(true);
  });

  it("skips status and lock events when the task did not change", () => {
    const events = finishWorkActivities({
      session,
      task: null,
      previousStatus: null,
      lockReleased: false,
      taskStatus: "done",
      actorType: "user",
      actorId: "018f1e2c-3d4e-7000-8000-0000000000a5",
      now: NOW,
    });
    expect(events.map((event) => event.verb)).toEqual(["finish_work"]);
  });
});
