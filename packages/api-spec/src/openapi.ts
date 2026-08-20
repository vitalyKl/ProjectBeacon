import { PAGINATION_DEFAULT_LIMIT, PAGINATION_MAX_LIMIT } from "@beacon/shared";
import { z } from "zod";

import {
  CommentSchema,
  DecisionSchema,
  ErrorResponseSchema,
  PaginationQuerySchema,
  PatchDecisionSchema,
  paginatedResponseSchema,
} from "./common.js";
import { CompileInputSchema, SessionBriefSchema } from "./session-brief.js";

type JsonSchema = Record<string, unknown>;

function asComponentSchema(schema: z.ZodType): JsonSchema {
  const json = { ...z.toJSONSchema(schema, { target: "draft-07" }) } as JsonSchema;
  delete json["~standard"];
  return json;
}

function markDefaultedFieldsOptional(schema: JsonSchema): JsonSchema {
  const properties = schema["properties"];
  const required = schema["required"];
  if (
    properties === null ||
    typeof properties !== "object" ||
    Array.isArray(properties) ||
    !Array.isArray(required)
  ) {
    return schema;
  }

  const optional = new Set(
    Object.entries(properties as Record<string, unknown>)
      .filter(([, value]) => {
        return value !== null && typeof value === "object" && !Array.isArray(value) && "default" in value;
      })
      .map(([name]) => name),
  );

  schema["required"] = required.filter((name) => typeof name === "string" && !optional.has(name));
  if ((schema["required"] as unknown[]).length === 0) {
    delete schema["required"];
  }
  return schema;
}

export function toOpenApi(): {
  openapi: "3.1.0";
  info: { title: string; version: string; description: string };
  servers: { url: string }[];
  paths: Record<string, unknown>;
  components: { schemas: Record<string, JsonSchema> };
} {
  const SessionBrief = asComponentSchema(SessionBriefSchema);
  const ErrorResponse = asComponentSchema(ErrorResponseSchema);
  const CompileInput = asComponentSchema(CompileInputSchema);
  const PaginationQuery = markDefaultedFieldsOptional(asComponentSchema(PaginationQuerySchema));
  const SessionBriefPage = asComponentSchema(paginatedResponseSchema(SessionBriefSchema));
  const Comment = asComponentSchema(CommentSchema);
  const CommentPage = asComponentSchema(paginatedResponseSchema(CommentSchema));
  const Decision = asComponentSchema(DecisionSchema);
  const PatchDecision = asComponentSchema(PatchDecisionSchema);
  const idParam = {
    name: "id",
    in: "path",
    required: true,
    schema: {
      type: "string",
      format: "uuid",
      pattern:
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-7[0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$",
    },
  };

  return {
    openapi: "3.1.0",
    info: {
      title: "ProjectBeacon API",
      version: "0.0.0",
      description: "Generated fragment: SessionBrief, comments, decisions, errors, and pagination.",
    },
    servers: [{ url: "/" }],
    paths: {
      "/v1/projects/{id}/context/compile": {
        post: {
          summary: "Compile a session brief",
          parameters: [idParam],
          requestBody: {
            content: {
              "application/json": {
                schema: { $ref: "#/components/schemas/CompileInput" },
              },
            },
          },
          responses: {
            "200": {
              description: "Compiled session brief",
              content: {
                "application/json": {
                  schema: { $ref: "#/components/schemas/SessionBrief" },
                },
              },
            },
            default: {
              description: "Error",
              content: {
                "application/json": {
                  schema: { $ref: "#/components/schemas/ErrorResponse" },
                },
              },
            },
          },
        },
      },
      "/v1/decisions/{id}": {
        patch: {
          summary: "Update a decision lifecycle status",
          parameters: [idParam],
          requestBody: {
            required: true,
            content: {
              "application/json": {
                schema: { $ref: "#/components/schemas/PatchDecision" },
              },
            },
          },
          responses: {
            "200": {
              description: "Updated decision",
              content: {
                "application/json": {
                  schema: { $ref: "#/components/schemas/Decision" },
                },
              },
            },
            default: {
              description: "Error",
              content: {
                "application/json": {
                  schema: { $ref: "#/components/schemas/ErrorResponse" },
                },
              },
            },
          },
        },
      },
      "/v1/tasks/{id}/comments": {
        get: {
          summary: "List comments on a task",
          parameters: [
            idParam,
            { name: "cursor", in: "query", schema: { type: "string", minLength: 1 } },
            {
              name: "limit",
              in: "query",
              schema: {
                type: "integer",
                minimum: 1,
                maximum: PAGINATION_MAX_LIMIT,
                default: PAGINATION_DEFAULT_LIMIT,
              },
            },
          ],
          responses: {
            "200": {
              description: "Task comments",
              content: {
                "application/json": {
                  schema: { $ref: "#/components/schemas/CommentPage" },
                },
              },
            },
            default: {
              description: "Error",
              content: {
                "application/json": {
                  schema: { $ref: "#/components/schemas/ErrorResponse" },
                },
              },
            },
          },
        },
      },
    },
    components: {
      schemas: {
        SessionBrief,
        ErrorResponse,
        CompileInput,
        PaginationQuery,
        SessionBriefPage,
        Comment,
        CommentPage,
        Decision,
        PatchDecision,
      },
    },
  };
}
