import { ERROR_CODES, SCOPES, TOKENIZER_ID } from "@beacon/shared";
import { z } from "zod";

export const ErrorCodeSchema = z.enum(ERROR_CODES);
export const ScopeSchema = z.enum(SCOPES);

export const ActorTypeSchema = z.enum(["user", "agent", "system"]);

export const TaskStatusSchema = z.enum([
  "backlog",
  "ready",
  "in_progress",
  "blocked",
  "in_review",
  "done",
  "canceled",
]);

export const TaskTypeSchema = z.enum(["epic", "story", "task", "bug"]);

export const ContextSectionIdSchema = z.enum([
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
  "definition_of_done",
  "custom",
]);

export const ConstraintKindSchema = z.enum(["must", "must_not", "security", "compliance"]);

export const MilestoneStatusSchema = z.enum(["open", "closed"]);

export const TokenizerIdSchema = z.literal(TOKENIZER_ID);

export const ScopeTypeSchema = z.enum(["project", "repo", "path", "task"]);

export type ErrorCode = z.infer<typeof ErrorCodeSchema>;
export type Scope = z.infer<typeof ScopeSchema>;
export type ActorType = z.infer<typeof ActorTypeSchema>;
export type TaskStatus = z.infer<typeof TaskStatusSchema>;
export type TaskType = z.infer<typeof TaskTypeSchema>;
export type ContextSectionId = z.infer<typeof ContextSectionIdSchema>;
export type ConstraintKind = z.infer<typeof ConstraintKindSchema>;
export type MilestoneStatus = z.infer<typeof MilestoneStatusSchema>;
export type TokenizerId = z.infer<typeof TokenizerIdSchema>;
export type ScopeType = z.infer<typeof ScopeTypeSchema>;
