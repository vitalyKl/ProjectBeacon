"use client";

import { createContext, useContext } from "react";

import type { PublicProject } from "@/lib/api";

export const ProjectSelectionContext = createContext<{ project: PublicProject | null }>({
  project: null,
});

export function useSelectedProject(): PublicProject | null {
  return useContext(ProjectSelectionContext).project;
}
