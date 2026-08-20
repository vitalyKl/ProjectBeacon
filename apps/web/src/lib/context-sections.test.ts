import { describe, expect, it } from "vitest";

import type { ContextNode, ContextSection } from "./api";
import { t } from "./i18n";
import {
  KNOWN_SECTION_IDS,
  KNOWN_SECTION_TITLES,
  WIZARD_SECTION_IDS,
  contextCreateNodeBody,
  draftsToSections,
  emptyDraftsFromNode,
  knownSectionMessageKey,
  knownSectionTitle,
  wizardBriefSections,
  writtenSectionTitle,
} from "./context-sections";

function section(overrides: Partial<ContextSection> = {}): ContextSection {
  return {
    id: "goals",
    title: "Goals",
    body_md: "Keep a living brief.",
    ordinal: 0,
    ...overrides,
  };
}

function node(overrides: Partial<ContextNode> = {}): ContextNode {
  return {
    id: "node-1",
    project_id: "proj-1",
    repo_id: null,
    task_id: null,
    scope_type: "project",
    path: "",
    sections: [section()],
    source: "native",
    source_path: null,
    review_state: "reviewed",
    updated_at: "2026-08-18T12:00:00.000Z",
    updated_by: { type: "user", id: "user-1", display: "alice" },
    ...overrides,
  };
}

describe("KNOWN_SECTION_IDS", () => {
  it("keeps Definition of Done and tech stack as first-class brief sections", () => {
    expect(KNOWN_SECTION_IDS).toContain("definition_of_done");
    expect(KNOWN_SECTION_IDS).toContain("stack");
    expect(KNOWN_SECTION_IDS).toContain("commands");
    expect(KNOWN_SECTION_IDS).not.toContain("custom");
    expect(WIZARD_SECTION_IDS.every((id) => KNOWN_SECTION_IDS.includes(id))).toBe(true);
  });
});

describe("picker titles", () => {
  it("maps known section ids onto catalog keys", () => {
    expect(knownSectionMessageKey("goals")).toBe("context.section.goals");
    expect(knownSectionMessageKey("stack")).toBe("context.section.stack");
    expect(knownSectionMessageKey("definition_of_done")).toBe("context.section.definition_of_done");
    expect(knownSectionTitle("goals")).toBe(t("context.section.goals"));
    expect(knownSectionTitle("stack")).toBe(t("context.section.stack"));
    expect(knownSectionTitle("definition_of_done")).toBe(t("context.section.definition_of_done"));
    expect(t(knownSectionMessageKey("goals"), "en")).toBe("Goals");
    expect(t(knownSectionMessageKey("goals"), "es")).toBe(t("context.section.goals", "es"));
    expect(writtenSectionTitle("goals")).toBe("Goals");
    expect(writtenSectionTitle("stack")).toBe("Tech stack");
    expect(writtenSectionTitle("definition_of_done")).toBe("Definition of Done");
    expect(writtenSectionTitle("goals")).toBe(KNOWN_SECTION_TITLES.goals);
  });
});

describe("emptyDraftsFromNode", () => {
  it("seeds every known section and keeps stored titles as written", () => {
    const drafts = emptyDraftsFromNode(
      node({
        sections: [
          section({ title: "Ship goals", body_md: "Ship it." }),
          section({
            id: "custom",
            key: "notes",
            title: "Notes",
            body_md: "A custom note.",
            ordinal: 1,
          }),
        ],
      }),
    );
    expect(drafts.map((draft) => draft.id).slice(0, KNOWN_SECTION_IDS.length)).toEqual([
      ...KNOWN_SECTION_IDS,
    ]);
    expect(drafts.find((draft) => draft.id === "goals")).toMatchObject({
      title: "Ship goals",
      body_md: "Ship it.",
    });
    expect(drafts.at(-1)).toMatchObject({ id: "custom", key: "notes", title: "Notes" });
    expect(drafts.find((draft) => draft.id === "stack")?.title).toBe("Tech stack");
  });

  it("promotes a legacy custom Definition of Done section", () => {
    const drafts = emptyDraftsFromNode(
      node({
        sections: [
          section({
            id: "custom",
            key: "definition-of-done",
            title: "Definition of Done",
            body_md: "Tests are green.",
          }),
        ],
      }),
    );
    expect(drafts.find((draft) => draft.id === "definition_of_done")).toMatchObject({
      body_md: "Tests are green.",
    });
    expect(drafts.some((draft) => draft.id === "custom")).toBe(false);
  });
});

describe("draftsToSections", () => {
  it("drops empty known sections and keeps filled custom ones", () => {
    expect(
      draftsToSections([
        { id: "goals", title: "Goals", body_md: "  " },
        { id: "stack", title: "Tech stack", body_md: "TypeScript" },
        { id: "custom", key: "empty", title: "Empty", body_md: "" },
        { id: "custom", key: "notes", title: "Notes", body_md: "Keep it." },
      ]),
    ).toEqual([
      { id: "stack", title: "Tech stack", body_md: "TypeScript", ordinal: 0 },
      { id: "custom", key: "notes", title: "Notes", body_md: "Keep it.", ordinal: 1 },
    ]);
  });
});

describe("contextCreateNodeBody", () => {
  it("omits a client node id so the API mints it", () => {
    const body = contextCreateNodeBody({
      sections: [section()],
      scope: "project",
    });
    expect(body).not.toHaveProperty("id");
    expect(body).toEqual({
      sections: [section()],
      scope_type: "project",
      path: "",
    });
    expect(
      contextCreateNodeBody({
        sections: [section()],
        scope: "repo",
        repoId: "repo-1",
        reviewState: "reviewed",
      }),
    ).toEqual({
      sections: [section()],
      scope_type: "repo",
      path: "",
      repo_id: "repo-1",
      review_state: "reviewed",
    });
  });
});

describe("wizardBriefSections", () => {
  it("posts written English titles for the shared known section ids", () => {
    const sections = wizardBriefSections({
      goals: "Ship.",
      non_goals: "No extra scope.",
      stack: "TypeScript",
      commands: "pnpm test",
      definition_of_done: "Tests are green.",
    });
    expect(sections.map((item) => item.id)).toEqual([...WIZARD_SECTION_IDS]);
    expect(sections[0]).toMatchObject({ title: "Goals", body_md: "Ship." });
    expect(sections[2]).toMatchObject({ title: "Tech stack" });
    expect(sections[4]).toMatchObject({ title: "Definition of Done" });
    expect(sections.map((item) => item.title)).toEqual([
      "Goals",
      "Non-goals",
      "Tech stack",
      "Commands",
      "Definition of Done",
    ]);
  });
});
