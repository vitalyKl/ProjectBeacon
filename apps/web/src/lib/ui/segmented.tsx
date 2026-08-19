import type { ButtonHTMLAttributes, ReactNode } from "react";

import { SEGMENTED_GROUP_CLASS, cx, segmentedItemClass } from "../ui";

export function Segmented({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cx(SEGMENTED_GROUP_CLASS, className)}>{children}</div>;
}

export function SegmentedItem({
  active = false,
  className,
  type = "button",
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { active?: boolean }) {
  return <button className={cx(segmentedItemClass(active), className)} type={type} {...props} />;
}
