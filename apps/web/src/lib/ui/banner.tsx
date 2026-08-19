import type { HTMLAttributes } from "react";

import { BANNER_TONE_CLASS, cx, type BannerTone } from "../ui";

export function Banner({
  tone = "neutral",
  className,
  ...props
}: HTMLAttributes<HTMLDivElement> & { tone?: BannerTone }) {
  return <div className={cx(BANNER_TONE_CLASS[tone], className)} role="status" {...props} />;
}
