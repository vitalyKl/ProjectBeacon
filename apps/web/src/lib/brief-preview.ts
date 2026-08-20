export type BriefPreviewSection = {
  id?: string;
  key?: string;
  title: string;
  body_md: string;
  ordinal?: number;
};

export function briefPreviewSectionKey(section: BriefPreviewSection, index: number): string {
  return `${section.id ?? section.title}:${section.key ?? section.ordinal ?? index}`;
}

export function briefDroppedItems(dropped: readonly string[] | null | undefined): string[] {
  return dropped && dropped.length > 0 ? [...dropped] : [];
}
