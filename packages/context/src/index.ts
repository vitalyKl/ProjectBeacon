export const packageName = "@beacon/context";

export { sectionFromHeading, sectionIdForHeading, slugifyHeading } from "./aliases.js";
export {
  DEFAULT_SECURITY_CONSTRAINTS,
  DEFAULT_SECURITY_CONSTRAINT_KIND,
  DEFAULT_SECURITY_CONSTRAINT_STATUS,
} from "./defaults.js";
export { exportAgentsMd, type AgentsMdExportInput, type AgentsMdScope } from "./export.js";
export {
  CONTEXT_SOURCES,
  parseImportFiles,
  type ContextSource,
  type ImportFile,
  type ImportScopeType,
  type ParsedCodeOwner,
  type ParsedImportNode,
  type ParseImportResult,
} from "./import.js";

export {
  CHANGED_SCOPE_PATH_CAP,
  COMPILER_VERSION,
  DECISIONS_CAP,
  DECISION_TEXT_CAP,
  DEFAULT_BUDGET_TOKENS,
  HANDOFF_TOKEN_CAP,
  SCHEMA_VERSION,
  canonicalJson,
  compileSessionBrief,
  hashCompiledBrief,
  mergeSections,
  posixPathPrefixes,
  selectNodes,
  sessionBriefMarkdown,
  truncateHandoff,
  type CompileDocument,
  type CompileNode,
  type CompileResult,
} from "./compile.js";
