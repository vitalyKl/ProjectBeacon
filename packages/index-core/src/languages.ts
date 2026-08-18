import { basename } from "./paths.js";
import type { LanguageId } from "./types.js";

const EXTENSION_LANG: Record<string, LanguageId> = {
  ".ts": "typescript",
  ".tsx": "tsx",
  ".mts": "typescript",
  ".cts": "typescript",
  ".js": "javascript",
  ".jsx": "javascript",
  ".mjs": "javascript",
  ".cjs": "javascript",
  ".py": "python",
  ".go": "go",
  ".rs": "rust",
  ".java": "java",
  ".cs": "csharp",
  ".json": "json",
  ".yml": "yaml",
  ".yaml": "yaml",
  ".md": "markdown",
  ".mdx": "markdown",
};

export const SYMBOL_LANGUAGES = new Set<LanguageId>(["typescript", "tsx", "javascript", "python"]);

export const IMPORTANT_FILE_NAMES = new Set([
  "readme",
  "readme.md",
  "agents.md",
  "conventions.md",
  "package.json",
  "go.mod",
  "pyproject.toml",
  "cargo.toml",
  "dockerfile",
  "makefile",
  "tsconfig.json",
  "pnpm-workspace.yaml",
]);

export function detectLanguage(repoPosixPath: string): LanguageId {
  const name = basename(repoPosixPath).toLowerCase();
  const dot = name.lastIndexOf(".");
  if (dot < 0) {
    return "unknown";
  }
  return EXTENSION_LANG[name.slice(dot)] ?? "unknown";
}

export function isImportantFile(repoPosixPath: string): boolean {
  return IMPORTANT_FILE_NAMES.has(basename(repoPosixPath).toLowerCase());
}

export function hasSymbolGrammar(lang: LanguageId): boolean {
  return SYMBOL_LANGUAGES.has(lang);
}
