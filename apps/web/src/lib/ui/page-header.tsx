import type { ReactNode } from "react";

import { PAGE_DESCRIPTION_CLASS, PAGE_TITLE_CLASS, cx } from "../ui";

export function PageHeader({
  title,
  description,
  actions,
  className,
}: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  className?: string;
}) {
  return (
    <header className={cx("flex flex-wrap items-center justify-between gap-3", className)}>
      <div>
        <h1 className={PAGE_TITLE_CLASS}>{title}</h1>
        {description ? <p className={PAGE_DESCRIPTION_CLASS}>{description}</p> : null}
      </div>
      {actions}
    </header>
  );
}
