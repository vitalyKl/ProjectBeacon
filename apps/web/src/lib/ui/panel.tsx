import type { HTMLAttributes } from "react";

import { PANEL_CLASS, cx } from "../ui";

export function Panel({ className, ...props }: HTMLAttributes<HTMLElement>) {
  return <section className={cx(PANEL_CLASS, className)} {...props} />;
}
