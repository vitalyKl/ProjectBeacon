export const packageName = "@beacon/mcp-tools";

export { createHttpCodeSource, type CodeSource } from "./code-source.js";
export {
  ToolError,
  codeIndexUnavailable,
  handoffWriteUnavailable,
  integrationUnavailable,
  isToolError,
  parseErrorBody,
  repoAmbiguous,
  toolError,
} from "./errors.js";
export { invoke } from "./invoke.js";
export {
  CODE_TOOLS,
  GITHUB_TOOLS,
  IDEMPOTENT_TOOLS,
  TOOL_ARG_SCHEMAS,
  TOOL_NAMES,
  getToolDefinition,
  isCodeTool,
  isGithubTool,
  isIdempotentTool,
  isToolName,
  listToolDefinitions,
  parseToolArgs,
  type CodeTool,
  type GithubTool,
  type IdempotentTool,
  type ToolArgs,
  type ToolDefinition,
  type ToolName,
} from "./tools.js";
export type { InvokeContext } from "./types.js";
