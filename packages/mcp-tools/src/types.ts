import type { CodeSource } from "./code-source.js";

export type InvokeContext = {
  baseUrl: string;
  token: string;
  projectId?: string;
  defaultRepoId?: string;
  projectTokens?: Record<string, string>;
  projectUrls?: Record<string, string>;
  fetch?: typeof fetch;
  codeSource?: CodeSource;
};

export type JsonObject = Record<string, unknown>;
