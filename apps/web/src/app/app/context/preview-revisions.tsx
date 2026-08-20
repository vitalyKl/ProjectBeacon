"use client";

import type { ContextRevision, ContextRevisionSummary, SessionBrief } from "@/lib/api";
import { briefDroppedItems } from "@/lib/brief-preview";
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

export function ContextBriefPreview({ brief }: { brief: SessionBrief }) {
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
  loading,
  onOpen,
}: {
  revisions: ContextRevisionSummary[];
  openRevision: ContextRevision | null;
  loading: boolean;
  onOpen: (revisionId: string) => void;
}) {
  const label = useT();
  const format = useTf();
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
          {openRevision.brief ? <ContextBriefPreview brief={openRevision.brief} /> : null}
        </div>
      ) : null}
    </div>
  );
}
