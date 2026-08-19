import type { ButtonHTMLAttributes } from "react";

import { BUTTON_VARIANT_CLASS, cx, type ButtonVariant } from "../ui";

export function Button({
  variant = "primary",
  className,
  type = "button",
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant }) {
  return <button className={cx(BUTTON_VARIANT_CLASS[variant], className)} type={type} {...props} />;
}
