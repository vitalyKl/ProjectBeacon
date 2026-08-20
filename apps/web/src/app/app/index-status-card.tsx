"use client";

import type { PublicRepo } from "@/lib/api";
import {
  connectionLabel,
  formatIndexWhen,
  indexModeLabel,
} from "@/lib/index-status";
import { useT } from "@/lib/use-locale";

export function IndexStatusCard({
  repo,
  hostedClone,
}: {
  repo: PublicRepo | null;
  hostedClone: boolean;
}) {
  const t = useT();
  return (
    <article className="space-y-3 rounded-lg border border-border bg-surface p-4">
      <h2 className="text-sm font-semibold tracking-wide uppercase">{t("index.title")}</h2>
      <dl className="grid grid-cols-2 gap-2 text-sm">
        <dt className="text-muted">{t("index.mode")}</dt>
        <dd>{indexModeLabel(repo?.index_mode, { hostedClone })}</dd>
        <dt className="text-muted">{t("index.sidecarLabel")}</dt>
        <dd className="capitalize">{connectionLabel(repo?.sidecar_connected)}</dd>
        <dt className="text-muted">{t("index.worker")}</dt>
        <dd className="capitalize">{connectionLabel(repo?.worker_index_connected)}</dd>
        <dt className="text-muted">{t("index.lastIndexed")}</dt>
        <dd>{repo ? formatIndexWhen(repo.last_indexed_at) : "—"}</dd>
      </dl>
    </article>
  );
}
