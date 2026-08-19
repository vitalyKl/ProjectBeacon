"use client";

import { useState, type FormEvent } from "react";

import { ApiError, createProjectRepo, type PublicRepo } from "@/lib/api";
import {
  LOCAL_INDEX_MODES,
  localRepoAttachInput,
  type LocalIndexMode,
} from "@/lib/local-root";
import { useT } from "@/lib/use-locale";

const MODE_LABELS: Record<LocalIndexMode, "settings.modeSidecar" | "settings.modeBindMount"> = {
  sidecar: "settings.modeSidecar",
  bind_mount: "settings.modeBindMount",
};

export function AttachLocalRepoForm({
  projectId,
  canSubmit,
  onAttached,
}: {
  projectId: string;
  canSubmit: boolean;
  onAttached: (repo: PublicRepo) => void;
}) {
  const t = useT();
  const [path, setPath] = useState(".");
  const [indexMode, setIndexMode] = useState<LocalIndexMode>("sidecar");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!canSubmit) {
    return <p className="text-sm text-muted">{t("settings.askAdminRepo")}</p>;
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const parsed = localRepoAttachInput(path, indexMode);
    if (!parsed.ok) {
      setError(t("settings.invalidLocalPath"));
      return;
    }
    setPending(true);
    setError(null);
    try {
      const created = await createProjectRepo(projectId, parsed.input);
      setPath(".");
      setIndexMode("sidecar");
      onAttached(created);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("common.failedAttachRepo"));
    } finally {
      setPending(false);
    }
  }

  return (
    <form className="max-w-xl space-y-3 rounded-lg border border-border bg-surface p-4" onSubmit={onSubmit}>
      <div>
        <h3 className="text-sm font-medium">{t("settings.attachLocal")}</h3>
        <p className="text-sm text-muted">{t("settings.localRootHint")}</p>
      </div>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      <label className="flex flex-col gap-1 text-sm">
        {t("common.path")}
        <input
          className="h-9 rounded-md border border-border bg-background px-3 font-mono"
          value={path}
          onChange={(event) => setPath(event.target.value)}
          maxLength={512}
          required
        />
      </label>
      <label className="flex flex-col gap-1 text-sm">
        {t("settings.index")}
        <select
          className="h-9 rounded-md border border-border bg-background px-2"
          value={indexMode}
          onChange={(event) => setIndexMode(event.target.value as LocalIndexMode)}
        >
          {LOCAL_INDEX_MODES.map((mode) => (
            <option key={mode} value={mode}>
              {t(MODE_LABELS[mode])}
            </option>
          ))}
        </select>
      </label>
      <button
        className="h-9 rounded-md bg-accent px-3 text-sm font-medium text-accent-fg disabled:opacity-60"
        type="submit"
        disabled={pending}
      >
        {pending ? t("settings.attaching") : t("settings.attachLocal")}
      </button>
    </form>
  );
}
