"use client";

import type { ContextNode } from "@/lib/api";
import { type CreateScope, scopeLabel, sourceLabel } from "@/lib/context-sections";
import { useT } from "@/lib/use-locale";

export function NodesList({
  nodes,
  selectedId,
  isCreate,
  createScope,
  onBeginCreate,
  onSelect,
}: {
  nodes: ContextNode[];
  selectedId: string | null;
  isCreate: boolean;
  createScope: CreateScope;
  onBeginCreate: () => void;
  onSelect: (nodeId: string) => void;
}) {
  const label = useT();
  return (
    <aside className="flex flex-col rounded-lg border border-border bg-surface">
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <h2 className="text-sm font-medium">{label("context.nodes")}</h2>
        <button
          className="text-sm text-muted hover:text-foreground"
          type="button"
          onClick={onBeginCreate}
        >
          {label("context.newBrief")}
        </button>
      </div>
      {nodes.length === 0 && !isCreate ? (
        <p className="px-3 py-3 text-sm leading-6 text-muted">{label("context.empty")}</p>
      ) : (
        <ul className="flex flex-col">
          {isCreate ? (
            <li>
              <div className="flex w-full flex-col items-start gap-1 bg-background px-3 py-2 text-left text-sm">
                <span className="font-medium">{label("context.newBrief")}</span>
                <span className="text-xs text-muted">{createScope}</span>
              </div>
            </li>
          ) : null}
          {nodes.map((node) => {
            const active = !isCreate && node.id === selectedId;
            return (
              <li key={node.id}>
                <button
                  className={`flex w-full flex-col items-start gap-1 px-3 py-2 text-left text-sm ${
                    active ? "bg-background" : "hover:bg-background/60"
                  }`}
                  type="button"
                  onClick={() => onSelect(node.id)}
                >
                  <span className="font-medium">{scopeLabel(node)}</span>
                  <span className="text-xs text-muted">{sourceLabel(node.source)}</span>
                  {node.review_state === "needs_review" ? (
                    <span className="rounded-full border border-border px-2 py-0.5 text-xs">
                      {label("context.importedReview")}
                    </span>
                  ) : null}
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </aside>
  );
}
