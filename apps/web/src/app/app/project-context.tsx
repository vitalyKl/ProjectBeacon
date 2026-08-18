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
import type { PublicMe, PublicOrg, PublicProject } from "@/lib/api";
export type AppSelection = {
  me: PublicMe;
  replaceProject: (project: PublicProject) => void;
const AppSelectionContext = createContext<AppSelection | null>(null);
export function AppSelectionProvider({
  value: AppSelection;
  return <AppSelectionContext.Provider value={value}>{children}</AppSelectionContext.Provider>;
export function useAppSelection(): AppSelection | null {
  return useContext(AppSelectionContext);
}
