"use client";

import { useCallback, useEffect, useRef, useState } from "react";

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
  const requestSeq = useRef(0);
  const pendingMoves = useRef(new Set<string>());

  const applyList = useCallback((nextTasks: PublicTask[], nextMilestones: PublicMilestone[]) => {
    setMilestones(nextMilestones);
    setTasks((current) => {
      const byId = new Map(current.map((item) => [item.id, item]));
      return nextTasks.map((item) => {
        const local = byId.get(item.id);
        if (!local) {
          return item;
        }
        if (pendingMoves.current.has(item.id) || item.version < local.version) {
          return local;
        }
        return item;
      });
    });
  }, []);

  const reload = useCallback(
    async (opts?: { silent?: boolean }) => {
      if (!projectId) {
        return;
      }
      const seq = ++requestSeq.current;
      const selectedId = projectId;
      try {
        await ensureBeaconSeed(selectedId);
        if (seq !== requestSeq.current) {
          return;
        }
        const [nextTasks, nextMilestones] = await Promise.all([
          fetchProjectTasks(selectedId),
          fetchProjectMilestones(selectedId),
        ]);
        if (seq !== requestSeq.current) {
          return;
        }
        applyList(nextTasks, nextMilestones);
        setError(null);
      } catch (caught) {
        if (seq === requestSeq.current) {
          setError(caught instanceof ApiError ? caught.message : "failed to load work");
        }
      } finally {
        if (!opts?.silent && seq === requestSeq.current) {
          setLoading(false);
        }
      }
    },
    [applyList, projectId],
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
    const id = window.setTimeout(() => {
      void reload();
    }, 0);
    return () => {
      window.clearTimeout(id);
      requestSeq.current += 1;
    };
  }, [projectId, reload]);

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
      pendingMoves.current.add(taskId);
      setTasks((items) => items.map((item) => (item.id === taskId ? optimistic : item)));
      try {
        const updated = await setTaskStatus(taskId, status, current.version);
        requestSeq.current += 1;
        setTasks((items) => items.map((item) => (item.id === taskId ? updated : item)));
      } catch (caught) {
        if (caught instanceof ApiError && caught.code === "version_conflict") {
          const server = taskFromConflict(caught);
          if (server) {
            requestSeq.current += 1;
            setTasks((items) => items.map((item) => (item.id === taskId ? server : item)));
            toast("Updated elsewhere — reapplied.");
            return;
          }
        }
        requestSeq.current += 1;
        setTasks((items) => items.map((item) => (item.id === taskId ? current : item)));
        toast(caught instanceof ApiError ? caught.message : "failed to update status");
      } finally {
        pendingMoves.current.delete(taskId);
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
