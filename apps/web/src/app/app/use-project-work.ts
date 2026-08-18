"use client";

import { useCallback, useEffect, useState } from "react";

import { ApiError } from "@/lib/api";
import {
  fetchProjectMilestones,
  fetchProjectTasks,
  setTaskStatus,
  taskFromConflict,
  type PublicMilestone,
  type PublicTask,
  type TaskStatus,
} from "@/lib/roadmap";
import { ensureBeaconSeed } from "@/lib/seed";
import { useInterval } from "@/lib/use-interval";

import { useSelectedProject } from "./project-context";
import { useToast } from "./toast";

export function useProjectWork(pollMs: number) {
  const { project } = useSelectedProject();
  const { toast } = useToast();
  const [tasks, setTasks] = useState<PublicTask[]>([]);
  const [milestones, setMilestones] = useState<PublicMilestone[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const projectId = project?.id ?? null;

  const reload = useCallback(
    async (opts?: { silent?: boolean }) => {
      if (!projectId) {
        return;
      }
      try {
        await ensureBeaconSeed(projectId);
        const [nextTasks, nextMilestones] = await Promise.all([
          fetchProjectTasks(projectId),
          fetchProjectMilestones(projectId),
        ]);
        setTasks(nextTasks);
        setMilestones(nextMilestones);
        setError(null);
      } catch (caught) {
        setError(caught instanceof ApiError ? caught.message : "failed to load work");
      } finally {
        if (!opts?.silent) {
          setLoading(false);
        }
      }
    },
    [projectId],
  );

  useEffect(() => {
    if (!projectId) {
      const id = window.setTimeout(() => {
        setTasks([]);
        setMilestones([]);
        setError(null);
        setLoading(false);
      }, 0);
      return () => window.clearTimeout(id);
    }
    const selectedId = projectId;
    let cancelled = false;
    async function load() {
      try {
        await ensureBeaconSeed(selectedId);
        if (cancelled) {
          return;
        }
        const [nextTasks, nextMilestones] = await Promise.all([
          fetchProjectTasks(selectedId),
          fetchProjectMilestones(selectedId),
        ]);
        if (cancelled) {
          return;
        }
        setTasks(nextTasks);
        setMilestones(nextMilestones);
        setError(null);
      } catch (caught) {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : "failed to load work");
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [projectId]);

  useInterval(
    () => {
      void reload({ silent: true });
    },
    projectId ? pollMs : null,
  );

  const moveTask = useCallback(
    async (taskId: string, status: TaskStatus) => {
      const current = tasks.find((item) => item.id === taskId);
      if (!current || current.status === status) {
        return;
      }
      const optimistic: PublicTask = { ...current, status };
      setTasks((items) => items.map((item) => (item.id === taskId ? optimistic : item)));
      try {
        const updated = await setTaskStatus(taskId, status, current.version);
        setTasks((items) => items.map((item) => (item.id === taskId ? updated : item)));
      } catch (caught) {
        if (caught instanceof ApiError && caught.code === "version_conflict") {
          const server = taskFromConflict(caught);
          if (server) {
            setTasks((items) => items.map((item) => (item.id === taskId ? server : item)));
            toast("Updated elsewhere — reapplied.");
            return;
          }
        }
        setTasks((items) => items.map((item) => (item.id === taskId ? current : item)));
        toast(caught instanceof ApiError ? caught.message : "failed to update status");
      }
    },
    [tasks, toast],
  );

  return {
    project,
    tasks,
    milestones,
    error,
    loading,
    reload,
    moveTask,
  };
}
