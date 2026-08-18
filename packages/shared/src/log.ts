export type LogLevel = "debug" | "info" | "warn" | "error";

export type LogFields = {
  level?: LogLevel;
  msg: string;
  trace_id?: string;
  project_id?: string;
  actor_type?: string;
  route?: string;
  tool?: string;
  duration_ms?: number;
  error?: { code?: string; message?: string };
  [key: string]: unknown;
};

const REDACTED = "[redacted]";
const SENSITIVE_KEY = /^(authorization|cookie|set-cookie|x-api-key|token|password|secret)$/i;
const FILE_BODY_KEY = /^(content|body|file_body|brief_markdown|brief_json)$/i;

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

export function redactLogValue(key: string, value: unknown): unknown {
  if (SENSITIVE_KEY.test(key) || FILE_BODY_KEY.test(key)) {
    return REDACTED;
  }
  if (Array.isArray(value)) {
    return value.map((item) => redactLogValue(key, item));
  }
  if (!isPlainObject(value)) {
    return value;
  }
  const out: Record<string, unknown> = {};
  for (const [childKey, child] of Object.entries(value)) {
    out[childKey] = redactLogValue(childKey, child);
  }
  return out;
}

export function serializeLog(fields: LogFields): string {
  const level = fields.level ?? "info";
  const payload: Record<string, unknown> = { level, msg: fields.msg };
  for (const [key, value] of Object.entries(fields)) {
    if (key === "level" || key === "msg" || value === undefined) {
      continue;
    }
    payload[key] = redactLogValue(key, value);
  }
  return JSON.stringify(payload);
}

export function writeLog(
  fields: LogFields,
  stream: { write(chunk: string): void } = process.stderr,
): void {
  const level = fields.level ?? "info";
  stream.write(`${serializeLog({ ...fields, level })}\n`);
}
