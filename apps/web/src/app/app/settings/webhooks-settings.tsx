"use client";

import { useCallback, useEffect, useState, type FormEvent } from "react";

import { ApiError, fetchProject, updateProject } from "@/lib/api";
import { t } from "@/lib/i18n";
import { DEFAULT_POLL_MS } from "@/lib/poll";
import { FIELD_ERROR_CLASS, FIELD_INPUT_CLASS } from "@/lib/ui";
import { useT } from "@/lib/use-locale";

type WebhooksState = {
  urls: string[];
  error: string | null;
  saving: boolean;
};

export function WebhooksSettings({ projectId }: { projectId: string }) {
  const label = useT();
  const [state, setState] = useState<WebhooksState>({
    urls: [],
    error: null,
    saving: false,
  });
  const [newUrl, setNewUrl] = useState("");

  const load = useCallback(async () => {
    try {
      const full = await fetchProject(projectId);
      const wh = (full.settings as Record<string, unknown>)?.["webhooks"] as Record<string, unknown> | undefined;
      const urls = (wh?.["urls"] as string[]) ?? [];
      setState((prev) => ({ ...prev, urls: Array.isArray(urls) ? urls : [], error: null }));
    } catch {
      // silently ignore — feed is non-critical
    }
  }, [projectId]);

  useEffect(() => {
    void load();
    const id = window.setInterval(load, DEFAULT_POLL_MS);
    return () => window.clearInterval(id);
  }, [load]);

  const save = useCallback(
    async (urls: string[]) => {
      setState((prev) => ({ ...prev, saving: true, error: null }));
      try {
        await updateProject(projectId, { settings: { webhooks: { urls } } });
        setState((prev) => ({ ...prev, urls, saving: false }));
      } catch (err) {
        setState((prev) => ({
          ...prev,
          saving: false,
          error: err instanceof ApiError ? err.message : t("webhooks.failedSave"),
        }));
      }
    },
    [projectId],
  );

  const addUrl = useCallback(
    (e: FormEvent) => {
      e.preventDefault();
      if (!newUrl.trim()) return;
      try {
        new URL(newUrl);
      } catch {
        return;
      }
      const next = [...state.urls, newUrl.trim()];
      void save(next);
      setNewUrl("");
    },
    [newUrl, state.urls, save],
  );

  const removeUrl = useCallback(
    async (url: string) => {
      const next = state.urls.filter((u) => u !== url);
      await save(next);
    },
    [state.urls, save],
  );

  return (
    <section className="space-y-3" id="webhooks">
      <div>
        <h2 className="text-lg font-semibold">{label("webhooks.title")}</h2>
        <p className="text-sm text-muted">{label("webhooks.hint")}</p>
      </div>
      {state.error ? <p className={FIELD_ERROR_CLASS}>{state.error}</p> : null}
      {state.urls.length > 0 && (
        <ul className="space-y-2">
          {state.urls.map((url) => (
            <li
              key={url}
              className="flex items-center justify-between rounded-lg border border-border bg-surface px-4 py-3 text-sm"
            >
              <span className="truncate font-mono text-xs">{url}</span>
              <button
                className="text-muted hover:text-foreground"
                type="button"
                onClick={() => void removeUrl(url)}
              >
                {label("webhooks.remove")}
              </button>
            </li>
          ))}
        </ul>
      )}
      <form className="flex gap-2" onSubmit={addUrl}>
        <input
          className={`flex-1 rounded-md border border-border bg-background px-3 py-2 text-sm ${FIELD_INPUT_CLASS}`}
          placeholder={label("webhooks.urlPlaceholder")}
          value={newUrl}
          onChange={(e) => setNewUrl(e.target.value)}
          type="url"
        />
        <button
          className="h-9 rounded-md bg-accent px-3 text-sm font-medium text-accent-fg disabled:opacity-60"
          type="submit"
          disabled={state.saving || !newUrl.trim()}
        >
          {state.saving ? label("webhooks.saving") : label("webhooks.add")}
        </button>
      </form>
    </section>
  );
}
