"use client";

import { useCallback, useState } from "react";

import { ContextEditor } from "@/app/app/context/context-editor";
import { FilesView } from "@/app/app/files/files-view";
import type { MessageKey } from "@/lib/i18n";
import { useT } from "@/lib/use-locale";

type TabId = "context" | "files";

const TABS: { id: TabId; message: MessageKey }[] = [
  { id: ("context" as const), message: "nav.context" },
  { id: ("files" as const), message: "nav.files" },
];

export function WorkspaceView() {
  const label = useT();
  const [activeTab, setActiveTab] = useState<TabId>("context");

  const onTabChange = useCallback((tab: TabId) => {
    setActiveTab(tab);
  }, []);

  return (
    <div className="space-y-6">
      <div className="flex gap-2 border-b border-border">
        {TABS.map((tab) => (
          <button
            key={tab.id}
            className={`border-b-2 px-4 py-2 text-sm font-medium transition-colors ${
              activeTab === tab.id
                ? "border-accent text-accent"
                : "border-transparent text-muted hover:text-foreground"
            }`}
            type="button"
            onClick={() => onTabChange(tab.id)}
          >
            {label(tab.message)}
          </button>
        ))}
      </div>
      {activeTab === "context" ? <ContextEditor /> : null}
      {activeTab === "files" ? <FilesView /> : null}
    </div>
  );
}
