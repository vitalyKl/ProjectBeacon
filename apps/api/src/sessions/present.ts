import type { BriefHandoff } from "@beacon/api-spec";

import type { AgentSessionRecord, HandoffRecord } from "./types.js";

export function presentSession(session: AgentSessionRecord, taskTitle?: string | null) {
  return {
    id: session.id,
    project_id: session.projectId,
    task_id: session.taskId,
    task_title: taskTitle ?? null,
    agent: {
      id: session.tokenId ?? session.id,
      name: session.agentName,
      host: session.agentHost,
    },
    status: session.status,
    context_revision_id: session.contextRevisionId,
    started_at: session.startedAt.toISOString(),
    finished_at: session.finishedAt ? session.finishedAt.toISOString() : null,
    lock_expires_at: session.lockExpiresAt ? session.lockExpiresAt.toISOString() : null,
    last_heartbeat_at: session.lastHeartbeatAt.toISOString(),
  };
}

export function presentHandoff(handoff: HandoffRecord): BriefHandoff {
  return {
    id: handoff.id,
    session_id: handoff.sessionId,
    summary: handoff.summary,
    next_steps: handoff.nextSteps,
    files_touched: handoff.filesTouched.map((path) => ({ repo_id: path.repo_id, path: path.path })),
    open_questions: [...handoff.openQuestions],
    created_at: handoff.createdAt.toISOString(),
  };
}

export function presentHandoffResource(handoff: HandoffRecord) {
  return {
    ...presentHandoff(handoff),
    task_id: handoff.taskId,
  };
}
