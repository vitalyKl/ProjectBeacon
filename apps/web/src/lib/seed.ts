import { createMilestone, createTask, fetchProjectMilestones, fetchProjectTasks } from "./roadmap";

const SEED_MILESTONE_TITLE = "Dogfood A";

const SEED_TASKS = [
  {
    key: "home-board",
    title: "Ship Home, Board, and Backlog",
    description: "Humans can see milestones, move work, and keep the board live.",
    status: "in_progress" as const,
  },
  {
    key: "task-detail",
    title: "Open a task and preview the session brief",
    description: "Comments, activity, lock badge, and fail-soft brief preview on the task.",
    status: "ready" as const,
  },
  {
    key: "beacon-mcp",
    title: "Connect an agent with beacon mcp",
    description: "Mint a project token and run Beacon tasks from a local agent.",
    status: "backlog" as const,
  },
  {
    key: "handoff",
    title: "Leave a handoff the next session can use",
    description: "Finish work with a summary so the next agent does not start from zero.",
    status: "backlog" as const,
  },
] as const;

function seedKey(projectId: string, suffix: string): string {
  return `beacon-dogfood:${projectId}:${suffix}`;
}

const inflight = new Map<string, Promise<void>>();

export async function ensureBeaconSeed(projectId: string): Promise<void> {
  const existing = inflight.get(projectId);
  if (existing) {
    return existing;
  }
  const run = seedProject(projectId).finally(() => {
    inflight.delete(projectId);
  });
  inflight.set(projectId, run);
  return run;
}

async function seedProject(projectId: string): Promise<void> {
  const [milestones, tasks] = await Promise.all([
    fetchProjectMilestones(projectId),
    fetchProjectTasks(projectId),
  ]);
  if (tasks.length > 0) {
    return;
  }

  let milestone = milestones.find((item) => item.title === SEED_MILESTONE_TITLE);
  if (!milestone) {
    milestone = await createMilestone(
      projectId,
      SEED_MILESTONE_TITLE,
      "First human + agent loop on Beacon itself.",
    );
  }

  for (const seed of SEED_TASKS) {
    await createTask(
      projectId,
      {
        title: seed.title,
        description: seed.description,
        status: seed.status,
        type: "task",
        milestone_id: milestone.id,
      },
      seedKey(projectId, seed.key),
    );
  }
}
