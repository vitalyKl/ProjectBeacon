import type { ReactNode } from "react";

import { FIELD_ERROR_CLASS, cx } from "../ui";

export function Field({
  label,
  error,
  className,
  children,
}: {
  label?: ReactNode;
  error?: ReactNode;
  className?: string;
  children: ReactNode;
}) {
  return (
    <label className={cx("flex flex-col gap-1 text-sm", className)}>
      {label}
      {children}
      {error ? <p className={FIELD_ERROR_CLASS}>{error}</p> : null}
    </label>
  );
}
