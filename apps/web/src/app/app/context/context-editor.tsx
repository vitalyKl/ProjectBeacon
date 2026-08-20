"use client";

import { useCallback, useEffect, useMemo, useRef, useState, type ChangeEvent } from "react";

import {
  ApiError,
  compileContext,
  exportAgentsMd,
  fetchContextNodes,
  fetchContextRevision,
  fetchContextRevisions,
  fetchProjectRepos,
  importContextFiles,
  putContextNode,
  type ContextNode,
  type ContextRevision,
  type ContextRevisionSummary,
  type ContextSection,
  type PublicRepo,
  type SessionBrief,
} from "@/lib/api";
import { briefDroppedItems } from "@/lib/brief-preview";
import { t, tf, type MessageKey } from "@/lib/i18n";
import {
  importedNodesNotice,
  readImportPayload,
  takeInputFiles,
  unrecognizedImportMessage,
} from "@/lib/import-files";
import { projectRepoCatalog, repoPickerOptions } from "@/lib/repo-picker";
import { FIELD_ERROR_CLASS } from "@/lib/ui";
import { useT, useTf } from "@/lib/use-locale";

import { BriefBlocks } from "../brief-blocks";
import { useSelectedProject } from "../project-context";

type KnownSectionId = Exclude<ContextSection["id"], "custom">;

const KNOWN_SECTIONS: { id: KnownSectionId; title: string }[] = [
  { id: "goals", title: "Goals" },
  { id: "non_goals", title: "Non-goals" },
  { id: "architecture", title: "Architecture" },
  { id: "stack", title: "Tech stack" },
  { id: "conventions", title: "Conventions" },
  { id: "style", title: "Style" },
  { id: "commands", title: "Commands" },
  { id: "definition_of_done", title: "Definition of Done" },
  { id: "security", title: "Security" },
  { id: "pitfalls", title: "Pitfalls" },
  { id: "glossary", title: "Glossary" },
  { id: "ownership", title: "Ownership" },
];

