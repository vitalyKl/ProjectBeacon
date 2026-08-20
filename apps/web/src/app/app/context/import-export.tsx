"use client";

import type { ChangeEvent } from "react";

import { useT } from "@/lib/use-locale";

export function ImportExportActions({
  importing,
  exporting,
  previewing,
  pasteOpen,
  onCompile,
  onPickFiles,
  onTogglePaste,
  onExport,
}: {
  importing: boolean;
  exporting: boolean;
  previewing: boolean;
  pasteOpen: boolean;
  onCompile: () => void;
  onPickFiles: (event: ChangeEvent<HTMLInputElement>) => void;
  onTogglePaste: () => void;
  onExport: () => void;
}) {
  const label = useT();
  return (
    <div className="flex flex-wrap items-center gap-2">
      <button
        className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
        type="button"
        disabled={previewing}
        onClick={onCompile}
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
          onChange={onPickFiles}
        />
      </label>
      <button
        className="rounded-md border border-border bg-surface px-3 py-1.5 text-sm"
        type="button"
        aria-pressed={pasteOpen}
        onClick={onTogglePaste}
      >
        {label("context.pasteFile")}
      </button>
      <button
        className="rounded-md border border-border bg-surface px-3 py-1.5 text-sm disabled:opacity-60"
        type="button"
        disabled={exporting}
        onClick={onExport}
      >
        {exporting ? label("context.exporting") : label("context.export")}
      </button>
    </div>
  );
}

export function PasteImportPanel({
  open,
  path,
  body,
  importing,
  onPath,
  onBody,
  onImport,
}: {
  open: boolean;
  path: string;
  body: string;
  importing: boolean;
  onPath: (path: string) => void;
  onBody: (body: string) => void;
  onImport: () => void;
}) {
  const label = useT();
  if (!open) {
    return null;
  }
  return (
    <div className="space-y-2 rounded-lg border border-border bg-surface p-3">
      <label className="flex flex-col gap-1 text-sm">
        {label("common.path")}
        <input
          className="h-9 rounded-md border border-border bg-background px-2"
          value={path}
          onChange={(event) => onPath(event.target.value)}
        />
      </label>
      <label className="flex flex-col gap-1 text-sm">
        {label("common.content")}
        <textarea
          className="min-h-32 rounded-md border border-border bg-background px-2 py-1.5 font-mono text-sm"
          value={body}
          onChange={(event) => onBody(event.target.value)}
        />
      </label>
      <button
        className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
        type="button"
        disabled={importing}
        onClick={onImport}
      >
        {label("context.importPasted")}
      </button>
    </div>
  );
}
