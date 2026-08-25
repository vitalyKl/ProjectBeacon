import { observeJob, uuidv7 } from "@beacon/shared";

export const DETECT_QUEUE = "detect";
export const GITHUB_IMPORT_QUEUE = "github_import";
export const GITHUB_INVALIDATE_QUEUE = "github_invalidate";
export const WEBHOOK_DELIVERY_QUEUE = "webhook_delivery";

export type DetectJobData = {
  repo_id: string;
  project_id: string;
};

export type GithubImportJobData = {
  repo_id: string;
  project_id: string;
  issue_number?: number;
};

export type GithubInvalidateJobData = {
  repo_id: string;
  project_id: string;
  ref?: string;
  before?: string;
  after?: string;
};

export type WebhookDeliveryJobData = {
  project_id: string;
  task_id: string;
  webhook_url: string;
  previous_status: string;
  new_status: string;
  agent_name: string;
};

export type JobQueue = {
  enqueueDetect(data: DetectJobData, options?: { singletonKey?: string }): Promise<string>;
  enqueueGithubImport(
    data: GithubImportJobData,
    options?: { singletonKey?: string },
  ): Promise<string>;
  enqueueGithubInvalidate(
    data: GithubInvalidateJobData,
    options?: { singletonKey?: string },
  ): Promise<string>;
  enqueueWebhookDelivery(data: WebhookDeliveryJobData): Promise<string>;
};

function enqueueMemory<T>(
  jobs: Array<{ id: string; data: T }>,
  data: T,
  options?: { singletonKey?: string },
): string {
  if (options?.singletonKey) {
    const existing = jobs.find((job) => job.id === options.singletonKey);
    if (existing) {
      return existing.id;
    }
  }
  const id = options?.singletonKey ?? uuidv7();
  jobs.push({ id, data });
  return id;
}

export class MemoryJobQueue implements JobQueue {
  readonly detectJobs: Array<{ id: string; data: DetectJobData }> = [];
  readonly githubImportJobs: Array<{ id: string; data: GithubImportJobData }> = [];
  readonly githubInvalidateJobs: Array<{ id: string; data: GithubInvalidateJobData }> = [];
  readonly webhookDeliveryJobs: Array<{ id: string; data: WebhookDeliveryJobData }> = [];

  async enqueueDetect(data: DetectJobData, options?: { singletonKey?: string }): Promise<string> {
    if (options?.singletonKey) {
      const existing = this.detectJobs.find((job) => job.id === options.singletonKey);
      if (existing) {
        observeJob(DETECT_QUEUE, "duplicate", 0);
        return existing.id;
      }
    }
    const id = enqueueMemory(this.detectJobs, data, options);
    observeJob(DETECT_QUEUE, "enqueued", 0);
    return id;
  }

  async enqueueGithubImport(
    data: GithubImportJobData,
    options?: { singletonKey?: string },
  ): Promise<string> {
    return enqueueMemory(this.githubImportJobs, data, options);
  }

  async enqueueGithubInvalidate(
    data: GithubInvalidateJobData,
    options?: { singletonKey?: string },
  ): Promise<string> {
    return enqueueMemory(this.githubInvalidateJobs, data, options);
  }

  async enqueueWebhookDelivery(data: WebhookDeliveryJobData): Promise<string> {
    return enqueueMemory(this.webhookDeliveryJobs, data, undefined);
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
    observeJob(DETECT_QUEUE, id ? "enqueued" : "duplicate", 0);
    return id ?? options?.singletonKey ?? uuidv7();
  }

  async enqueueGithubImport(
    data: GithubImportJobData,
    options?: { singletonKey?: string },
  ): Promise<string> {
    const id = await this.send(GITHUB_IMPORT_QUEUE, data, options);
    return id ?? options?.singletonKey ?? uuidv7();
  }

  async enqueueGithubInvalidate(
    data: GithubInvalidateJobData,
    options?: { singletonKey?: string },
  ): Promise<string> {
    const id = await this.send(GITHUB_INVALIDATE_QUEUE, data, options);
    return id ?? options?.singletonKey ?? uuidv7();
  }

  async enqueueWebhookDelivery(data: WebhookDeliveryJobData): Promise<string> {
    const id = await this.send(WEBHOOK_DELIVERY_QUEUE, data);
    return id ?? uuidv7();
  }
}
