"use client";

import { useAppSelection } from "../project-context";

import { SettingsView } from "./settings-view";

export function SettingsClient({
  hostedClone,
  sidecarTunnel,
}: {
  hostedClone: boolean;
  sidecarTunnel: boolean;
}) {
  const selection = useAppSelection();
  const project = selection?.project ?? null;
  return (
    <SettingsView
      key={project?.id ?? "none"}
      project={project}
      me={selection?.me ?? null}
      hostedClone={hostedClone}
      sidecarTunnel={sidecarTunnel}
      onProjectSaved={selection?.replaceProject}
    />
  );
}
