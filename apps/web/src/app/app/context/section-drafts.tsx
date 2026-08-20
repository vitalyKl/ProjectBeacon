"use client";

import type { ContextNode, PublicRepo } from "@/lib/api";
import {
  knownSectionTitle,
  scopeLabel,
  sourceLabel,
  type CreateScope,
  type DraftSection,
} from "@/lib/context-sections";
import { repoPickerOptions, type RepoPickerCatalogState } from "@/lib/repo-picker";
import { FIELD_ERROR_CLASS } from "@/lib/ui";
import { useT } from "@/lib/use-locale";

export function SectionDrafts({
  isCreate,
  selected,
  drafts,
  createScope,
  createPath,
  repoCatalog,
  saving,
  onCreateScope,
  onCreatePath,
  onCreateRepoId,
  onDraftBody,
  onSave,
}: {
  isCreate: boolean;
  selected: ContextNode | null;
  drafts: DraftSection[];
  createScope: CreateScope;
  createPath: string;
  repoCatalog: {
    repos: readonly PublicRepo[] | null;
    selectedId: string;
    state: RepoPickerCatalogState;
  };
  saving: boolean;
  onCreateScope: (scope: CreateScope) => void;
  onCreatePath: (path: string) => void;
  onCreateRepoId: (repoId: string) => void;
  onDraftBody: (index: number, body: string) => void;
  onSave: (reviewState?: "reviewed") => void;
}) {
  const label = useT();
  const repoOptions = repoPickerOptions(repoCatalog.repos, repoCatalog.selectedId);

  return (
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
                onChange={(event) => onCreateScope(event.target.value as CreateScope)}
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
                    onChange={(event) => onCreateRepoId(event.target.value)}
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
                  onChange={(event) => onCreatePath(event.target.value)}
                />
              </label>
            ) : null}
          </div>
        </div>
      )}

      {drafts.map((draft, index) => (
        <label key={`${draft.id}:${draft.key ?? draft.title}`} className="flex flex-col gap-1">
          <span className="text-sm font-medium">
            {draft.id !== "custom" ? knownSectionTitle(draft.id) : draft.title}
          </span>
          <textarea
            className="min-h-24 rounded-md border border-border bg-background px-2 py-1.5 font-mono text-sm"
            value={draft.body_md}
            onChange={(event) => onDraftBody(index, event.target.value)}
          />
        </label>
      ))}

      <div className="flex flex-wrap gap-2">
        <button
          className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
          type="button"
          disabled={saving}
          onClick={() => onSave()}
        >
          {saving ? label("common.saving") : label("common.save")}
        </button>
        {selected?.review_state === "needs_review" ? (
          <button
            className="rounded-md border border-border px-3 py-1.5 text-sm disabled:opacity-60"
            type="button"
            disabled={saving}
            onClick={() => onSave("reviewed")}
          >
            {label("context.markReviewed")}
          </button>
        ) : null}
      </div>
    </div>
  );
}
