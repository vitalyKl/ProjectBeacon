"use client";

import { useCallback, useEffect, useMemo, useRef, useState, type ChangeEvent } from "react";

import {
  ApiError,
  compileContext,
  createContextNode,
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
  type PublicRepo,
  type SessionBrief,
} from "@/lib/api";
import {
  contextCreateNodeBody,
  draftsToSections,
  emptyDraftsFromNode,
  type CreateScope,
  type DraftSection,
} from "@/lib/context-sections";
import { t } from "@/lib/i18n";
import {
  importedNodesNotice,
  readImportPayload,
  takeInputFiles,
  unrecognizedImportMessage,
} from "@/lib/import-files";
import { projectRepoCatalog } from "@/lib/repo-picker";
import { FIELD_ERROR_CLASS } from "@/lib/ui";
import { useT } from "@/lib/use-locale";

import { useSelectedProject } from "../project-context";
import { ImportExportActions, PasteImportPanel } from "./import-export";
import { NodesList } from "./nodes-list";
import { PreviewTab, RevisionsTab } from "./preview-revisions";
import { SectionDrafts } from "./section-drafts";

type EditorTab = "edit" | "preview" | "revisions";

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
      const sections = draftsToSections(drafts);
      let saved: ContextNode;
      if (isCreate) {
        saved = await createContextNode(
          project.id,
          contextCreateNodeBody({
            sections,
            scope: createScope,
            path: createPath,
            repoId: createRepoId,
            reviewState,
          }),
        );
      } else if (!selected) {
        return;
      } else {
        saved = await putContextNode(project.id, selected.id, {
          sections,
          ...(reviewState ? { review_state: reviewState } : {}),
        });
      }
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
        <ImportExportActions
          importing={importing}
          exporting={exporting}
          previewing={previewing}
          pasteOpen={pasteOpen}
          onCompile={() => {
            setTab("preview");
            void runPreview();
          }}
          onPickFiles={(event) => void onPickFiles(event)}
          onTogglePaste={() => setPasteOpen((open) => !open)}
          onExport={() => void onExport()}
        />
      </div>

      <PasteImportPanel
        open={pasteOpen}
        path={pastePath}
        body={pasteBody}
        importing={importing}
        onPath={setPastePath}
        onBody={setPasteBody}
        onImport={() => void onPasteImport()}
      />

      {error ? <p className={FIELD_ERROR_CLASS}>{error}</p> : null}
      {notice ? <p className="text-sm text-muted">{notice}</p> : null}

      <div className="grid min-h-[32rem] gap-4 lg:grid-cols-[18rem_minmax(0,1fr)]">
        <NodesList
          nodes={nodes}
          selectedId={selectedId}
          isCreate={isCreate}
          createScope={createScope}
          onBeginCreate={beginCreate}
          onSelect={selectNode}
        />

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
            <SectionDrafts
              isCreate={isCreate}
              selected={selected}
              drafts={drafts}
              createScope={createScope}
              createPath={createPath}
              repoCatalog={repoCatalog}
              saving={saving}
              onCreateScope={setCreateScope}
              onCreatePath={setCreatePath}
              onCreateRepoId={setCreateRepoId}
              onDraftBody={updateDraft}
              onSave={(reviewState) => void save(reviewState)}
            />
          ) : null}

          {tab === "preview" ? (
            <PreviewTab
              preview={preview}
              previewing={previewing}
              onCompile={() => void runPreview()}
            />
          ) : null}

          {tab === "revisions" ? (
            <RevisionsTab
              revisions={revisions}
              openRevision={openRevision}
              preview={preview}
              loading={revisionsLoading}
              onOpen={(revisionId) => void openStoredRevision(revisionId)}
            />
          ) : null}
        </div>
      </div>
    </section>
  );
}
