import type { ContextSection } from "@beacon/api-spec";

export type AgentsMdScope = {
  repo_id: string | null;
  path: string;
};

export type AgentsMdExportInput = {
  revision: string;
  scope: AgentsMdScope;
  sections: ContextSection[];
};

function yamlScalar(value: string): string {
  if (value === "" || /[:#{}[\],&*?|<>=!%@\\]/.test(value) || /^\s|\s$/.test(value)) {
    return JSON.stringify(value);
  }
  return value;
}

function scopeLine(scope: AgentsMdScope): string {
  const path = scope.path;
  if (scope.repo_id && path) {
    return `${scope.repo_id}:${path}`;
  }
  if (scope.repo_id) {
    return scope.repo_id;
  }
  if (path) {
    return path;
  }
  return "project";
}

function compareExportSections(left: ContextSection, right: ContextSection): number {
  if (left.ordinal !== right.ordinal) {
    return left.ordinal - right.ordinal;
  }
  if (left.id !== right.id) {
    return left.id.localeCompare(right.id);
  }
  const leftKey = left.id === "custom" ? left.key : (left.key ?? "");
  const rightKey = right.id === "custom" ? right.key : (right.key ?? "");
  return leftKey.localeCompare(rightKey);
}

export function exportAgentsMd(input: AgentsMdExportInput): string {
  const sections = [...input.sections].sort(compareExportSections);
  const lines = [
    "---",
    "managed-by: projectbeacon",
    `revision: ${yamlScalar(input.revision)}`,
    `scope: ${yamlScalar(scopeLine(input.scope))}`,
    "---",
    "",
  ];
  for (const section of sections) {
    lines.push(`## ${section.title}`, "");
    if (section.body_md.length > 0) {
      lines.push(section.body_md, "");
    }
  }
  return `${lines.join("\n").replace(/\n+$/, "")}\n`;
}
