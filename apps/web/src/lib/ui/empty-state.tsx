import type { ReactNode } from "react";

import { EMPTY_DESCRIPTION_CLASS, EMPTY_TITLE_CLASS, cx } from "../ui";

export function EmptyState({
  title,
  description,
  action,
  className,
  children,
}: {
  title?: ReactNode;
  description?: ReactNode;
  action?: ReactNode;
  className?: string;
  children?: ReactNode;
}) {
  return (
    <section className={cx("space-y-2", className)}>
      {title ? <h2 className={EMPTY_TITLE_CLASS}>{title}</h2> : null}
      {description ? <p className={EMPTY_DESCRIPTION_CLASS}>{description}</p> : null}
      {action}
      {children}
    </section>
  );
}
