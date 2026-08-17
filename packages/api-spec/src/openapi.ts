import { z } from "zod";

import { ErrorResponseSchema, PaginationQuerySchema, paginatedResponseSchema } from "./common.js";
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
              schema: {
                type: "string",
                format: "uuid",
                pattern:
                  "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-7[0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$",
              },
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