function randomUuidV7(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  const ts = BigInt(Date.now());
  bytes[0] = Number((ts >> 40n) & 0xffn);
  bytes[1] = Number((ts >> 32n) & 0xffn);
  bytes[2] = Number((ts >> 24n) & 0xffn);
  bytes[3] = Number((ts >> 16n) & 0xffn);
  bytes[4] = Number((ts >> 8n) & 0xffn);
  bytes[5] = Number(ts & 0xffn);
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x70;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;
  const hex = [...bytes].map((byte) => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

type DraftSection = {
  id: ContextSection["id"];
  key?: string;
  title: string;
  body_md: string;
};

type CreateScope = "project" | "repo" | "path";

type EditorTab = "edit" | "preview" | "revisions";

function knownSectionTitle(id: KnownSectionId): string {
  return t(`context.section.${id}` as MessageKey);
}

function sourceLabel(source: string): string {
  switch (source) {
    case "native":
      return t("context.source.native");
    case "imported_agents_md":
      return "AGENTS.md";
    case "imported_claude_md":
      return "CLAUDE.md";
    case "imported_cursor":
      return "Cursor";
    case "imported_grok":
      return "Grok";
    case "imported_conventions_md":
      return "CONVENTIONS.md";
    default:
      return source;
  }
}

function scopeLabel(node: Pick<ContextNode, "scope_type" | "path">): string {
  if (node.scope_type === "path") {
    return node.path || t("context.scopePathEmpty");
  }
  if (node.scope_type === "repo") {
    return node.path ? tf("context.scopeRepoWithPath", { path: node.path }) : t("context.scopeRepo");
  }
  return node.scope_type === "project" ? t("context.scopeProject") : node.scope_type;
}

function isLegacyDefinitionOfDone(section: ContextSection): boolean {
  if (section.id !== "custom") {
    return false;
  }
  const key = (section.key ?? "").trim().toLowerCase().replaceAll("_", "-");
  const title = section.title.trim().toLowerCase();
  return key === "definition-of-done" || title === "definition of done";
}

function emptyDraftsFromNode(node: ContextNode | null): DraftSection[] {
  const byId = new Map<string, ContextSection>();
  for (const section of node?.sections ?? []) {
    if (section.id === "definition_of_done" || isLegacyDefinitionOfDone(section)) {
      byId.set("definition_of_done", { ...section, id: "definition_of_done" });
      continue;
    }
    byId.set(
      section.id === "custom" ? `custom:${section.key ?? section.title}` : section.id,
      section,
    );
  }
  const drafts: DraftSection[] = KNOWN_SECTIONS.map((known) => {
    const existing = byId.get(known.id);
    return {
      id: known.id,
      title: existing?.title ?? known.title,
      body_md: existing?.body_md ?? "",
    };
  });
  for (const section of node?.sections ?? []) {
    if (section.id !== "custom" || isLegacyDefinitionOfDone(section)) {
      continue;
    }
    drafts.push({
      id: "custom",
      key: section.key,
      title: section.title,
      body_md: section.body_md,
    });
  }
  return drafts;
}

function draftsToSections(drafts: DraftSection[]): ContextSection[] {
  return drafts
    .filter((draft) => draft.body_md.trim().length > 0 || draft.id === "custom")
    .filter((draft) => draft.id !== "custom" || draft.body_md.trim().length > 0)
    .map((draft, ordinal) =>
      draft.id === "custom"
        ? {
            id: "custom" as const,
            key: (draft.key ?? draft.title).trim() || "untitled",
            title: draft.title.trim() || "Custom",
            body_md: draft.body_md,
            ordinal,
          }
        : {
            id: draft.id,
            title: draft.title,
            body_md: draft.body_md,
            ordinal,
          },
    );
}

function formatWhen(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }
  return date.toLocaleString();
}

function downloadText(filename: string, content: string, type: string): void {
  const blob = new Blob([content], { type });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(url);
}

export function ContextEditor() {
  const label = useT();
  const format = useTf();
  const { project } = useSelectedProject();
  const [nodes, setNodes] = useState<ContextNode[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [drafts, setDrafts] = useState<DraftSection[]>(emptyDraftsFromNode(null));
  const [tab, setTab] = useState<EditorTab>("edit");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [preview, setPreview] = useState<SessionBrief | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [revisions, setRevisions] = useState<ContextRevisionSummary[]>([]);
  const [openRevision, setOpenRevision] = useState<ContextRevision | null>(null);
  const [revisionsLoading, setRevisionsLoading] = useState(false);
  const [importing, setImporting] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [pasteOpen, setPasteOpen] = useState(false);
  const [pastePath, setPastePath] = useState("AGENTS.md");
  const [pasteBody, setPasteBody] = useState("");
  const [creating, setCreating] = useState(false);
  const creatingRef = useRef(false);
  const [createScope, setCreateScope] = useState<CreateScope>("project");
  const [createPath, setCreatePath] = useState("");
  const [createRepoId, setCreateRepoId] = useState("");
  const [repos, setRepos] = useState<PublicRepo[] | null>(null);
  const [reposProjectId, setReposProjectId] = useState<string | null>(null);
  const [reposFailed, setReposFailed] = useState(false);

  const selected = useMemo(
    () => (creating ? null : (nodes.find((node) => node.id === selectedId) ?? null)),
    [creating, nodes, selectedId],
  );
  const isCreate = creating || !selected;
  const repoCatalog = projectRepoCatalog({
    projectId: project?.id,
    loadedProjectId: reposProjectId,
    repos,
    failed: reposFailed,
    selectedId: createRepoId,
  });
  const repoOptions = useMemo(
    () => repoPickerOptions(repoCatalog.repos, repoCatalog.selectedId),
    [repoCatalog.repos, repoCatalog.selectedId],
  );

  const applyNodes = useCallback((items: ContextNode[], preferId?: string | null) => {
    setNodes(items);
    if (creatingRef.current && !preferId) {
      return;
    }
    if (preferId) {
      creatingRef.current = false;
      setCreating(false);
    }
    if (items.length === 0) {
      creatingRef.current = true;
      setCreating(true);
      setSelectedId(null);
      setDrafts(emptyDraftsFromNode(null));
      return;
    }
    setSelectedId((current) => {
      const nextId =
        (preferId && items.some((item) => item.id === preferId) ? preferId : null) ??
        (current && items.some((item) => item.id === current) ? current : null) ??
        items[0]?.id ??
        null;
      setDrafts(emptyDraftsFromNode(items.find((item) => item.id === nextId) ?? null));
      return nextId;
    });
  }, []);

  const reloadNodes = useCallback(
    async (preferId?: string | null) => {
      if (!project) {
        return;
      }
      const items = await fetchContextNodes(project.id);
      applyNodes(items, preferId);
    },
    [applyNodes, project],
  );

  useEffect(() => {
    if (!project) {
      return;
    }
    const projectId = project.id;
    let cancelled = false;
    async function load() {
      setError(null);
      try {
        const items = await fetchContextNodes(projectId);
        if (cancelled) {
          return;
        }
        applyNodes(items);
      } catch (caught) {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : t("context.failedLoad"));
        }
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [applyNodes, project]);

  useEffect(() => {
    if (!project) {
      return;
    }
    const projectId = project.id;
    let cancelled = false;
    async function load() {
      try {
        const listed = await fetchProjectRepos(projectId);
        if (cancelled) {
          return;
        }
        setRepos(listed ?? []);
        setReposProjectId(projectId);
        setReposFailed(false);
        setCreateRepoId("");
      } catch (caught) {
        if (!cancelled) {
          setRepos(null);
          setReposProjectId(projectId);
          setReposFailed(true);
          setCreateRepoId("");
          setError(caught instanceof ApiError ? caught.message : t("context.failedRepos"));
        }
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [project]);

  useEffect(() => {
    if (!project || tab !== "revisions") {
      return;
    }
    const projectId = project.id;
    let cancelled = false;
    async function load() {
      setRevisionsLoading(true);
      setError(null);
      try {
        const items = await fetchContextRevisions(projectId);
        if (!cancelled) {
          setRevisions(items);
        }
      } catch (caught) {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : t("context.failedRevisions"));
        }
      } finally {
        if (!cancelled) {
          setRevisionsLoading(false);
        }
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [project, tab]);

  function beginCreate() {
    creatingRef.current = true;
    setCreating(true);
    setSelectedId(null);
    setNotice(null);
    setCreateScope("project");
    setCreatePath("");
    setCreateRepoId("");
    setDrafts(emptyDraftsFromNode(null));
    setTab("edit");
  }

  function selectNode(nodeId: string) {
    creatingRef.current = false;
    setCreating(false);
    setSelectedId(nodeId);
    setNotice(null);
    const node = nodes.find((item) => item.id === nodeId) ?? null;
    setDrafts(emptyDraftsFromNode(node));
  }

  function updateDraft(index: number, body: string) {
    setDrafts((current) =>
      current.map((draft, i) => (i === index ? { ...draft, body_md: body } : draft)),
    );
  }

  async function save(reviewState?: "reviewed") {
    if (!project) {
      return;
    }
    if (isCreate) {
      if (createScope === "path" && createPath.trim().length === 0) {
        setError(t("context.pathRequired"));
        return;
      }
      if (createScope !== "project" && createRepoId.trim().length === 0) {
        setError(t("context.repoRequired"));
        return;
      }
    } else if (!selected) {
      return;
    }
    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      const nodeId = isCreate || !selected ? randomUuidV7() : selected.id;
      const body: Record<string, unknown> = {
        sections: draftsToSections(drafts),
      };
      if (reviewState) {
        body.review_state = reviewState;
      }
      if (isCreate) {
        body.scope_type = createScope;
        body.path = createScope === "path" ? createPath.trim() : "";
        if (createScope !== "project") {
          body.repo_id = createRepoId.trim();
        }
      }
      const saved = await putContextNode(project.id, nodeId, body);
      setNotice(reviewState === "reviewed" ? t("context.markedReviewed") : t("context.saved"));
      await reloadNodes(saved.id);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("context.failedSave"));
    } finally {
      setSaving(false);
    }
  }

  async function runPreview() {
    if (!project) {
      return;
    }
    setPreviewing(true);
    setError(null);
    try {
      const brief = await compileContext(project.id);
      setPreview(brief);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("context.failedPreview"));
    } finally {
      setPreviewing(false);
    }
  }

  async function openStoredRevision(revisionId: string) {
    if (!project) {
      return;
    }
    setError(null);
    try {
      setOpenRevision(await fetchContextRevision(project.id, revisionId));
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("context.failedRevisions"));
    }
  }

  async function applyImportedNodes(result: { nodes: ContextNode[] }, successNotice: string) {
    if (result.nodes.length === 0) {
      setError(unrecognizedImportMessage());
      setNotice(null);
      return;
    }
    setNotice(successNotice);
    await reloadNodes(result.nodes[0]?.id ?? selectedId);
  }

  async function onPickFiles(event: ChangeEvent<HTMLInputElement>) {
    const files = takeInputFiles(event.target);
    if (!project || files.length === 0) {
      return;
    }
    setImporting(true);
    setError(null);
    setNotice(null);
    try {
      const result = await importContextFiles(project.id, await readImportPayload(files));
      await applyImportedNodes(result, importedNodesNotice(result.nodes.length));
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("context.failedImport"));
    } finally {
      setImporting(false);
    }
  }

  async function onPasteImport() {
    if (!project) {
      return;
    }
    const path = pastePath.trim();
    if (!path || !pasteBody.trim()) {
      setError(t("context.pathRequired"));
      return;
    }
    setImporting(true);
    setError(null);
    setNotice(null);
    try {
      const result = await importContextFiles(project.id, [{ path, content: pasteBody }]);
      if (result.nodes.length === 0) {
        setError(unrecognizedImportMessage());
        return;
      }
      setPasteOpen(false);
      setPasteBody("");
      await applyImportedNodes(result, t("context.importedOne"));
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("context.failedImport"));
    } finally {
      setImporting(false);
    }
  }

  async function onExport() {
    if (!project) {
      return;
    }
    setExporting(true);
    setError(null);
    try {
      const markdown = await exportAgentsMd(project.id);
      downloadText("AGENTS.md", markdown, "text/markdown;charset=utf-8");
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("context.failedExport"));
    } finally {
      setExporting(false);
    }
  }

  if (!project) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">{label("nav.context")}</h1>
        <p className="max-w-xl text-sm leading-6 text-muted">{label("context.selectProject")}</p>
      </section>
    );
  }

  return (
    <section className="flex min-h-0 flex-col gap-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1">
          <h1 className="text-2xl font-semibold tracking-tight">{label("nav.context")}</h1>
          <p className="max-w-2xl text-sm leading-6 text-muted">{label("context.intro")}</p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <button
            className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
            type="button"
            disabled={previewing}
            onClick={() => {
              setTab("preview");
              void runPreview();
            }}
          >
            {previewing ? label("common.compiling") : label("common.compileBrief")}
          </button>
          <label className="cursor-pointer rounded-md border border-border bg-surface px-3 py-1.5 text-sm">
            {importing ? label("context.importing") : label("context.importFiles")}
            <input
              className="hidden"
              type="file"
              multiple
              disabled={importing}
              onChange={(event) => void onPickFiles(event)}
            />
          </label>
          <button
            className="rounded-md border border-border bg-surface px-3 py-1.5 text-sm"
            type="button"
            onClick={() => setPasteOpen((open) => !open)}
          >
            {label("context.pasteFile")}
          </button>
          <button
            className="rounded-md border border-border bg-surface px-3 py-1.5 text-sm disabled:opacity-60"
            type="button"
            disabled={exporting}
            onClick={() => void onExport()}
          >
            {exporting ? label("context.exporting") : label("context.export")}
          </button>
        </div>
      </div>

      {pasteOpen ? (
        <div className="space-y-2 rounded-lg border border-border bg-surface p-3">
          <label className="flex flex-col gap-1 text-sm">
            {label("common.path")}
            <input
              className="h-9 rounded-md border border-border bg-background px-2"
              value={pastePath}
              onChange={(event) => setPastePath(event.target.value)}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            {label("common.content")}
            <textarea
              className="min-h-32 rounded-md border border-border bg-background px-2 py-1.5 font-mono text-sm"
              value={pasteBody}
              onChange={(event) => setPasteBody(event.target.value)}
            />
          </label>
          <button
            className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
            type="button"
            disabled={importing}
            onClick={() => void onPasteImport()}
          >
            {label("context.importPasted")}
          </button>
        </div>
      ) : null}

      {error ? <p className={FIELD_ERROR_CLASS}>{error}</p> : null}
      {notice ? <p className="text-sm text-muted">{notice}</p> : null}

      <div className="grid min-h-[32rem] gap-4 lg:grid-cols-[18rem_minmax(0,1fr)]">
        <aside className="flex flex-col rounded-lg border border-border bg-surface">
          <div className="flex items-center justify-between border-b border-border px-3 py-2">
            <h2 className="text-sm font-medium">{label("context.nodes")}</h2>
            <button
              className="text-sm text-muted hover:text-foreground"
              type="button"
              onClick={beginCreate}
            >
              {label("context.newBrief")}
            </button>
          </div>
          {nodes.length === 0 && !isCreate ? (
            <p className="px-3 py-3 text-sm leading-6 text-muted">
              {label("context.empty")}
            </p>
          ) : (
            <ul className="flex flex-col">
              {isCreate ? (
                <li>
                  <div className="flex w-full flex-col items-start gap-1 bg-background px-3 py-2 text-left text-sm">
                    <span className="font-medium">{label("context.newBrief")}</span>
                    <span className="text-xs text-muted">{createScope}</span>
                  </div>
                </li>
              ) : null}
              {nodes.map((node) => {
                const active = !isCreate && node.id === selectedId;
                return (
                  <li key={node.id}>
                    <button
                      className={`flex w-full flex-col items-start gap-1 px-3 py-2 text-left text-sm ${
                        active ? "bg-background" : "hover:bg-background/60"
                      }`}
                      type="button"
                      onClick={() => selectNode(node.id)}
                    >
                      <span className="font-medium">{scopeLabel(node)}</span>
                      <span className="text-xs text-muted">{sourceLabel(node.source)}</span>
                      {node.review_state === "needs_review" ? (
                        <span className="rounded-full border border-border px-2 py-0.5 text-xs">
                          {label("context.importedReview")}
                        </span>
                      ) : null}
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </aside>

        <div className="min-w-0 rounded-lg border border-border bg-surface">
          <div className="flex flex-wrap items-center gap-2 border-b border-border px-3 py-2">
            {(
              [
                ["edit", "common.edit"],
                ["preview", "context.previewTab"],
                ["revisions", "context.revisionsTab"],
              ] as const
            ).map(([id, message]) => (
              <button
                key={id}
                className={`rounded-md px-3 py-1.5 text-sm ${
                  tab === id ? "bg-background font-medium" : "text-muted hover:text-foreground"
                }`}
                type="button"
                onClick={() => setTab(id)}
              >
                {label(message)}
              </button>
            ))}
          </div>

          {tab === "edit" ? (
            <div className="space-y-4 p-4">
              {!isCreate && selected ? (
                <div className="flex flex-wrap items-center gap-2 text-sm">
                  <span className="font-medium">{scopeLabel(selected)}</span>
                  <span className="text-muted">{sourceLabel(selected.source)}</span>
                  {selected.review_state === "needs_review" ? (
                    <span className="rounded-full border border-border px-2 py-0.5 text-xs">
                      {label("context.importedReview")}
                    </span>
                  ) : null}
                </div>
              ) : (
                <div className="space-y-3">
                  <p className="text-sm text-muted">{label("context.newBriefHint")}</p>
                  <div className="flex flex-wrap gap-3">
                    <label className="flex flex-col gap-1 text-sm">
                      {label("context.scope")}
                      <select
                        className="h-9 rounded-md border border-border bg-background px-2"
                        value={createScope}
                        onChange={(event) => setCreateScope(event.target.value as CreateScope)}
                      >
                        <option value="project">{label("context.scopeProject")}</option>
                        <option value="repo">{label("context.scopeRepo")}</option>
                        <option value="path">{label("context.scopePath")}</option>
                      </select>
                    </label>
                    {createScope !== "project" ? (
                      <label className="flex min-w-56 flex-1 flex-col gap-1 text-sm">
                        {label("context.repoId")}
                        {repoCatalog.state === "loading" ? (
                          <span className="text-sm text-muted">{label("common.loading")}</span>
                        ) : repoCatalog.state === "error" ? (
                          <span className={FIELD_ERROR_CLASS}>{label("context.failedRepos")}</span>
                        ) : repoCatalog.state === "empty" ? (
                          <span className="text-sm text-muted">{label("context.repoNone")}</span>
                        ) : (
                          <select
                            className="h-9 rounded-md border border-border bg-background px-2"
                            value={repoCatalog.selectedId}
                            onChange={(event) => setCreateRepoId(event.target.value)}
                          >
                            <option value="">{label("context.repoPlaceholder")}</option>
                            {repoOptions.map((option) => (
                              <option key={option.id} value={option.id}>
                                {option.label}
                              </option>
                            ))}
                          </select>
                        )}
                      </label>
                    ) : null}
                    {createScope === "path" ? (
                      <label className="flex min-w-56 flex-1 flex-col gap-1 text-sm">
                        {label("common.path")}
                        <input
                          className="h-9 rounded-md border border-border bg-background px-2 font-mono"
                          value={createPath}
                          onChange={(event) => setCreatePath(event.target.value)}
                        />
                      </label>
                    ) : null}
                  </div>
                </div>
              )}

              {drafts.map((draft, index) => (
                <label
                  key={`${draft.id}:${draft.key ?? draft.title}`}
                  className="flex flex-col gap-1"
                >
                  <span className="text-sm font-medium">
                    {draft.id !== "custom" ? knownSectionTitle(draft.id) : draft.title}
                  </span>
                  <textarea
                    className="min-h-24 rounded-md border border-border bg-background px-2 py-1.5 font-mono text-sm"
                    value={draft.body_md}
                    onChange={(event) => updateDraft(index, event.target.value)}
                  />
                </label>
              ))}

              <div className="flex flex-wrap gap-2">
                <button
                  className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
                  type="button"
                  disabled={saving}
                  onClick={() => void save()}
                >
                  {saving ? label("common.saving") : label("common.save")}
                </button>
                {selected?.review_state === "needs_review" ? (
                  <button
                    className="rounded-md border border-border px-3 py-1.5 text-sm disabled:opacity-60"
                    type="button"
                    disabled={saving}
                    onClick={() => void save("reviewed")}
                  >
                    {label("context.markReviewed")}
                  </button>
                ) : null}
              </div>
            </div>
          ) : null}

          {tab === "preview" ? (
            <div className="space-y-4 p-4">
              <div className="flex flex-wrap items-center gap-2">
                <button
                  className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
                  type="button"
                  disabled={previewing}
                  onClick={() => void runPreview()}
                >
                  {previewing ? label("common.compiling") : label("context.compilePreview")}
                </button>
                <p className="text-sm text-muted">{label("context.previewHint")}</p>
              </div>
              {preview ? <ContextBriefPreview brief={preview} /> : null}
            </div>
          ) : null}

          {tab === "revisions" ? (
            <div className="grid gap-4 p-4 lg:grid-cols-[16rem_minmax(0,1fr)]">
              <div>
                {revisionsLoading ? (
                  <p className="text-sm text-muted">{label("common.loading")}</p>
                ) : revisions.length === 0 ? (
                  <p className="text-sm leading-6 text-muted">{label("context.noRevisions")}</p>
                ) : (
                  <ul className="flex flex-col gap-1">
                    {revisions.map((revision) => (
                      <li key={revision.id}>
                        <button
                          className={`w-full rounded-md px-2 py-1.5 text-left text-sm ${
                            openRevision?.id === revision.id
                              ? "bg-background font-medium"
                              : "text-muted hover:text-foreground"
                          }`}
                          type="button"
                          onClick={() => void openStoredRevision(revision.id)}
                        >
                          {formatWhen(revision.created_at)}
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
              {openRevision ? (
                <div className="space-y-3">
                  <p className="text-xs text-muted">{format("context.revision", { id: openRevision.id })}</p>
                  {openRevision.brief ? <ContextBriefPreview brief={openRevision.brief} /> : null}
                </div>
              ) : null}
            </div>
          ) : null}
        </div>
      </div>
    </section>
  );
}

function ContextBriefPreview({ brief }: { brief: SessionBrief }) {
  const dropped = briefDroppedItems(brief.budget.dropped);
  return (
    <div className="space-y-4">
      {dropped.length > 0 ? (
        <p className="text-sm text-muted">{tf("context.dropped", { items: dropped.join(", ") })}</p>
      ) : (
        <p className="text-sm text-muted">{t("context.nothingDropped")}</p>
      )}
      <BriefBlocks sections={brief.sections} empty={t("context.noSections")} />
    </div>
  );
}
