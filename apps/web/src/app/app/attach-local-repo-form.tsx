"use client";

import { useState, type FormEvent } from "react";

import { ApiError, createProjectRepo, type PublicRepo } from "@/lib/api";
import {
  LOCAL_INDEX_MODES,
  localRepoAttachInput,
  type LocalIndexMode,
} from "@/lib/local-root";
import { FIELD_INPUT_CLASS } from "@/lib/ui";
import { Banner } from "@/lib/ui/banner";
import { Button } from "@/lib/ui/button";
import { EmptyState } from "@/lib/ui/empty-state";
import { Field } from "@/lib/ui/field";
import { Panel } from "@/lib/ui/panel";
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
    return <EmptyState description={t("settings.askAdminRepo")} />;
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
    <Panel className="max-w-xl space-y-3">
      <form className="space-y-3" onSubmit={onSubmit}>
        <div>
          <h3 className="text-sm font-medium">{t("settings.attachLocal")}</h3>
          <p className="text-sm text-muted">{t("settings.localRootHint")}</p>
        </div>
        {error ? <Banner tone="danger">{error}</Banner> : null}
        <Field label={t("common.path")}>
          <input
            className={`${FIELD_INPUT_CLASS} font-mono`}
            value={path}
            onChange={(event) => setPath(event.target.value)}
            maxLength={512}
            required
          />
        </Field>
        <Field label={t("settings.index")}>
          <select
            className={FIELD_INPUT_CLASS}
            value={indexMode}
            onChange={(event) => setIndexMode(event.target.value as LocalIndexMode)}
          >
            {LOCAL_INDEX_MODES.map((mode) => (
              <option key={mode} value={mode}>
                {t(MODE_LABELS[mode])}
              </option>
            ))}
          </select>
        </Field>
        <Button type="submit" disabled={pending}>
          {pending ? t("settings.attaching") : t("settings.attachLocal")}
        </Button>
      </form>
    </Panel>
  );
}
