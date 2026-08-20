"use client";

import { useCallback, useEffect, useMemo, useState } from "react";

import { ApiError, fetchProjectMembers, type PublicRepo } from "@/lib/api";
import {
  excerptCanContinue,
  fetchRepoFile,
  fetchRepoTree,
  FILE_EXCERPT_MAX_LINES,
  filesEmptyKind,
  isCodeIndexUnavailable,
  isUnsupportedMedia,
  mergeTreeDirs,
  posixBasename,
  sortTreeChildren,
  treeDirPaths,
  treeEntryKind,
  type RepoFileExcerpt,
  type RepoTreeDir,
} from "@/lib/files";
import {
  fetchDetailedProjectRepos,
  pickHomeIndexRepo,
  repoDisplayName,
} from "@/lib/index-status";
import { FIELD_INPUT_CLASS } from "@/lib/ui";
import { Banner } from "@/lib/ui/banner";
import { Button } from "@/lib/ui/button";
import { EmptyState } from "@/lib/ui/empty-state";
import { PageHeader } from "@/lib/ui/page-header";
import { Panel } from "@/lib/ui/panel";
import { useT, useTf } from "@/lib/use-locale";

import { AttachLocalRepoForm } from "../attach-local-repo-form";
import { useAppSelection, useSelectedProject } from "../project-context";

