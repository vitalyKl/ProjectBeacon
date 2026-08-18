"use client";

import { createContext, useContext, type ReactNode } from "react";

import type { PublicOrg, PublicProject } from "@/lib/api";

export type ProjectContextValue = {
  org: PublicOrg | null;
  project: PublicProject | null;
  projects: PublicProject[];
  loading: boolean;
  setProjectId: (projectId: string) => void;
  reloadProjects: () => Promise<void>;
};

const ProjectContext = createContext<ProjectContextValue>({
  org: null,
  project: null,
  projects: [],
  loading: true,
  setProjectId: () => undefined,
  reloadProjects: async () => undefined,
});

export function ProjectProvider({
  value,
  children,
}: {
  value: ProjectContextValue;
  children: ReactNode;
}) {
  return <ProjectContext.Provider value={value}>{children}</ProjectContext.Provider>;
}

export function useSelectedProject(): ProjectContextValue {
  return useContext(ProjectContext);
}
