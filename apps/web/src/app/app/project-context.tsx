"use client";

import { createContext, useContext, type ReactNode } from "react";

import type { PublicMe, PublicOrg, PublicProject } from "@/lib/api";

export type AppSelection = {
  me: PublicMe;
  org: PublicOrg | null;
  project: PublicProject | null;
  projects: PublicProject[];
  loading: boolean;
  setProjectId: (projectId: string) => void;
  reloadProjects: () => Promise<void>;
  replaceProject: (project: PublicProject) => void;
};

export type ProjectContextValue = Pick<
  AppSelection,
  "org" | "project" | "projects" | "loading" | "setProjectId" | "reloadProjects"
>;

const AppSelectionContext = createContext<AppSelection | null>(null);

const EMPTY_PROJECT: ProjectContextValue = {
  org: null,
  project: null,
  projects: [],
  loading: true,
  setProjectId: () => undefined,
  reloadProjects: async () => undefined,
};

export function AppSelectionProvider({
  value,
  children,
}: {
  value: AppSelection;
  children: ReactNode;
}) {
  return <AppSelectionContext.Provider value={value}>{children}</AppSelectionContext.Provider>;
}

export function useAppSelection(): AppSelection | null {
  return useContext(AppSelectionContext);
}

export function useSelectedProject(): ProjectContextValue {
  return useAppSelection() ?? EMPTY_PROJECT;
}
