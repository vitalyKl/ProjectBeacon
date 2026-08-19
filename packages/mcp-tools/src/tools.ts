import {
  ConstraintKindSchema,
  LinkedPathSchema,
  TaskStatusSchema,
  TaskTypeSchema,
  UuidSchema,
} from "@beacon/api-spec";
import { PAGINATION_DEFAULT_LIMIT, PAGINATION_MAX_LIMIT } from "@beacon/shared";
import { z } from "zod";

export const TOOL_NAMES = [
  "get_project",
  "get_context_pack",
  "search_context",
  "get_task_brief",
  "list_milestones",
  "list_tasks",
  "get_task",
  "create_task",
  "update_task",
  "list_comments",
  "add_comment",
  "set_status",
  "link_dependency",
  "list_decisions",
  "record_decision",
  "list_labels",
  "propose_label",
  "set_task_labels",
  "get_constraints",
  "create_constraint",
  "apply_constraint",
  "get_tree",
  "search_code",
  "get_file",
  "get_symbol",
  "get_owners",
  "get_related_files",
  "get_changed_scope",
  "start_work",
  "finish_work",
  "write_handoff",
  "get_handoff",
  "github_list_prs",
  "github_list_issues",
  "github_link_issue",
  "github_sync_now",
  "list_reports",
  "generate_report",
  "get_report",
  "list_reviews",
  "import_review",
  "get_review",
] as const;

export type ToolName = (typeof TOOL_NAMES)[number];

export const IDEMPOTENT_TOOLS = [
  "create_task",
  "add_comment",
  "record_decision",
  "create_constraint",
  "start_work",
] as const;

export type IdempotentTool = (typeof IDEMPOTENT_TOOLS)[number];

export const CODE_TOOLS = [
  "get_tree",
  "search_code",
  "get_file",
  "get_symbol",
  "get_owners",
  "get_related_files",
  "get_changed_scope",
] as const;

export type CodeTool = (typeof CODE_TOOLS)[number];

export const GITHUB_TOOLS = [
  "github_list_prs",
  "github_list_issues",
  "github_link_issue",
  "github_sync_now",
] as const;

export type GithubTool = (typeof GITHUB_TOOLS)[number];

const IdempotencyKeySchema = z.string().min(1).max(256);
const OptionalProjectId = UuidSchema.optional();
const OptionalRepoId = UuidSchema.optional();
const OptionalPath = z.string().min(1).max(1024).optional();
const LimitSchema = z.coerce.number().int().min(1).max(PAGINATION_MAX_LIMIT).optional();
const CursorSchema = z.string().min(1).optional();
const BudgetTokensSchema = z.number().int().positive().optional();

export const GetProjectArgsSchema = z.object({
  project_id: OptionalProjectId,
});

export const GetContextPackArgsSchema = z.object({
  project_id: OptionalProjectId,
  repo_id: OptionalRepoId,
  path: OptionalPath,
  task_id: UuidSchema.optional(),
  budget_tokens: BudgetTokensSchema,
});

export const SearchContextArgsSchema = z.object({
  q: z.string().min(1),
  project_id: OptionalProjectId,
  limit: LimitSchema,
});

export const GetTaskBriefArgsSchema = z.object({
  task_id: UuidSchema,
  path: OptionalPath,
  budget_tokens: BudgetTokensSchema,
});

export const ListMilestonesArgsSchema = z.object({
  project_id: OptionalProjectId,
  include_closed: z.boolean().optional(),
});

export const ListTasksArgsSchema = z.object({
  project_id: OptionalProjectId,
  milestone_id: UuidSchema.optional(),
  status: z.array(TaskStatusSchema).optional(),
  q: z.string().min(1).optional(),
  assignee: z.string().min(1).optional(),
  label_id: UuidSchema.optional(),
  cursor: CursorSchema,
  limit: LimitSchema,
});

export const GetTaskArgsSchema = z.object({
  task_id: UuidSchema,
});

