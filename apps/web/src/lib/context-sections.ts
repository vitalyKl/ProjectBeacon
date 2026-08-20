import type { ContextNode, ContextSection, ContextSectionId } from "./api";
import { t, tf, type MessageKey } from "./i18n";

export type KnownSectionId = Exclude<ContextSectionId, "custom">;

export const KNOWN_SECTION_IDS = [
  "goals",
  "non_goals",
  "architecture",
  "stack",
  "conventions",
  "style",
  "commands",
  "definition_of_done",
  "security",
  "pitfalls",
  "glossary",
  "ownership",
] as const satisfies readonly KnownSectionId[];

export const WIZARD_SECTION_IDS = [
  "goals",
  "non_goals",
  "stack",
  "commands",
  "definition_of_done",
] as const satisfies readonly KnownSectionId[];

/** Written titles stored on nodes. Picker chrome uses `knownSectionTitle` instead. */
export const KNOWN_SECTION_TITLES = {
  goals: "Goals",
  non_goals: "Non-goals",
  architecture: "Architecture",
  stack: "Tech stack",
  conventions: "Conventions",
  style: "Style",
  commands: "Commands",
  definition_of_done: "Definition of Done",
  security: "Security",
  pitfalls: "Pitfalls",
  glossary: "Glossary",
  ownership: "Ownership",
} as const satisfies Record<KnownSectionId, string>;

export type DraftSection = {
  id: ContextSectionId;
  key?: string;
  title: string;
  body_md: string;
};

export type CreateScope = "project" | "repo" | "path";

export function knownSectionMessageKey(id: KnownSectionId): MessageKey {
  return `context.section.${id}` as MessageKey;
}

export function knownSectionTitle(id: KnownSectionId): string {
  return t(knownSectionMessageKey(id));
}

export function writtenSectionTitle(id: KnownSectionId): string {
  return KNOWN_SECTION_TITLES[id];
}

export function sourceLabel(source: string): string {
  switch (source) {
    case "native":
      return t("context.source.native");
    case "imported_agents_md":
      return "AGENTS.md";
    case "imported_claude_md":
      return "CLAUDE.md";
    case "imported_cursor":
      return "Cursor";
    case "imported_grok":
      return "Grok";
    case "imported_conventions_md":
      return "CONVENTIONS.md";
    default:
      return source;
  }
}

export function scopeLabel(node: Pick<ContextNode, "scope_type" | "path">): string {
  if (node.scope_type === "path") {
    return node.path || t("context.scopePathEmpty");
  }
  if (node.scope_type === "repo") {
    return node.path ? tf("context.scopeRepoWithPath", { path: node.path }) : t("context.scopeRepo");
  }
  return node.scope_type === "project" ? t("context.scopeProject") : node.scope_type;
}

function isLegacyDefinitionOfDone(section: ContextSection): boolean {
  if (section.id !== "custom") {
    return false;
  }
  const key = (section.key ?? "").trim().toLowerCase().replaceAll("_", "-");
  const title = section.title.trim().toLowerCase();
  return key === "definition-of-done" || title === "definition of done";
}

export function emptyDraftsFromNode(node: ContextNode | null): DraftSection[] {
  const byId = new Map<string, ContextSection>();
  for (const section of node?.sections ?? []) {
    if (section.id === "definition_of_done" || isLegacyDefinitionOfDone(section)) {
      byId.set("definition_of_done", { ...section, id: "definition_of_done" });
      continue;
    }
    byId.set(
      section.id === "custom" ? `custom:${section.key ?? section.title}` : section.id,
      section,
    );
  }
  const drafts: DraftSection[] = KNOWN_SECTION_IDS.map((id) => {
    const existing = byId.get(id);
    return {
      id,
      title: existing?.title ?? writtenSectionTitle(id),
      body_md: existing?.body_md ?? "",
    };
  });
  for (const section of node?.sections ?? []) {
    if (section.id !== "custom" || isLegacyDefinitionOfDone(section)) {
      continue;
    }
    drafts.push({
      id: "custom",
      key: section.key,
      title: section.title,
      body_md: section.body_md,
    });
  }
  return drafts;
}

export function draftsToSections(drafts: DraftSection[]): ContextSection[] {
  return drafts
    .filter((draft) => draft.body_md.trim().length > 0 || draft.id === "custom")
    .filter((draft) => draft.id !== "custom" || draft.body_md.trim().length > 0)
    .map((draft, ordinal) =>
      draft.id === "custom"
        ? {
            id: "custom" as const,
            key: (draft.key ?? draft.title).trim() || "untitled",
            title: draft.title.trim() || "Custom",
            body_md: draft.body_md,
            ordinal,
          }
        : {
            id: draft.id,
            title: draft.title,
            body_md: draft.body_md,
            ordinal,
          },
    );
}

export function contextCreateNodeBody(input: {
  sections: ContextSection[];
  scope: CreateScope;
  path?: string;
  repoId?: string;
  reviewState?: "reviewed";
}): Record<string, unknown> {
  const body: Record<string, unknown> = {
    sections: input.sections,
    scope_type: input.scope,
    path: input.scope === "path" ? (input.path ?? "").trim() : "",
  };
  if (input.reviewState) {
    body.review_state = input.reviewState;
  }
  if (input.scope !== "project") {
    body.repo_id = (input.repoId ?? "").trim();
  }
  return body;
}

export function wizardBriefSections(bodies: {
  goals: string;
  non_goals: string;
  stack: string;
  commands: string;
  definition_of_done: string;
}): ContextSection[] {
  return WIZARD_SECTION_IDS.map((id, ordinal) => ({
    id,
    title: writtenSectionTitle(id),
    body_md: bodies[id],
    ordinal,
  }));
}