export function FilesView() {
  const t = useT();
  const tf = useTf();
  const { project } = useSelectedProject();
  const selection = useAppSelection();
  const [repos, setRepos] = useState<PublicRepo[]>([]);
  const [repoId, setRepoId] = useState<string | null>(null);
  const [dirs, setDirs] = useState<RepoTreeDir[]>([]);
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set(["."]));
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [excerpt, setExcerpt] = useState<RepoFileExcerpt | null>(null);
  const [excerptNote, setExcerptNote] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [indexUnavailable, setIndexUnavailable] = useState(false);
  const [loading, setLoading] = useState(Boolean(project));
  const [fileLoading, setFileLoading] = useState(false);
  const [canAttach, setCanAttach] = useState(false);
  const projectId = project?.id ?? null;
  const meId = selection?.me.id ?? null;
  const defaultRepoId = project?.default_repo_id ?? null;

  const dirPaths = useMemo(() => treeDirPaths(dirs), [dirs]);
  const dirsByPath = useMemo(() => new Map(dirs.map((dir) => [dir.path, dir])), [dirs]);
  const selectedRepo = repos.find((repo) => repo.id === repoId) ?? null;

  const loadTree = useCallback(
    async (nextRepoId: string, path?: string, replace = false) => {
      const incoming = await fetchRepoTree(nextRepoId, { path });
      setDirs((current) => (replace ? incoming : mergeTreeDirs(current, incoming)));
      setIndexUnavailable(false);
      setError(null);
      return incoming;
    },
    [],
  );

  const reload = useCallback(async () => {
    if (!projectId) {
      return;
    }
    setLoading(true);
    try {
      const listed = await fetchDetailedProjectRepos(projectId);
      const nextRepos = listed ?? [];
      setRepos(nextRepos);
      if (meId) {
        try {
          const members = await fetchProjectMembers(projectId);
          setCanAttach(members.some((member) => member.user_id === meId && member.role === "admin"));
        } catch {
          setCanAttach(false);
        }
      } else {
        setCanAttach(false);
      }
      const picked = pickHomeIndexRepo(nextRepos, defaultRepoId);
      const nextRepoId = picked?.id ?? null;
      setRepoId(nextRepoId);
      setSelectedPath(null);
      setExcerpt(null);
      setExcerptNote(null);
      setExpanded(new Set(["."]));
      if (!nextRepoId) {
        setDirs([]);
        setIndexUnavailable(false);
        setError(null);
        return;
      }
      await loadTree(nextRepoId, undefined, true);
    } catch (caught) {
      setDirs([]);
      if (isCodeIndexUnavailable(caught)) {
        setIndexUnavailable(true);
        setError(null);
      } else {
        setIndexUnavailable(false);
        setError(caught instanceof ApiError ? caught.message : t("files.loadFailed"));
      }
    } finally {
      setLoading(false);
    }
  }, [defaultRepoId, loadTree, meId, projectId, t]);

  useEffect(() => {
    if (!projectId) {
      const id = window.setTimeout(() => {
        setRepos([]);
        setRepoId(null);
        setDirs([]);
        setSelectedPath(null);
        setExcerpt(null);
        setExcerptNote(null);
        setError(null);
        setIndexUnavailable(false);
        setCanAttach(false);
        setLoading(false);
      }, 0);
      return () => window.clearTimeout(id);
    }
    const id = window.setTimeout(() => {
      void reload();
    }, 0);
    return () => window.clearTimeout(id);
  }, [projectId, reload]);

  async function onSelectRepo(nextRepoId: string) {
    setRepoId(nextRepoId);
    setSelectedPath(null);
    setExcerpt(null);
    setExcerptNote(null);
    setExpanded(new Set(["."]));
    setLoading(true);
    try {
      await loadTree(nextRepoId, undefined, true);
    } catch (caught) {
      setDirs([]);
      if (isCodeIndexUnavailable(caught)) {
        setIndexUnavailable(true);
        setError(null);
      } else {
        setIndexUnavailable(false);
        setError(caught instanceof ApiError ? caught.message : t("files.loadFailed"));
      }
    } finally {
      setLoading(false);
    }
  }

  async function onToggleDir(path: string) {
    const next = new Set(expanded);
    if (next.has(path)) {
      next.delete(path);
      setExpanded(next);
      return;
    }
    next.add(path);
    setExpanded(next);
    const existing = dirsByPath.get(path);
    if (existing && existing.children.length > 0) {
      return;
    }
    if (!repoId) {
      return;
    }
    try {
      await loadTree(repoId, path === "." ? undefined : path);
    } catch (caught) {
      if (isCodeIndexUnavailable(caught)) {
        setIndexUnavailable(true);
        setError(null);
      } else {
        setError(caught instanceof ApiError ? caught.message : t("files.loadFailed"));
      }
    }
  }

  async function onOpenFile(path: string, startLine = 1) {
    if (!repoId) {
      return;
    }
    setSelectedPath(path);
    setFileLoading(true);
    if (startLine === 1) {
      setExcerpt(null);
      setExcerptNote(null);
    }
    try {
      const next = await fetchRepoFile(repoId, {
        path,
        start_line: startLine,
        end_line: startLine + FILE_EXCERPT_MAX_LINES - 1,
      });
      setExcerpt((current) =>
        startLine === 1 || !current || current.path !== path
          ? next
          : {
              ...next,
              start_line: current.start_line,
              content: `${current.content}\n${next.content}`,
              bytes: current.bytes + next.bytes,
            },
      );
      setExcerptNote(null);
      setError(null);
    } catch (caught) {
      if (isUnsupportedMedia(caught)) {
        setExcerpt(null);
        setExcerptNote(t("files.binary"));
      } else if (caught instanceof ApiError && (caught.status === 404 || caught.code === "not_found")) {
        setExcerpt(null);
        setExcerptNote(t("files.notFound"));
      } else if (isCodeIndexUnavailable(caught)) {
        setIndexUnavailable(true);
        setExcerpt(null);
        setExcerptNote(null);
      } else {
        setExcerptNote(caught instanceof ApiError ? caught.message : t("files.loadFailed"));
      }
    } finally {
      setFileLoading(false);
    }
  }

  if (!project) {
    return (
      <section className="space-y-2">
        <PageHeader title={t("nav.files")} description={t("common.selectProject")} />
      </section>
    );
  }

  const root = dirsByPath.get(".") ?? dirs[0] ?? null;
  const emptyKind = filesEmptyKind({ loading, repoCount: repos.length, canAttach });

  return (
    <section className="space-y-4">
      <PageHeader
        title={t("nav.files")}
        description={t("files.intro")}
        actions={
          repos.length > 1 ? (
            <label className="flex items-center gap-2 text-sm">
              {t("files.repo")}
              <select
                className={FIELD_INPUT_CLASS}
                value={repoId ?? ""}
                onChange={(event) => void onSelectRepo(event.target.value)}
              >
                {repos.map((repo) => (
                  <option key={repo.id} value={repo.id}>
                    {repoDisplayName(repo)}
                  </option>
                ))}
              </select>
            </label>
          ) : null
        }
      />

      {error ? <Banner tone="danger">{error}</Banner> : null}
      {loading ? <p className="text-sm text-muted">{t("common.loading")}</p> : null}

      {emptyKind !== "none" ? (
        <EmptyState title={t("files.emptyRepos")} description={t("files.emptyReposHint")}>
          <AttachLocalRepoForm
            projectId={project.id}
            canSubmit={canAttach}
            onAttached={() => {
              void reload();
            }}
          />
        </EmptyState>
      ) : null}

      {!loading && repos.length > 0 && indexUnavailable ? (
        <Panel className="space-y-1">
          <p className="text-sm font-medium">{t("files.unavailable")}</p>
          <p className="text-sm text-muted">{t("files.unavailableHint")}</p>
        </Panel>
      ) : null}

      {!loading && repos.length > 0 && !indexUnavailable ? (
        <div className="grid gap-4 lg:grid-cols-[minmax(16rem,20rem)_1fr]">
          <aside className="min-h-64 overflow-auto rounded-lg border border-border bg-surface p-3">
            {root ? (
              <TreeList
                path={root.path}
                dirsByPath={dirsByPath}
                dirPaths={dirPaths}
                expanded={expanded}
                selectedPath={selectedPath}
                onToggleDir={onToggleDir}
                onOpenFile={(path) => void onOpenFile(path)}
              />
            ) : (
              <p className="text-sm text-muted">{t("files.emptyTree")}</p>
            )}
          </aside>
          <Panel className="min-h-64 space-y-3">
            {selectedPath ? (
              <>
                <header className="space-y-1">
                  <h2 className="font-mono text-sm font-semibold break-all">{selectedPath}</h2>
                  {excerpt ? (
                    <p className="text-xs text-muted">
                      {tf("files.excerptRange", {
                        start: String(excerpt.start_line),
                        end: String(excerpt.end_line),
                      })}
                      {selectedRepo ? ` · ${repoDisplayName(selectedRepo)}` : ""}
                    </p>
                  ) : null}
                </header>
                {fileLoading && !excerpt ? (
                  <p className="text-sm text-muted">{t("files.loadingFile")}</p>
                ) : null}
                {excerptNote ? <p className="text-sm text-muted">{excerptNote}</p> : null}
                {excerpt ? (
                  <>
                    <pre className="overflow-auto rounded-md border border-border bg-background p-3 text-xs leading-5">
                      <code>{excerpt.content}</code>
                    </pre>
                    {excerptCanContinue(excerpt) ? (
                      <Button
                        variant="secondary"
                        type="button"
                        onClick={() => void onOpenFile(selectedPath, excerpt.end_line + 1)}
                        disabled={fileLoading}
                      >
                        {t("files.more")}
                      </Button>
                    ) : null}
                  </>
                ) : null}
              </>
            ) : (
              <p className="text-sm text-muted">{t("files.selectFile")}</p>
            )}
          </Panel>
        </div>
      ) : null}
    </section>
  );
}

