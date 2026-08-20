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
    description: "Comments, activity, and a session brief preview live on the task.",
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

const SEED_TITLES = new Set<string>(SEED_TASKS.map((seed) => seed.title));

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
  const existingTitles = new Set(tasks.map((task) => task.title));
  const hasUserTasks = tasks.some((task) => !SEED_TITLES.has(task.title));
  if (hasUserTasks) {
    return;
  }
  const missing = SEED_TASKS.filter((seed) => !existingTitles.has(seed.title));
  if (missing.length === 0 && milestones.some((item) => item.title === SEED_MILESTONE_TITLE)) {
    return;
  }

  let milestone = milestones.find((item) => item.title === SEED_MILESTONE_TITLE);
  if (!milestone) {
    milestone = await createMilestone(
      projectId,
      {
        title: SEED_MILESTONE_TITLE,
        description: "First human + agent loop on Beacon itself.",
      },
      seedKey(projectId, "milestone"),
    );
    const refreshed = await fetchProjectMilestones(projectId);
    milestone = refreshed.find((item) => item.title === SEED_MILESTONE_TITLE) ?? milestone;
  }

  for (const seed of missing) {
    const latest = await fetchProjectTasks(projectId);
    if (latest.some((task) => task.title === seed.title)) {
      continue;
    }
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
