import { uuidv7 } from "@beacon/shared";

export const DETECT_QUEUE = "detect";

export type DetectJobData = {
  repo_id: string;
  project_id: string;
};

export type JobQueue = {
  enqueueDetect(data: DetectJobData, options?: { singletonKey?: string }): Promise<string>;
};

export class MemoryJobQueue implements JobQueue {
  readonly detectJobs: Array<{ id: string; data: DetectJobData }> = [];

  async enqueueDetect(data: DetectJobData, options?: { singletonKey?: string }): Promise<string> {
    if (options?.singletonKey) {
      const existing = this.detectJobs.find((job) => job.id === options.singletonKey);
      if (existing) {
        return existing.id;
      }
    }
    const id = options?.singletonKey ?? uuidv7();
    this.detectJobs.push({ id, data });
    return id;
  }
}

export class PgBossJobQueue implements JobQueue {
  constructor(
    private readonly send: (
      name: string,
      data: object,
      options?: { singletonKey?: string },
    ) => Promise<string | null>,
  ) {}

  async enqueueDetect(data: DetectJobData, options?: { singletonKey?: string }): Promise<string> {
    const id = await this.send(DETECT_QUEUE, data, options);
    return id ?? options?.singletonKey ?? uuidv7();
  }
}
