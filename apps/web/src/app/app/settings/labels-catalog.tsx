"use client";

import { useCallback, useEffect, useState, type FormEvent } from "react";

import { ApiError, type PublicRepo } from "@/lib/api";
import {
  createLabel,
  fetchProjectLabels,
  formatLabelPaths,
  labelStatusLabel,
  patchLabel,
  removeLabelPath,
  sortLabels,
  upsertLabelPath,
  type PublicLabel,
} from "@/lib/labels";
import { t } from "@/lib/i18n";
import { DEFAULT_POLL_MS } from "@/lib/poll";
import { useT } from "@/lib/use-locale";

export function LabelsCatalog({
  projectId,
  repos,
  canWrite,
}: {
  projectId: string;
  repos: PublicRepo[] | null;
  canWrite: boolean;
}) {
  const labelText = useT();
  const [labels, setLabels] = useState<PublicLabel[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [color, setColor] = useState("");
  const [repoId, setRepoId] = useState("");
  const [path, setPath] = useState("");
  const [pending, setPending] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editDescription, setEditDescription] = useState("");
  const [editColor, setEditColor] = useState("");
  const [editRepoId, setEditRepoId] = useState("");
  const [editPath, setEditPath] = useState("");
  const [editPending, setEditPending] = useState(false);

  const reload = useCallback(
    async (opts?: { silent?: boolean }) => {
      try {
        setLabels(sortLabels(await fetchProjectLabels(projectId)));
        setError(null);
      } catch (caught) {
        setError(caught instanceof ApiError ? caught.message : t("labels.failedLoad"));
      } finally {
        if (!opts?.silent) {
          setLoading(false);
        }
      }
    },
    [projectId],
  );

  useEffect(() => {
    const id = window.setTimeout(() => {
      void reload();
    }, 0);
    return () => window.clearTimeout(id);
  }, [reload]);

  useEffect(() => {
    const id = window.setInterval(() => {
      void reload({ silent: true });
    }, DEFAULT_POLL_MS);
    return () => window.clearInterval(id);
  }, [reload]);

  function resetForm() {
    setName("");
    setDescription("");
    setColor("");
    setRepoId(repos?.[0]?.id ?? "");
    setPath("");
    setError(null);
  }

  async function onCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const nextName = name.trim();
    if (!nextName) {
      setError(t("common.nameRequired"));
      return;
    }
    setPending(true);
    setError(null);
    try {
      const created = await createLabel(projectId, {
        name: nextName,
        description: description.trim(),
        color: color.trim() || null,
        paths: repoId && path.trim() ? [{ repo_id: repoId, path: path.trim() }] : [],
      });
      setLabels((current) =>
        sortLabels([created, ...current.filter((item) => item.id !== created.id)]),
      );
      resetForm();
      setOpen(false);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("labels.failedCreate"));
    } finally {
      setPending(false);
    }
  }

  function applyUpdated(updated: PublicLabel) {
    setLabels((current) =>
      sortLabels(current.map((item) => (item.id === updated.id ? updated : item))),
    );
  }

  async function onActivate(label: PublicLabel) {
    try {
      applyUpdated(await patchLabel(label.id, { status: "active" }));
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("labels.failedActivate"));
    }
  }

  function startEdit(label: PublicLabel) {
    setEditingId(label.id);
    setEditName(label.name);
    setEditDescription(label.description);
    setEditColor(label.color ?? "");
    setEditRepoId(repos?.[0]?.id ?? "");
    setEditPath("");
    setError(null);
  }

  function cancelEdit() {
    setEditingId(null);
    setEditName("");
    setEditDescription("");
    setEditColor("");
    setEditRepoId("");
    setEditPath("");
  }

  async function onSaveEdit(event: FormEvent<HTMLFormElement>, label: PublicLabel) {
    event.preventDefault();
    const nextName = editName.trim();
    if (!nextName) {
      setError(t("common.nameRequired"));
      return;
    }
    setEditPending(true);
    setError(null);
    try {
      applyUpdated(
        await patchLabel(label.id, {
          name: nextName,
          description: editDescription.trim(),
          color: editColor.trim() || null,
        }),
      );
      cancelEdit();
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("labels.failedUpdate"));
    } finally {
      setEditPending(false);
    }
  }

  async function onAddPath(label: PublicLabel) {
    const nextPath = editPath.trim();
    if (!editRepoId || !nextPath) {
      setError(t("labels.chooseRepoPath"));
      return;
    }
    const next = upsertLabelPath(label.paths, { repo_id: editRepoId, path: nextPath });
    if (next === label.paths) {
      setEditPath("");
      return;
    }
    setEditPending(true);
    setError(null);
    try {
      applyUpdated(await patchLabel(label.id, { paths: next }));
      setEditPath("");
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("labels.failedPaths"));
    } finally {
      setEditPending(false);
    }
  }

  async function onRemovePath(label: PublicLabel, repoId: string, pathValue: string) {
    setEditPending(true);
    setError(null);
    try {
      applyUpdated(await patchLabel(label.id, { paths: removeLabelPath(label.paths, repoId, pathValue) }));
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("labels.failedPaths"));
    } finally {
      setEditPending(false);
    }
  }

  return (
    <section className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-lg font-semibold">{labelText("labels.areas")}</h2>
          <p className="text-sm text-muted">{labelText("labels.areasHint")}</p>
        </div>
        {canWrite ? (
          <button
            className="rounded-md border border-border px-3 py-1.5 text-sm"
            type="button"
            onClick={() => {
              resetForm();
              setOpen(true);
            }}
          >
            {labelText("labels.newArea")}
          </button>
        ) : null}
      </div>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {loading ? <p className="text-sm text-muted">{labelText("common.loading")}</p> : null}
      {open ? (
        <form
          className="max-w-xl space-y-3 rounded-lg border border-border bg-surface p-4"
          onSubmit={onCreate}
        >
          <label className="flex flex-col gap-1 text-sm">
            {labelText("common.name")}
            <input
              className="h-9 rounded-md border border-border bg-background px-3"
              value={name}
              onChange={(event) => setName(event.target.value)}
              maxLength={80}
              required
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            {labelText("common.description")}
            <textarea
              className="min-h-16 rounded-md border border-border bg-background px-3 py-2"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              maxLength={400}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            {labelText("labels.color")}
            <input
              className="h-9 rounded-md border border-border bg-background px-3"
              value={color}
              onChange={(event) => setColor(event.target.value)}
              placeholder="#3366ff"
              maxLength={7}
            />
          </label>
          <div className="flex flex-wrap gap-2">
            <select
              className="h-9 min-w-40 rounded-md border border-border bg-background px-2 text-sm"
              aria-label={labelText("common.repository")}
              value={repoId}
              onChange={(event) => setRepoId(event.target.value)}
            >
              <option value="">{labelText("labels.noPrefix")}</option>
              {(repos ?? []).map((repo) => (
                <option key={repo.id} value={repo.id}>
                  {repo.remote_url || repo.local_root_hint || repo.id}
                </option>
              ))}
            </select>
            <input
              className="h-9 min-w-40 flex-1 rounded-md border border-border bg-background px-3 text-sm"
              placeholder="apps/api"
              value={path}
              onChange={(event) => setPath(event.target.value)}
              disabled={!repoId}
            />
          </div>
          <div className="flex gap-2">
            <button
              className="h-9 rounded-md bg-accent px-3 text-sm font-medium text-accent-fg disabled:opacity-60"
              type="submit"
              disabled={pending}
            >
              {pending ? labelText("common.saving") : labelText("common.save")}
            </button>
            <button
              className="h-9 rounded-md border border-border px-3 text-sm"
              type="button"
              onClick={() => {
                resetForm();
                setOpen(false);
              }}
            >
              {labelText("common.cancel")}
            </button>
          </div>
        </form>
      ) : null}
      {labels.length === 0 && !loading ? (
        <p className="text-sm text-muted">
          {labelText("labels.empty")}
        </p>
      ) : (
        <ul className="space-y-2">
          {labels.map((label) => (
            <li
              key={label.id}
              className="rounded-lg border border-border bg-surface px-4 py-3 text-sm"
            >
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0 space-y-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium">{label.name}</span>
                    <span className="text-xs tracking-wide text-muted uppercase">
                      {labelStatusLabel(label.status)}
                    </span>
                    <span className="text-xs text-muted">{label.slug}</span>
                  </div>
                  {label.description ? (
                    <p className="text-muted whitespace-pre-wrap">{label.description}</p>
                  ) : null}
                  <p className="text-xs text-muted">{formatLabelPaths(label)}</p>
                </div>
                {canWrite ? (
                  <div className="flex flex-wrap gap-2">
                    {label.status === "proposed" ? (
                      <button
                        className="rounded-md border border-border px-3 py-1.5 text-sm"
                        type="button"
                        onClick={() => void onActivate(label)}
                      >
                        {labelText("labels.activate")}
                      </button>
                    ) : null}
                    <button
                      className="rounded-md border border-border px-3 py-1.5 text-sm"
                      type="button"
                      onClick={() => startEdit(label)}
                    >
                      {labelText("common.edit")}
                    </button>
                  </div>
                ) : null}
              </div>
            {canWrite && editingId === label.id ? (
              <form className="mt-3 space-y-3" onSubmit={(event) => void onSaveEdit(event, label)}>
                <div className="grid gap-2 sm:grid-cols-2">
                  <label className="flex flex-col gap-1 text-sm">
                    {labelText("common.name")}
                    <input
                      className="h-9 rounded-md border border-border bg-background px-3"
                      value={editName}
                      onChange={(event) => setEditName(event.target.value)}
                      maxLength={80}
                      required
                    />
                  </label>
                  <label className="flex flex-col gap-1 text-sm">
                    {labelText("labels.color")}
                    <input
                      className="h-9 rounded-md border border-border bg-background px-3"
                      value={editColor}
                      onChange={(event) => setEditColor(event.target.value)}
                      placeholder="#3366ff"
                      maxLength={7}
                    />
                  </label>
                </div>
                <label className="flex flex-col gap-1 text-sm">
                  {labelText("common.description")}
                  <textarea
                    className="min-h-16 rounded-md border border-border bg-background px-3 py-2"
                    value={editDescription}
                    onChange={(event) => setEditDescription(event.target.value)}
                    maxLength={400}
                  />
                </label>
                <div className="space-y-2">
                  <p className="text-xs text-muted">{labelText("labels.pathPrefixes")}</p>
                  {label.paths.length === 0 ? (
                    <p className="text-xs text-muted">{labelText("labels.noneYet")}</p>
                  ) : (
                    <ul className="flex flex-wrap gap-2">
                      {label.paths.map((item) => (
                        <li
                          key={`${item.repo_id}:${item.path}`}
                          className="flex items-center gap-2 rounded-md border border-border px-2 py-1 text-xs"
                        >
                          <span className="font-mono">{item.path}</span>
                          <button
                            className="text-muted underline"
                            type="button"
                            onClick={() => void onRemovePath(label, item.repo_id, item.path)}
                            disabled={editPending}
                          >
                            {labelText("labels.remove")}
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                  <div className="flex flex-wrap gap-2">
                    <select
                      className="h-9 min-w-40 rounded-md border border-border bg-background px-2 text-sm"
                      aria-label={labelText("labels.repoForPrefix")}
                      value={editRepoId}
                      onChange={(event) => setEditRepoId(event.target.value)}
                    >
                      <option value="">{labelText("labels.chooseRepo")}</option>
                      {(repos ?? []).map((repo) => (
                        <option key={repo.id} value={repo.id}>
                          {repo.remote_url || repo.local_root_hint || repo.id}
                        </option>
                      ))}
                    </select>
                    <input
                      className="h-9 min-w-40 flex-1 rounded-md border border-border bg-background px-3 text-sm"
                      placeholder="apps/api"
                      value={editPath}
                      onChange={(event) => setEditPath(event.target.value)}
                      disabled={!editRepoId}
                    />
                    <button
                      className="h-9 rounded-md border border-border px-3 text-sm disabled:opacity-60"
                      type="button"
                      onClick={() => void onAddPath(label)}
                      disabled={editPending || !editRepoId}
                    >
                      {labelText("labels.addPrefix")}
                    </button>
                  </div>
                </div>
                <div className="flex gap-2">
                  <button
                    className="h-9 rounded-md bg-accent px-3 text-sm font-medium text-accent-fg disabled:opacity-60"
                    type="submit"
                    disabled={editPending}
                  >
                    {editPending ? labelText("common.saving") : labelText("labels.saveArea")}
                  </button>
                  <button
                    className="h-9 rounded-md border border-border px-3 text-sm"
                    type="button"
                    onClick={cancelEdit}
                  >
                    {labelText("common.cancel")}
                  </button>
                </div>
              </form>
            ) : null}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
