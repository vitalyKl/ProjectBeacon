import {
  PAGINATION_DEFAULT_LIMIT,
  PAGINATION_MAX_LIMIT,
} from "@beacon/shared";
import { z } from "zod";

import {
  ActorTypeSchema,
  DecisionLifecycleStatusSchema,
  DecisionStatusSchema,
  ErrorCodeSchema,
  ScopeSchema,
} from "./enums.js";

export const AnyUuidSchema = z.uuid();
export const UuidSchema = z.uuidv7();

export const ActorRefSchema = z.object({
  type: ActorTypeSchema,
  id: z.string().min(1),
  display: z.string().min(1),
});

export const ErrorBodySchema = z.object({
  code: ErrorCodeSchema,
  message: z.string(),
  details: z.record(z.string(), z.unknown()).default({}),
});

export const ErrorResponseSchema = z.object({
  error: ErrorBodySchema,
});

export const PaginationQuerySchema = z.object({
  cursor: z.string().min(1).optional(),
  limit: z.coerce.number().int().min(1).max(PAGINATION_MAX_LIMIT).default(PAGINATION_DEFAULT_LIMIT),
});

export function paginatedResponseSchema<T extends z.ZodType>(itemSchema: T) {
  return z.object({
    items: z.array(itemSchema),
    next_cursor: z.string().nullable(),
  });
}

export const CommentSchema = z.object({
  id: UuidSchema,
  task_id: UuidSchema,
  author_type: ActorTypeSchema,
  author_id: z.string().min(1),
  body: z.string(),
  created_at: z.iso.datetime({ offset: true }),
});

export const LinkedPathSchema = z.object({
  repo_id: UuidSchema,
  path: z.string(),
});

export const DecisionSchema = z.object({
  id: UuidSchema,
  project_id: UuidSchema,
  title: z.string(),
  status: DecisionStatusSchema,
  context: z.string(),
  decision: z.string(),
  consequences: z.string(),
  created_by_type: z.string(),
  created_by_id: z.string(),
  superseded_by: UuidSchema.nullable(),
  created_at: z.iso.datetime({ offset: true }),
  related_paths: z.array(LinkedPathSchema),
  related_task_ids: z.array(UuidSchema),
});

export const PatchDecisionSchema = z
  .object({
    status: DecisionLifecycleStatusSchema,
    superseded_by: UuidSchema.nullable().optional(),
  })
  .superRefine((value, ctx) => {
    if (value.status === "superseded" && value.superseded_by == null) {
      ctx.addIssue({
        code: "custom",
        path: ["superseded_by"],
        message: "superseded_by is required",
      });
    }
  });

export const ScopeListSchema = z.array(ScopeSchema);

export type ActorRef = z.infer<typeof ActorRefSchema>;
export type ErrorBody = z.infer<typeof ErrorBodySchema>;
export type ErrorResponse = z.infer<typeof ErrorResponseSchema>;
export type PaginationQuery = z.infer<typeof PaginationQuerySchema>;
export type LinkedPath = z.infer<typeof LinkedPathSchema>;
export type Decision = z.infer<typeof DecisionSchema>;
export type PatchDecision = z.infer<typeof PatchDecisionSchema>;
export type Comment = z.infer<typeof CommentSchema>;
