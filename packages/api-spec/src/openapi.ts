import { z } from "zod";

import { ErrorResponseSchema, PaginationQuerySchema, paginatedResponseSchema } from "./common.js";
import { CompileInputSchema, SessionBriefSchema } from "./session-brief.js";

type JsonSchema = Record<string, unknown>;

function asComponentSchema(schema: z.ZodType): JsonSchema {
  const json = { ...z.toJSONSchema(schema, { target: "draft-07" }) } as JsonSchema;
  delete json["~standard"];
  return json;
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
  const PaginationQuery = asComponentSchema(PaginationQuerySchema);
  const SessionBriefPage = asComponentSchema(paginatedResponseSchema(SessionBriefSchema));

  return {
    openapi: "3.1.0",
    info: {
      title: "ProjectBeacon API",
      version: "0.0.0",
      description: "Generated fragment: SessionBrief, errors, and pagination.",
    },
    servers: [{ url: "/" }],
    paths: {
      "/v1/projects/{id}/context/compile": {
        post: {
          summary: "Compile a session brief",
          parameters: [
            {
              name: "id",
              in: "path",
              required: true,
              schema: { type: "string", format: "uuid" },
            },
          ],
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
    },
    components: {
      schemas: {
        SessionBrief,
        ErrorResponse,
        CompileInput,
        PaginationQuery,
        SessionBriefPage,
      },
    },
  };
}
