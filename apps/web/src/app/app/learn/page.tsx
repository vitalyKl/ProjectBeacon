"use client";

import Link from "next/link";

import { navMessageForHref } from "@/lib/nav";
import { useT } from "@/lib/use-locale";

const SCREENS = [
  { href: "/app", title: "learn.home" },
  { href: "/app/board", title: "learn.board" },
  { href: "/app/backlog", title: "learn.backlog" },
  { href: "/app/roadmap", title: "learn.roadmap" },
  { href: "/app/context", title: "learn.context" },
  { href: "/app/files", title: "learn.files" },
  { href: "/app/agents", title: "learn.agents" },
  { href: "/app/decisions", title: "learn.decisions" },
  { href: "/app/reports", title: "learn.reports" },
  { href: "/app/settings", title: "learn.settings" },
] as const;

export default function LearnPage() {
  const t = useT();
  return (
    <section className="mx-auto max-w-3xl space-y-8">
      <header className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">{t("learn.title")}</h1>
        <p className="text-sm leading-6 text-muted">{t("learn.intro")}</p>
      </header>

      <article className="space-y-2 rounded-lg border border-border bg-surface p-4">
        <h2 className="text-sm font-semibold tracking-wide uppercase">{t("learn.start")}</h2>
        <p className="text-sm leading-6">{t("learn.startBody")}</p>
      </article>

      <article className="space-y-3">
        <h2 className="text-sm font-semibold tracking-wide uppercase">{t("learn.screens")}</h2>
        <ol className="space-y-3">
          {SCREENS.map((item) => (
            <li key={item.href} className="rounded-lg border border-border bg-surface p-4">
              <Link className="text-sm font-medium underline-offset-2 hover:underline" href={item.href}>
                {t(navMessageForHref(item.href))}
              </Link>
              <p className="mt-1 text-sm leading-6 text-muted">{t(item.title)}</p>
            </li>
          ))}
        </ol>
      </article>

      <article className="space-y-2 rounded-lg border border-border bg-surface p-4">
        <h2 className="text-sm font-semibold tracking-wide uppercase">{t("learn.check")}</h2>
        <p className="text-sm leading-6">{t("learn.checkBody")}</p>
      </article>

      <article className="space-y-2 rounded-lg border border-border bg-surface p-4">
        <h2 className="text-sm font-semibold tracking-wide uppercase">{t("learn.multi")}</h2>
        <p className="text-sm leading-6">{t("learn.multiBody")}</p>
      </article>

      <article className="space-y-2 rounded-lg border border-amber-200 bg-amber-50 p-4 dark:border-amber-900 dark:bg-amber-950/40">
        <h2 className="text-sm font-semibold tracking-wide uppercase">{t("learn.guide")}</h2>
        <ol className="space-y-1 text-sm leading-6">
          <li>{t("learn.guideOne")}</li>
          <li>{t("learn.guideTwo")}</li>
          <li>{t("learn.guideThree")}</li>
          <li>{t("learn.guideFour")}</li>
        </ol>
      </article>
    </section>
  );
}
