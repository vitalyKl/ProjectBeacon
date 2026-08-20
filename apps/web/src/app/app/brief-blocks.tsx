import { briefPreviewSectionKey, type BriefPreviewSection } from "@/lib/brief-preview";
import { t } from "@/lib/i18n";

import { MarkdownView } from "./markdown-view";

export type BriefBlockSection = BriefPreviewSection;

export function BriefBlocks({
  sections,
  empty = t("brief.empty"),
}: {
  sections: readonly BriefBlockSection[];
  empty?: string;
}) {
  if (sections.length === 0) {
    return <p className="text-sm text-muted">{empty}</p>;
  }
  return (
    <div className="space-y-4">
      {sections.map((section, index) => (
        <article
          key={briefPreviewSectionKey(section, index)}
          className="space-y-1 rounded-lg border border-border bg-surface p-4"
        >
          <h3 className="text-sm font-semibold tracking-wide uppercase">{section.title}</h3>
          <MarkdownView source={section.body_md} />
        </article>
      ))}
    </div>
  );
}
