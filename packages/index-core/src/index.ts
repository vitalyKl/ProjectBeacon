export const packageName = "@beacon/index-core";

export { IndexCore } from "./index-core.js";
export { NativeTreeSitterParser, createParser } from "./parser.js";
export { containsBeaconToken, containsNul, findBeaconTokenHits, isDeniedDirName, isDeniedFile, pathHasDeniedSegment } from "./denylist.js";
export { PathEscapeError, toPosix, toRepoPosixPath } from "./paths.js";
export { SCHEMA_SQL, SCHEMA_VERSION } from "./schema.js";
export {
  createIndexHttpServer,
  handleIndexRequest,
  listenIndexHttp,
  DEFAULT_INDEX_HTTP_HOST,
  DEFAULT_INDEX_HTTP_PORT,
  GET_FILE_MAX_LINES,
} from "./http.js";
export { readFileExcerpt } from "./file.js";
export { extractIdentifiers } from "./identifiers.js";
export { ownersForPath, parseCodeowners, matchCodeowners } from "./owners.js";
export {
  PREBUILD_PLATFORMS,
  currentPrebuildPlatform,
  resolveRipgrepPath,
  type PrebuildPlatform,
} from "./binaries.js";

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
  SearchCodeOptions,
  SearchCodeResult,
  SearchContentOptions,
  SearchMode,
  SearchSymbolsOptions,
  SymbolRecord,
  TreeSitterParser,
} from "./types.js";