export const CreateTaskArgsSchema = z.object({
  title: z.string().min(1).max(200),
  idempotency_key: IdempotencyKeySchema,
  project_id: OptionalProjectId,
  description: z.string().max(8000).optional(),
  type: TaskTypeSchema.optional(),
  milestone_id: UuidSchema.nullable().optional(),
  parent_id: UuidSchema.nullable().optional(),
  priority: z.number().int().optional(),
  linked_paths: z.array(LinkedPathSchema).optional(),
  label_ids: z.array(UuidSchema).optional(),
  agent_brief: z.string().max(8000).optional(),
  how_to_check: z.string().max(8000).optional(),
});

export const UpdateTaskArgsSchema = z.object({
  task_id: UuidSchema,
  expected_version: z.number().int().min(1),
  title: z.string().min(1).max(200).optional(),
  description: z.string().max(8000).optional(),
  status: TaskStatusSchema.optional(),
  type: TaskTypeSchema.optional(),
  priority: z.number().int().optional(),
  milestone_id: UuidSchema.nullable().optional(),
  parent_id: UuidSchema.nullable().optional(),
  assignee_user_id: UuidSchema.nullable().optional(),
  assignee_agent_name: z.string().min(1).max(120).nullable().optional(),
  agent_brief: z.string().max(8000).optional(),
  how_to_check: z.string().max(8000).optional(),
  linked_paths: z.array(LinkedPathSchema).optional(),
});

export const ListCommentsArgsSchema = z.object({
  task_id: UuidSchema,
  cursor: CursorSchema,
  limit: LimitSchema,
});

export const AddCommentArgsSchema = z.object({
  task_id: UuidSchema,
  body: z.string().min(1).max(8000),
  idempotency_key: IdempotencyKeySchema,
});

export const SetStatusArgsSchema = z.object({
  task_id: UuidSchema,
  status: TaskStatusSchema,
  expected_version: z.number().int().min(1),
});

export const LinkDependencyArgsSchema = z.object({
  from_task_id: UuidSchema,
  to_task_id: UuidSchema,
  type: z.enum(["blocks", "relates"]),
});

export const ListDecisionsArgsSchema = z.object({
  project_id: OptionalProjectId,
  status: z.enum(["proposed", "accepted", "superseded", "deprecated"]).optional(),
  q: z.string().min(1).optional(),
  path_prefix: z.string().min(1).max(1024).optional(),
  cursor: CursorSchema,
  limit: LimitSchema,
});

export const ListLabelsArgsSchema = z.object({
  project_id: OptionalProjectId,
  cursor: CursorSchema,
  limit: LimitSchema,
});

