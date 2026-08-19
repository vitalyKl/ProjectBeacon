import type { ContextSection, ContextSectionId } from "@beacon/api-spec";

export type KnownSectionId = Exclude<ContextSectionId, "custom">;

const HEADING_ALIASES: ReadonlyArray<{ pattern: RegExp; id: KnownSectionId }> = [
  { pattern: /^(product\s+)?goals?$/i, id: "goals" },
  { pattern: /^(non[-\s]?goals?|out of scope)$/i, id: "non_goals" },
  { pattern: /^(architecture|system design)$/i, id: "architecture" },
  { pattern: /^(conventions?)$/i, id: "conventions" },
  { pattern: /^(code\s+style|style)$/i, id: "style" },
  { pattern: /^(commands?|development)$/i, id: "commands" },
  { pattern: /^security$/i, id: "security" },
  { pattern: /^(pitfalls?|gotchas?|do not)$/i, id: "pitfalls" },
  { pattern: /^glossary$/i, id: "glossary" },
  { pattern: /^(ownership|code\s*owners?)$/i, id: "ownership" },
  { pattern: /^(tech[-\s]?stack|stack)$/i, id: "stack" },
  {
    pattern: /^(definition of done|done when|acceptance( criteria)?)$/i,
    id: "definition_of_done",
  },
];

export function slugifyHeading(heading: string): string {
  const slug = heading
    .trim()
    .toLowerCase()
    .replace(/['"]/g, "")
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return slug.length > 0 ? slug : "untitled";
}

export function sectionIdForHeading(heading: string): { id: ContextSectionId; key?: string } {
  const normalized = heading.trim().replace(/\s+/g, " ");
  for (const alias of HEADING_ALIASES) {
    if (alias.pattern.test(normalized)) {
      return { id: alias.id };
    }
  }
  return { id: "custom", key: slugifyHeading(normalized) };
}

export function sectionFromHeading(
  heading: string,
  bodyMd: string,
  ordinal: number,
): ContextSection {
  const mapped = sectionIdForHeading(heading);
  const title = heading.trim();
  if (mapped.id === "custom") {
    return {
      id: "custom",
      key: mapped.key ?? slugifyHeading(title),
      title,
      body_md: bodyMd,
      ordinal,
    };
  }
  return {
    id: mapped.id,
    title,
    body_md: bodyMd,
    ordinal,
  };
}