function TreeList({
  path,
  dirsByPath,
  dirPaths,
  expanded,
  selectedPath,
  onToggleDir,
  onOpenFile,
  depth = 0,
}: {
  path: string;
  dirsByPath: Map<string, RepoTreeDir>;
  dirPaths: Set<string>;
  expanded: Set<string>;
  selectedPath: string | null;
  onToggleDir: (path: string) => void;
  onOpenFile: (path: string) => void;
  depth?: number;
}) {
  const dir = dirsByPath.get(path);
  const open = expanded.has(path);
  const children = dir ? sortTreeChildren(dir.children, dirPaths) : [];
  const label = posixBasename(path);

  return (
    <div>
      <button
        type="button"
        className="flex w-full items-center gap-1 rounded px-1 py-0.5 text-left text-sm hover:bg-background"
        style={{ paddingLeft: `${depth * 12 + 4}px` }}
        onClick={() => onToggleDir(path)}
        aria-expanded={open}
      >
        <span className="w-3 text-muted">{open ? "▾" : "▸"}</span>
        <span className="font-medium">{label}</span>
      </button>
      {open
        ? children.map((child) =>
            treeEntryKind(child, dirPaths) === "dir" ? (
              <TreeList
                key={child}
                path={child}
                dirsByPath={dirsByPath}
                dirPaths={dirPaths}
                expanded={expanded}
                selectedPath={selectedPath}
                onToggleDir={onToggleDir}
                onOpenFile={onOpenFile}
                depth={depth + 1}
              />
            ) : (
              <button
                key={child}
                type="button"
                className={`flex w-full rounded px-1 py-0.5 text-left font-mono text-sm hover:bg-background ${
                  selectedPath === child ? "bg-background" : ""
                }`}
                style={{ paddingLeft: `${(depth + 1) * 12 + 20}px` }}
                onClick={() => onOpenFile(child)}
              >
                {posixBasename(child)}
              </button>
            ),
          )
        : null}
    </div>
  );
}
