import { TOKENIZER_ID } from "@beacon/shared";
import { z } from "zod";

import { LinkedPathSchema, UuidSchema } from "./common.js";
import {
  ConstraintKindSchema,
  MilestoneStatusSchema,
  ScopeTypeSchema,
  TaskStatusSchema,
  TaskTypeSchema,
  TokenizerIdSchema,
} from "./enums.js";

const ContextSectionFields = {
  title: z.string(),
  body_md: z.string(),
  ordinal: z.number().int(),
} as const;

export const ContextSectionSchema = z.discriminatedUnion("id", [
  z.object({
    id: z.literal("custom"),
    key: z.string().min(1),
    ...ContextSectionFields,
  }),
  z.object({
    id: z.enum([
      "goals",
      "non_goals",
      "architecture",
      "conventions",
      "glossary",
      "ownership",
      "pitfalls",
      "commands",
      "stack",
      "security",
      "style",
    ]),
    key: z.string().min(1).optional(),
    ...ContextSectionFields,
  }),
]);

export const TaskSummarySchema = z.object({
  id: UuidSchema,
  title: z.string(),
  status: TaskStatusSchema,
  type: TaskTypeSchema,
  milestone_id: UuidSchema.nullable(),
  acceptance_md: z.string(),
  linked_paths: z.array(LinkedPathSchema),
});

export const ConstraintViewSchema = z.object({
  id: UuidSchema,
  kind: ConstraintKindSchema,
  body: z.string(),
  scope_path: z.string(),
  status: z.literal("active"),
});

export const DecisionSummarySchema = z.object({
  id: UuidSchema,
  title: z.string(),
  status: z.literal("accepted"),
  decision: z.string(),
  related_paths: z.array(z.string()),
});

export const BriefHandoffSchema = z.object({
  id: UuidSchema,
  session_id: UuidSchema,
  summary: z.string(),
  next_steps: z.string(),
  files_touched: z.array(LinkedPathSchema),
  open_questions: z.array(z.string()),
  created_at: z.iso.datetime({ offset: true }),
});

export const ChangedScopeReasonSchema = z.object({
  path: z.string(),
  repo_id: UuidSchema,
  reason: z.string(),
});

export const ChangedScopeSchema = z.object({
  paths: z.array(LinkedPathSchema),
  reasons: z.array(ChangedScopeReasonSchema),
});

export const TreeCapsuleEntrySchema = z.object({
  path: z.string(),
  kind: z.enum(["dir", "file"]),
  file_count: z.number().int().nonnegative().optional(),
  langs: z.record(z.string(), z.number()).optional(),
  important: z.boolean().optional(),
});

export const TreeCapsuleSchema = z.object({
  repo_id: UuidSchema,
  root: z.string(),
  entries: z.array(TreeCapsuleEntrySchema),
});

export const BriefProjectSchema = z.object({
  id: UuidSchema,
  name: z.string(),
  slug: z.string(),
});

export const BriefTargetSchema = z.object({
  repo_id: UuidSchema.nullable(),
  path: z.string(),
  task_id: UuidSchema.nullable(),
});

export const BriefMilestoneSchema = z.object({
  id: UuidSchema,
  title: z.string(),
  status: MilestoneStatusSchema,
});

export const BriefBudgetSchema = z.object({
  requested: z.number().int().nonnegative(),
  used_estimate: z.number().int().nonnegative(),
  tokenizer: TokenizerIdSchema,
  overflow: z.boolean(),
  dropped: z.array(z.string()),
});

export const BriefSourceSchema = z.object({
  node_id: UuidSchema,
  scope_type: ScopeTypeSchema.or(z.string()),
  path: z.string(),
});

export const SessionBriefSchema = z.object({
  schema_version: z.literal("1"),
  compiler_version: z.string().min(1),
  project: BriefProjectSchema,
  compiled_at: z.iso.datetime({ offset: true }),
  revision_id: UuidSchema,
  compiled_hash: z.string().min(1),
  target: BriefTargetSchema,
  milestone: BriefMilestoneSchema.nullable(),
  task: TaskSummarySchema.nullable(),
  sections: z.array(ContextSectionSchema),
  constraints: z.array(ConstraintViewSchema),
  decisions_relevant: z.array(DecisionSummarySchema),
  handoff: BriefHandoffSchema.nullable(),
  changed_scope: ChangedScopeSchema.nullable(),
  tree_capsule: TreeCapsuleSchema.nullable(),
  budget: BriefBudgetSchema,
  sources: z.array(BriefSourceSchema),
});

export const CompileIncludeSchema = z.object({
  handoff: z.boolean().optional(),
  changed_scope: z.boolean().optional(),
  tree_capsule: z.boolean().optional(),
});

export const CompileExtrasSchema = z.object({
  changed_scope: ChangedScopeSchema.nullable().optional(),
  tree_capsule: TreeCapsuleSchema.nullable().optional(),
  handoff: BriefHandoffSchema.nullable().optional(),
});

export const CompileInputSchema = z.object({
  project_id: UuidSchema,
  repo_id: UuidSchema.optional(),
  path: z.string().optional(),
  task_id: UuidSchema.optional(),
  budget_tokens: z.number().int().positive().optional(),
  include: CompileIncludeSchema.optional(),
  extras: CompileExtrasSchema.optional(),
});

export const DEFAULT_TOKENIZER = TOKENIZER_ID;

export type ContextSection = z.infer<typeof ContextSectionSchema>;
export type TaskSummary = z.infer<typeof TaskSummarySchema>;
export type ConstraintView = z.infer<typeof ConstraintViewSchema>;
export type DecisionSummary = z.infer<typeof DecisionSummarySchema>;
export type BriefHandoff = z.infer<typeof BriefHandoffSchema>;
export type ChangedScope = z.infer<typeof ChangedScopeSchema>;
export type TreeCapsule = z.infer<typeof TreeCapsuleSchema>;
export type SessionBrief = z.infer<typeof SessionBriefSchema>;
export type CompileInput = z.infer<typeof CompileInputSchema>;
