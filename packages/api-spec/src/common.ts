import {
  PAGINATION_DEFAULT_LIMIT,
  PAGINATION_MAX_LIMIT,
} from "@beacon/shared";
import { z } from "zod";

import { ActorTypeSchema, ErrorCodeSchema, ScopeSchema } from "./enums.js";

export const UuidSchema = z.uuid();

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

export const LinkedPathSchema = z.object({
  repo_id: UuidSchema,
  path: z.string(),
});

export const ScopeListSchema = z.array(ScopeSchema);

export type ActorRef = z.infer<typeof ActorRefSchema>;
export type ErrorBody = z.infer<typeof ErrorBodySchema>;
export type ErrorResponse = z.infer<typeof ErrorResponseSchema>;
export type PaginationQuery = z.infer<typeof PaginationQuerySchema>;
export type LinkedPath = z.infer<typeof LinkedPathSchema>;
