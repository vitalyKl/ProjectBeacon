export const packageName = "@beacon/shared";

export type { ActorRef, ActorType } from "./actor.js";
export {
  AnomalyTracker,
  anomalyTracker,
  CODE_VOLUME_MULTIPLIER,
  evaluateCodeVolumeAnomaly,
  evaluateGetFileLineAnomaly,
  GET_FILE_LINE_THRESHOLD,
  GET_FILE_LINE_WINDOW_MS,
  type Anomaly,
} from "./anomaly.js";
export {
  CursorError,
  decodeCursor,
  encodeCursor,
  isIsoTimestamp,
  tryDecodeCursor,
  type CursorDecodeResult,
  type CursorPayload,
} from "./cursor.js";
export { ERROR_CODES, isErrorCode, type ErrorCode } from "./error-codes.js";
export { isUuid, isUuidV7, uuidv7 } from "./ids.js";
export { redactLogValue, serializeLog, writeLog, type LogFields, type LogLevel } from "./log.js";
export {
  addGetFileBytes,
  COMPILE_DURATION_BUCKETS,
  HTTP_DURATION_BUCKETS,
  INDEX_DURATION_BUCKETS,
  JOB_DURATION_BUCKETS,
  MCP_DURATION_BUCKETS,
  MetricsRegistry,
  metrics,
  incRateLimited,
  observeCompile,
  observeHttp,
  observeIndex,
  observeJob,
  observeMcpTool,
  setApprovalPending,
  setGithubSyncLagSeconds,
  setSidecarConnected,
  type HistogramSnapshot,
  type MetricsSnapshot,
} from "./metrics.js";
export {
  endSpan,
  isCodeHttpRoute,
  isCodeToolName,
  loadOtelConfig,
  newSpanId,
  newTraceId,
  parseBoolEnv,
  shouldSampleSuccess,
  startSpan,
  withSpan,
  type OtelConfig,
  type Span,
  type SpanStatus,
} from "./otel.js";
export { PAGINATION_DEFAULT_LIMIT, PAGINATION_MAX_LIMIT, type Page } from "./pagination.js";
export { DEFAULT_TOKEN_SCOPES, isScope, SCOPES, type Scope } from "./scopes.js";
export { jsLengthDiv4, TOKENIZER_ID } from "./tokenizer.js";