export const ProposeLabelArgsSchema = z.object({
  name: z.string().min(1).max(80),
  project_id: OptionalProjectId,
  slug: z.string().min(1).max(64).optional(),
  description: z.string().max(400).optional(),
  color: z
    .string()
    .regex(/^#[0-9a-fA-F]{6}$/)
    .optional(),
  paths: z.array(LinkedPathSchema).optional(),
});

export const SetTaskLabelsArgsSchema = z.object({
  task_id: UuidSchema,
  label_ids: z.array(UuidSchema),
});

export const RecordDecisionArgsSchema = z.object({
  title: z.string().min(1).max(200),
  context: z.string().min(1).max(8000),
  decision: z.string().min(1).max(8000),
  idempotency_key: IdempotencyKeySchema,
  project_id: OptionalProjectId,
  consequences: z.string().max(8000).optional(),
  related_task_ids: z.array(UuidSchema).optional(),
  related_paths: z.array(LinkedPathSchema).optional(),
});

export const GetConstraintsArgsSchema = z.object({
  project_id: OptionalProjectId,
  path: OptionalPath,
  active_only: z.boolean().optional(),
});

export const CreateConstraintArgsSchema = z.object({
  kind: ConstraintKindSchema,
  body: z.string().min(1).max(8000),
  idempotency_key: IdempotencyKeySchema,
  project_id: OptionalProjectId,
  scope_path: z.string().max(1024).optional(),
});

export const ApplyConstraintArgsSchema = z.object({
  constraint_id: UuidSchema,
});

export const GetTreeArgsSchema = z.object({
  repo_id: OptionalRepoId,
  path: OptionalPath,
  depth: z.number().int().min(1).max(4).optional(),
});

export const SearchCodeArgsSchema = z.object({
  q: z.string().min(1),
  repo_id: OptionalRepoId,
  mode: z.enum(["symbol", "content", "path", "auto"]).optional(),
  lang: z.string().min(1).optional(),
  path_prefix: z.string().min(1).max(1024).optional(),
  limit: LimitSchema,
});

export const GetFileArgsSchema = z.object({
  path: z.string().min(1).max(1024),
  repo_id: OptionalRepoId,
  start_line: z.number().int().min(1).optional(),
  end_line: z.number().int().min(1).optional(),
});

export const GetSymbolArgsSchema = z.object({
  name: z.string().min(1),
  repo_id: OptionalRepoId,
  path: OptionalPath,
  kind: z.string().min(1).optional(),
});

export const GetOwnersArgsSchema = z.object({
  path: z.string().min(1).max(1024),
  repo_id: OptionalRepoId,
});

export const GetRelatedFilesArgsSchema = z.object({
  path: z.string().min(1).max(1024),
  repo_id: OptionalRepoId,
  limit: LimitSchema,
});

export const GetChangedScopeArgsSchema = z.object({
  task_id: UuidSchema,
  limit: LimitSchema,
});

export const StartWorkArgsSchema = z.object({
  task_id: UuidSchema,
  idempotency_key: IdempotencyKeySchema,
  path: OptionalPath,
  steal: z.boolean().optional(),
  budget_tokens: BudgetTokensSchema,
});

export const FinishWorkArgsSchema = z.object({
  session_id: UuidSchema,
  summary: z.string().min(20).max(8000),
  next_steps: z.string().max(8000).optional(),
  how_to_check: z.string().max(8000).optional(),
  files_touched: z.array(LinkedPathSchema).optional(),
  open_questions: z.array(z.string().min(1).max(800)).optional(),
  status: z.enum(["done", "canceled", "in_review", "blocked", "ready"]).optional(),
});

const WriteHandoffFields = {
  summary: z.string().min(20).max(8000),
  next_steps: z.string().max(8000).optional(),
  files_touched: z.array(LinkedPathSchema).optional(),
  open_questions: z.array(z.string().min(1).max(800)).optional(),
} as const;

export const WriteHandoffArgsSchema = z.union([
  z.object({
    ...WriteHandoffFields,
    session_id: UuidSchema,
    task_id: UuidSchema.optional(),
  }),
  z.object({
    ...WriteHandoffFields,
    task_id: UuidSchema,
    session_id: UuidSchema.optional(),
  }),
]);

export const GetHandoffArgsSchema = z.object({
  task_id: UuidSchema,
});

export const GithubListPrsArgsSchema = z.object({
  repo_id: OptionalRepoId,
  state: z.enum(["open", "closed", "all"]).optional(),
  task_id: UuidSchema.optional(),
});

export const GithubListIssuesArgsSchema = z.object({
  repo_id: OptionalRepoId,
  state: z.enum(["open", "closed", "all"]).optional(),
  q: z.string().min(1).optional(),
});

export const GithubLinkIssueArgsSchema = z.object({
  task_id: UuidSchema,
  issue_number: z.number().int().positive(),
  repo_id: OptionalRepoId,
});

export const ListReportsArgsSchema = z.object({
  project_id: OptionalProjectId,
  cursor: CursorSchema,
  limit: LimitSchema,
});

export const GenerateReportArgsSchema = z.object({
  project_id: OptionalProjectId,
  title: z.string().min(1).max(200).optional(),
});

export const GetReportArgsSchema = z.object({
  report_id: UuidSchema,
});

export const ListReviewsArgsSchema = z.object({
  project_id: OptionalProjectId,
  cursor: CursorSchema,
  limit: LimitSchema,
});

export const ImportReviewArgsSchema = z.object({
  body_md: z.string().min(1).max(32000),
  project_id: OptionalProjectId,
  title: z.string().min(1).max(200).optional(),
  source_path: z.string().min(1).max(1024).optional(),
});

export const GetReviewArgsSchema = z.object({
  review_id: UuidSchema,
});

export const GithubSyncNowArgsSchema = z.object({
  repo_id: OptionalRepoId,
});

export const TOOL_ARG_SCHEMAS = {
  get_project: GetProjectArgsSchema,
  get_context_pack: GetContextPackArgsSchema,
  search_context: SearchContextArgsSchema,
  get_task_brief: GetTaskBriefArgsSchema,
  list_milestones: ListMilestonesArgsSchema,
  list_tasks: ListTasksArgsSchema,
  get_task: GetTaskArgsSchema,
  create_task: CreateTaskArgsSchema,
  update_task: UpdateTaskArgsSchema,
  list_comments: ListCommentsArgsSchema,
  add_comment: AddCommentArgsSchema,
  set_status: SetStatusArgsSchema,
  link_dependency: LinkDependencyArgsSchema,
  list_decisions: ListDecisionsArgsSchema,
  record_decision: RecordDecisionArgsSchema,
  list_labels: ListLabelsArgsSchema,
  propose_label: ProposeLabelArgsSchema,
  set_task_labels: SetTaskLabelsArgsSchema,
  get_constraints: GetConstraintsArgsSchema,
  create_constraint: CreateConstraintArgsSchema,
  apply_constraint: ApplyConstraintArgsSchema,
  get_tree: GetTreeArgsSchema,
  search_code: SearchCodeArgsSchema,
  get_file: GetFileArgsSchema,
  get_symbol: GetSymbolArgsSchema,
  get_owners: GetOwnersArgsSchema,
  get_related_files: GetRelatedFilesArgsSchema,
  get_changed_scope: GetChangedScopeArgsSchema,
  start_work: StartWorkArgsSchema,
  finish_work: FinishWorkArgsSchema,
  write_handoff: WriteHandoffArgsSchema,
  get_handoff: GetHandoffArgsSchema,
  github_list_prs: GithubListPrsArgsSchema,
  github_list_issues: GithubListIssuesArgsSchema,
  github_link_issue: GithubLinkIssueArgsSchema,
  github_sync_now: GithubSyncNowArgsSchema,
  list_reports: ListReportsArgsSchema,
  generate_report: GenerateReportArgsSchema,
  get_report: GetReportArgsSchema,
  list_reviews: ListReviewsArgsSchema,
  import_review: ImportReviewArgsSchema,
  get_review: GetReviewArgsSchema,
} as const;

export type ToolArgSchemas = typeof TOOL_ARG_SCHEMAS;
export type ToolArgs<T extends ToolName> = z.infer<ToolArgSchemas[T]>;

export function isToolName(value: string): value is ToolName {
  return (TOOL_NAMES as readonly string[]).includes(value);
}

export function isIdempotentTool(value: string): value is IdempotentTool {
  return (IDEMPOTENT_TOOLS as readonly string[]).includes(value);
}

export function isCodeTool(value: string): value is CodeTool {
  return (CODE_TOOLS as readonly string[]).includes(value);
}

export function isGithubTool(value: string): value is GithubTool {
  return (GITHUB_TOOLS as readonly string[]).includes(value);
}

export function parseToolArgs<T extends ToolName>(
  tool: T,
  args: unknown,
): { ok: true; data: ToolArgs<T> } | { ok: false } {
  const parsed = TOOL_ARG_SCHEMAS[tool].safeParse(args ?? {});
  if (!parsed.success) {
    return { ok: false };
  }
  return { ok: true, data: parsed.data as ToolArgs<T> };
}

type JsonSchema = Record<string, unknown>;

function asJsonSchema(schema: z.ZodType): JsonSchema {
  const json = { ...z.toJSONSchema(schema, { target: "draft-07" }) } as JsonSchema;
  delete json["$schema"];
  delete json["~standard"];
  return json;
}

function requiredOf(schema: unknown): string[] {
  if (schema === null || typeof schema !== "object" || Array.isArray(schema)) {
    return [];
  }
  const required = (schema as { required?: unknown }).required;
  return Array.isArray(required) ? required.filter((name) => typeof name === "string") : [];
}

function withHandoffTargetAnyOf(schema: JsonSchema): JsonSchema {
  const variants = [schema["anyOf"], schema["oneOf"]].find(Array.isArray);
  if (variants) {
    const covers =
      variants.some((variant) => requiredOf(variant).includes("session_id")) &&
      variants.some((variant) => requiredOf(variant).includes("task_id"));
    if (covers) {
      return schema;
    }
  }
  return {
    ...schema,
    anyOf: [{ required: ["session_id"] }, { required: ["task_id"] }],
  };
}

function toolInputSchema(name: ToolName): JsonSchema {
  const schema = asJsonSchema(TOOL_ARG_SCHEMAS[name]);
  if (name === "write_handoff") {
    return withHandoffTargetAnyOf(schema);
  }
  return schema;
}

export const TOOL_DESCRIPTIONS: Record<ToolName, string> = {
  get_project:
    "Get a project. Pass project_id when this machine has more than one saved Beacon project.",
  get_context_pack: "Compile a project context pack for the current path or task.",
  search_context: "Search project context nodes.",
  get_task_brief: "Compile a session brief for a task.",
  list_milestones: "List project milestones.",
  list_tasks: "List project tasks.",
  get_task: "Get a task by id.",
  create_task: "Create a task in backlog.",
  update_task: "Update a task with an expected version.",
  list_comments: "List comments on a task.",
  add_comment: "Add a comment to a task.",
  set_status: "Set a task status. Agents cannot mark done or canceled here.",
  link_dependency: "Link two tasks as blocks or relates.",
  list_decisions: "List project decisions.",
  record_decision: "Record a proposed decision.",
  list_labels: "List project area labels.",
  propose_label: "Propose a scoped area label. Humans activate it.",
  set_task_labels: "Replace the labels attached to a task.",
  get_constraints: "List project constraints.",
  create_constraint: "Propose a constraint.",
  apply_constraint: "Apply a proposed constraint.",
  get_tree: "Get a repository tree summary.",
  search_code: "Search code for symbols, paths, or content.",
  get_file: "Read a source file excerpt.",
  get_symbol: "Look up a symbol.",
  get_owners: "Get owners for a path.",
  get_related_files: "Get files related by imports.",
  get_changed_scope: "Guess files in scope for a task.",
  start_work: "Start work on a task and return a session brief.",
  finish_work:
    "Finish a work session and write a handoff. Set how_to_check so a human can verify the change in the app.",
  write_handoff: "Write a handoff without changing task status.",
  get_handoff: "Get the latest handoff for a task.",
  github_list_prs: "List GitHub pull requests for a repo.",
  github_list_issues: "List GitHub issues for a repo.",
  github_link_issue: "Link a GitHub issue to a task.",
  github_sync_now: "Trigger a GitHub sync.",
  list_reports: "List generated development reports.",
  generate_report: "Generate a development report from the current board and reviews.",
  get_report: "Get a development report by id.",
  list_reviews: "List imported reviews agents can turn into tasks.",
  import_review: "Import a review document for agents to read.",
  get_review: "Get an imported review by id.",
};

export type ToolDefinition = {
  name: ToolName;
  description: string;
  inputSchema: JsonSchema;
};

export function listToolDefinitions(): ToolDefinition[] {
  return TOOL_NAMES.map((name) => ({
    name,
    description: TOOL_DESCRIPTIONS[name],
    inputSchema: toolInputSchema(name),
  }));
}

export function getToolDefinition(name: ToolName): ToolDefinition {
  return {
    name,
    description: TOOL_DESCRIPTIONS[name],
    inputSchema: toolInputSchema(name),
  };
}

export { PAGINATION_DEFAULT_LIMIT, PAGINATION_MAX_LIMIT };
