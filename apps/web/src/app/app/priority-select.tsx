"use client";

import { prioritySelectOptions } from "@/lib/priority";
import { useT } from "@/lib/use-locale";

export function PrioritySelect({
  value,
  onChange,
  id,
}: {
  value: number;
  onChange: (priority: number) => void;
  id?: string;
}) {
  const t = useT();
  return (
    <select
      id={id}
      className="h-9 rounded-md border border-border bg-background px-2 text-sm"
      aria-label={t("common.priority")}
      value={value}
      onChange={(event) => onChange(Number(event.target.value))}
    >
      {prioritySelectOptions(value).map((option) => (
        <option key={option.value} value={option.value}>
          {option.label}
        </option>
      ))}
    </select>
  );
}
