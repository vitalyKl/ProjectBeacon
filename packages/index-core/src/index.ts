export const packageName = "@beacon/index-core";

export { IndexCore } from "./index-core.js";
export { NativeTreeSitterParser, createParser } from "./parser.js";
export {
  containsBeaconToken,
  containsNul,
  findBeaconTokenHits,
  isDeniedDirName,
  isDeniedFile,
  pathHasDeniedSegment,
} from "./denylist.js";
export { PathEscapeError, toPosix, toRepoPosixPath } from "./paths.js";
export { SCHEMA_SQL, SCHEMA_VERSION } from "./schema.js";

export type {
  ChangedScopeResult,
  ContentHit,
  DirCapsule,
  ExtractedImport,
  ExtractedSymbol,
  FileRecord,
  GetChangedScopeOptions,
  GetRelatedFilesOptions,
  GetSymbolOptions,
  GetTreeOptions,
  ImportEdge,
  IndexCoreOptions,
  IndexStats,
  IndexWarning,
  LanguageId,
  ParseResult,
  SearchContentOptions,
  SearchSymbolsOptions,
  SymbolRecord,
  TreeSitterParser,
} from "./types.js";
