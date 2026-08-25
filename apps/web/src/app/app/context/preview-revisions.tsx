"use client";

import type { ContextRevision, ContextRevisionSummary, SessionBrief } from "@/lib/api";
import { t, tf } from "@/lib/i18n";
import { useT, useTf } from "@/lib/use-locale";

import { BriefBlocks } from "../brief-blocks";

export function formatWhen(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }
  return date.toLocaleString();
}

function droppedSectionName(id: string): string {
  if (id === "decisions") return "Decisions";
  if (id === "handoff") return "Handoff";
  if (id === "changed_scope") return "Changed scope";
  if (id === "tree_capsule") return "Tree";
  if (id.startsWith("section:custom:")) return id.slice("section:custom:".length);
  if (id.startsWith("section:")) return id.slice("section:".length);
  return id;
}

function droppedSectionNames(dropped: readonly string[] | null | undefined): string[] {
  if (!dropped || dropped.length === 0) return [];
  return dropped.map(droppedSectionName);
}

export function ContextBriefPreview({ brief }: { brief: SessionBrief }) {
  const budget = brief.budget;
  const droppedNames = droppedSectionNames(brief.budget.dropped);
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2 text-xs">
        <span className="text-muted">
          {tf("context.budget", { used: String(budget.used_estimate), requested: String(budget.requested) })}
        </span>
        {budget.overflow ? (
          <span className="text-amber-600 dark:text-amber-400">
            {t("context.overflow")}
          </span>
        ) : null}
      </div>
      {droppedNames.length > 0 ? (
        <ul className="flex flex-wrap gap-x-3 gap-y-1 text-xs text-muted">
          {droppedNames.map((name) => (
            <li key={name}>{tf("context.droppedSection", { name })}</li>
          ))}
        </ul>
      ) : (
        <p className="text-xs text-muted">{t("context.nothingDropped")}</p>
      )}
      <BriefBlocks sections={brief.sections} empty={t("context.noSections")} />
    </div>
  );
}

export function PreviewTab({
  preview,
  previewing,
  onCompile,
}: {
  preview: SessionBrief | null;
  previewing: boolean;
  onCompile: () => void;
}) {
  const label = useT();
  return (
    <div className="space-y-4 p-4">
      <div className="flex flex-wrap items-center gap-2">
        <button
          className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
          type="button"
          disabled={previewing}
          onClick={onCompile}
        >
          {previewing ? label("common.compiling") : label("context.compilePreview")}
        </button>
        <p className="text-sm text-muted">{label("context.previewHint")}</p>
      </div>
      {preview ? <ContextBriefPreview brief={preview} /> : null}
    </div>
  );
}

export function RevisionsTab({
  revisions,
  openRevision,
  preview,
  loading,
  onOpen,
}: {
  revisions: ContextRevisionSummary[];
  openRevision: ContextRevision | null;
  preview: SessionBrief | null;
  loading: boolean;
  onOpen: (revisionId: string) => void;
}) {
  const label = useT();
  const format = useTf();

function sectionKey(id?: string, title?: string): string {
  if (id) return id;
  return `title:${title ?? ""}`;
}

function computeDiff(
  current: SessionBrief,
  stored: SessionBrief,
): { added: string[]; removed: string[]; common: number } {
  const currentKeys = new Set(current.sections.map((s) => sectionKey(s.id, s.title)));
  const storedKeys = new Set(stored.sections.map((s) => sectionKey(s.id, s.title)));

    const added: string[] = [];
    for (const key of currentKeys) {
      if (!storedKeys.has(key)) {
        added.push(key);
      }
    }

    const removed: string[] = [];
    for (const key of storedKeys) {
      if (!currentKeys.has(key)) {
        removed.push(key);
      }
    }

    const common = currentKeys.size - added.length;
    return { added, removed, common: common > 0 ? common : 0 };
  }

  const diff = preview && openRevision?.brief ? computeDiff(preview, openRevision.brief) : null;

  return (
    <div className="grid gap-4 p-4 lg:grid-cols-[16rem_minmax(0,1fr)]">
      <div>
        {loading ? (
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
                  onClick={() => onOpen(revision.id)}
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
          {preview ? (
            <div className="space-y-2">
              {diff?.added.length ? (
                <details className="rounded-md border border-border px-2 py-1.5 text-xs">
                  <summary className="cursor-pointer font-medium text-muted">
                    {tf("context.diffOnlyInCurrent", { count: String(diff.added.length) })} ({diff.added.length})
                  </summary>
                  <ul className="mt-1 list-disc pl-4 text-muted">
                    {diff.added.map((key) => (
                      <li key={key}>{droppedSectionName(key)}</li>
                    ))}
                  </ul>
                </details>
              ) : null}
              {diff?.removed.length ? (
                <details className="rounded-md border border-border px-2 py-1.5 text-xs">
                  <summary className="cursor-pointer font-medium text-muted">
                    {tf("context.diffOnlyInRevision", { label: formatWhen(openRevision.created_at) })} ({diff.removed.length})
                  </summary>
                  <ul className="mt-1 list-disc pl-4 text-muted">
                    {diff.removed.map((key) => (
                      <li key={key}>{droppedSectionName(key)}</li>
                    ))}
                  </ul>
                </details>
              ) : null}
              {diff && diff.common > 0 ? (
                <p className="text-xs text-muted">
                  {tf("context.diffCommon", { count: String(diff.common) })}
                </p>
              ) : null}
            </div>
          ) : null}
          {openRevision.brief ? <ContextBriefPreview brief={openRevision.brief} /> : null}
        </div>
      ) : null}
    </div>
  );
}
