import { isTaskLocked, type PublicTask } from "@/lib/roadmap";
import { useT } from "@/lib/use-locale";
import { useNow } from "@/lib/use-now";

export function LockBadge({ task }: { task: PublicTask }) {
  const t = useT();
  const now = useNow(1000);
  if (!isTaskLocked(task, now)) {
    return null;
  }
  return (
    <span
      className="inline-flex items-center rounded-full border border-border bg-background px-2 py-0.5 text-[11px] font-medium tracking-wide uppercase"
      title={t("lock.title")}
    >
      {t("lock.label")}
    </span>
  );
}
