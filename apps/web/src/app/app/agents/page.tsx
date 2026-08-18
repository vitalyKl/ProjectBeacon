"use client";

import { useAppSelection } from "../project-context";

import { AgentsView } from "./agents-view";

export default function AgentsPage() {
  const selection = useAppSelection();
  const project = selection?.project ?? null;
  return <AgentsView key={project?.id ?? "none"} project={project} />;
}
