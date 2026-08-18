"use client";

import { useCallback, useEffect, useState } from "react";

import { ApiError, fetchProjectRepos, fetchRepo, type PublicRepo } from "@/lib/api";
import { LIVE_POLL_MS } from "@/lib/poll";

import { useAppSelection } from "./project-context";

function tunnelServing(repos: PublicRepo[]): boolean {
  return repos.some(
    (repo) =>
      repo.sidecar_connected && (repo.index_mode === "sidecar" || repo.index_mode === "both"),
  );
}

export function SidecarTunnelBanner({ enabled }: { enabled: boolean }) {
  const selection = useAppSelection();
  const project = selection?.project ?? null;
  const [servingProjectId, setServingProjectId] = useState<string | null>(null);
  const serving = Boolean(enabled && project && servingProjectId === project.id);

  const load = useCallback(async (projectId: string) => {
    const listed = await fetchProjectRepos(projectId);
    if (!listed) {
      return false;
    }
    const detailed = await Promise.all(
      listed.map(async (repo) => (await fetchRepo(repo.id)) ?? repo),
    );
    return tunnelServing(detailed);
  }, []);

  useEffect(() => {
    if (!enabled || !project) {
      return;
    }
    const projectId = project.id;
    let cancelled = false;
    void load(projectId)
      .then((next) => {
        if (!cancelled) {
          setServingProjectId(next ? projectId : null);
        }
      })
      .catch((caught: unknown) => {
        if (!cancelled && !(caught instanceof ApiError)) {
          setServingProjectId(null);
        }
      });
    const id = window.setInterval(() => {
      void load(projectId)
        .then((next) => {
          if (!cancelled) {
            setServingProjectId(next ? projectId : null);
          }
        })
        .catch(() => {
          // keep the last banner state across a transient poll error
        });
    }, LIVE_POLL_MS);
    return () => {
      cancelled = true;
      window.clearInterval(id);
    };
  }, [enabled, load, project]);

  if (!serving) {
    return null;
  }

  return (
    <div
      className="border-b border-amber-300 bg-amber-50 px-4 py-2 text-sm text-amber-950 dark:border-amber-800 dark:bg-amber-950 dark:text-amber-50"
      data-sidecar-tunnel-banner="on"
      role="status"
    >
      This project is serving code through the sidecar tunnel. File contents pass through the
      control plane.
    </div>
  );
}
