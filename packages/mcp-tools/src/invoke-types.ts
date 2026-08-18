export type GetTreeArgs = {
  repo_id?: string;
  path?: string;
  depth?: number;
};

export type SearchCodeArgs = {
  q: string;
  repo_id?: string;
  mode?: "symbol" | "content" | "path" | "auto";
  lang?: string;
  path_prefix?: string;
  limit?: number;
};

export type GetFileArgs = {
  path: string;
  repo_id?: string;
  start_line?: number;
  end_line?: number;
};

export type GetSymbolArgs = {
  name: string;
  repo_id?: string;
  path?: string;
  kind?: string;
};

export type GetOwnersArgs = {
  path: string;
  repo_id?: string;
};

export type GetRelatedFilesArgs = {
  path: string;
  repo_id?: string;
  limit?: number;
};

export type GetChangedScopeArgs = {
  task_id: string;
  limit?: number;
};
