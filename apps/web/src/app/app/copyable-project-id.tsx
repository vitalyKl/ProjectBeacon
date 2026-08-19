"use client";

import { useEffect, useState } from "react";

import { useT } from "@/lib/use-locale";

export async function copyProjectId(
  projectId: string,
  clipboard: { writeText(value: string): Promise<void> } | undefined,
): Promise<boolean> {
  if (!clipboard) {
    return false;
  }
  try {
    await clipboard.writeText(projectId);
    return true;
  } catch {
    return false;
  }
}

export function CopyableProjectId({ projectId }: { projectId: string }) {
  const t = useT();
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!copied) {
      return;
    }
    const id = window.setTimeout(() => {
      setCopied(false);
    }, 2000);
    return () => {
      window.clearTimeout(id);
    };
  }, [copied]);

  async function onCopy() {
    const ok = await copyProjectId(projectId, navigator.clipboard);
    setCopied(ok);
  }

  return (
    <div className="flex max-w-xl flex-wrap items-center gap-2 text-sm">
      <span className="text-muted">{t("copy.projectId")}</span>
      <code className="break-all rounded-md bg-background px-2 py-1 font-mono">{projectId}</code>
      <button
        className="rounded-md border border-border px-3 py-1.5"
        type="button"
        onClick={() => void onCopy()}
      >
        {copied ? t("common.copied") : t("common.copy")}
      </button>
    </div>
  );
}
