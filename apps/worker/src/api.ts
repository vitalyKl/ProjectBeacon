export type RetentionResult = {
  activity_deleted: number;
  briefs_deleted: number;
  idempotency_deleted: number;
  sessions_deleted: number;
};

export type ExpireLocksResult = {
  locks_released: number;
  sessions_abandoned: number;
};

export type WorkerApi = {
  runRetention(): Promise<RetentionResult>;
  expireLocks(): Promise<ExpireLocksResult>;
};

export type WorkerApiOptions = {
  baseUrl: string;
  token: string;
  fetchImpl?: typeof fetch;
};

function requireOk(path: string, res: Response): void {
  if (!res.ok) {
    throw new Error(`${path} failed: ${res.status}`);
  }
}

export function createWorkerApi(options: WorkerApiOptions): WorkerApi {
  const baseUrl = options.baseUrl.replace(/\/+$/, "");
  const fetchImpl = options.fetchImpl ?? fetch;

  async function post<T>(path: string): Promise<T> {
    const res = await fetchImpl(`${baseUrl}${path}`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${options.token}`,
        accept: "application/json",
      },
    });
    requireOk(path, res);
    return (await res.json()) as T;
  }

  return {
    runRetention: () => post<RetentionResult>("/v1/jobs/retention"),
    expireLocks: () => post<ExpireLocksResult>("/v1/jobs/expire-locks"),
  };
}

export function loadWorkerEnv(env: NodeJS.ProcessEnv = process.env): {
  databaseUrl: string | undefined;
  api: WorkerApiOptions;
} {
  return {
    databaseUrl: env["DATABASE_URL"],
    api: {
      baseUrl: env["BEACON_API_URL"] ?? "http://127.0.0.1:8080",
      token: env["BEACON_WORKER_TOKEN"] ?? "",
    },
  };
}
