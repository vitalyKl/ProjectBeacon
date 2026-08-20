"use client";

import Link from "next/link";
import type { ReactNode } from "react";

import type { PublicLabel } from "@/lib/labels";
import { FIELD_INPUT_CLASS, segmentedItemClass } from "@/lib/ui";
import { PageHeader } from "@/lib/ui/page-header";
import { Segmented } from "@/lib/ui/segmented";
import { useT } from "@/lib/use-locale";

export type WorkSurface = "board" | "backlog" | "roadmap";

export function WorkHeader({
  surface,
  title,
  description,
  catalog,
  labelId,
  onLabelIdChange,
  actions,
}: {
  surface: WorkSurface;
  title: ReactNode;
  description?: ReactNode;
  catalog: PublicLabel[];
  labelId: string;
  onLabelIdChange: (labelId: string) => void;
  actions?: ReactNode;
}) {
  const t = useT();
  const showToggle = surface === "board" || surface === "backlog";

  return (
    <PageHeader
      title={title}
      description={description}
      actions={
        <div className="flex flex-wrap items-center gap-2">
          {showToggle ? (
            <nav aria-label={t("work.views")}>
              <Segmented>
                <Link
                  href="/app/board"
                  className={segmentedItemClass(surface === "board")}
                  aria-current={surface === "board" ? "page" : undefined}
                >
                  {t("nav.board")}
                </Link>
                <Link
                  href="/app/backlog"
                  className={segmentedItemClass(surface === "backlog")}
                  aria-current={surface === "backlog" ? "page" : undefined}
                >
                  {t("nav.backlog")}
                </Link>
              </Segmented>
            </nav>
          ) : null}
          {catalog.length > 0 ? (
            <select
              className={FIELD_INPUT_CLASS}
              aria-label={t("board.filterArea")}
              value={labelId}
              onChange={(event) => onLabelIdChange(event.target.value)}
            >
              <option value="">{t("board.allAreas")}</option>
              {catalog.map((label) => (
                <option key={label.id} value={label.id}>
                  {label.name}
                </option>
              ))}
            </select>
          ) : null}
          {actions}
        </div>
      }
    />
  );
}
