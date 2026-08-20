"use client";

import { useCallback, useEffect, useState } from "react";

import { fetchProjectLabels, type PublicLabel } from "@/lib/labels";
import type { PublicTask } from "@/lib/roadmap";

import { readStoredId, writeStoredId } from "./selection";

export const WORK_AREA_STORAGE_PREFIX = "beacon.work.area";

export function workAreaStorageKey(projectId: string): string {
  return `${WORK_AREA_STORAGE_PREFIX}.${projectId}`;
}

export function readWorkAreaFilter(projectId: string | null): string {
  if (!projectId) {
    return "";
  }
  return readStoredId(workAreaStorageKey(projectId)) ?? "";
}

export function writeWorkAreaFilter(projectId: string, labelId: string): void {
  writeStoredId(workAreaStorageKey(projectId), labelId || null);
}

export function taskMatchesArea(task: Pick<PublicTask, "labels">, labelId: string): boolean {
  return !labelId || (task.labels ?? []).some((label) => label.id === labelId);
}

export function useWorkFilters(projectId: string | null) {
  const [labelId, setLabelIdState] = useState("");
  const [catalog, setCatalog] = useState<PublicLabel[]>([]);

  useEffect(() => {
    if (!projectId) {
      const id = window.setTimeout(() => {
        setCatalog([]);
        setLabelIdState("");
      }, 0);
      return () => window.clearTimeout(id);
    }
    const selectedId = projectId;
    const stored = readWorkAreaFilter(selectedId);
    let cancelled = false;
    const id = window.setTimeout(() => {
      setLabelIdState(stored);
      void fetchProjectLabels(selectedId)
        .then((items) => {
          if (cancelled) {
            return;
          }
          setCatalog(items);
          if (stored && !items.some((item) => item.id === stored)) {
            setLabelIdState("");
            writeWorkAreaFilter(selectedId, "");
          }
        })
        .catch(() => {
          if (cancelled) {
            return;
          }
          setCatalog([]);
          if (stored) {
            setLabelIdState("");
            writeWorkAreaFilter(selectedId, "");
          }
        });
    }, 0);
    return () => {
      cancelled = true;
      window.clearTimeout(id);
    };
  }, [projectId]);

  const setLabelId = useCallback(
    (next: string) => {
      setLabelIdState(next);
      if (projectId) {
        writeWorkAreaFilter(projectId, next);
      }
    },
    [projectId],
  );

  return { labelId, setLabelId, catalog };
}
