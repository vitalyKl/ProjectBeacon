"use client";

import Link from "next/link";

import { navMessageForHref } from "@/lib/nav";
import { Banner } from "@/lib/ui/banner";
import { PageHeader } from "@/lib/ui/page-header";
import { Panel } from "@/lib/ui/panel";
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
      <PageHeader title={t("learn.title")} description={t("learn.intro")} />

      <Panel className="space-y-2">
        <h2 className="text-sm font-semibold tracking-wide uppercase">{t("learn.start")}</h2>
        <p className="text-sm leading-6">{t("learn.startBody")}</p>
      </Panel>

      <article className="space-y-3">
        <h2 className="text-sm font-semibold tracking-wide uppercase">{t("learn.screens")}</h2>
        <ol className="space-y-3">
          {SCREENS.map((item) => (
            <li key={item.href}>
              <Panel>
                <Link className="text-sm font-medium underline-offset-2 hover:underline" href={item.href}>
                  {t(navMessageForHref(item.href))}
                </Link>
                <p className="mt-1 text-sm leading-6 text-muted">{t(item.title)}</p>
              </Panel>
            </li>
          ))}
        </ol>
      </article>

      <Panel className="space-y-2">
        <h2 className="text-sm font-semibold tracking-wide uppercase">{t("learn.check")}</h2>
        <p className="text-sm leading-6">{t("learn.checkBody")}</p>
      </Panel>

      <Panel className="space-y-2">
        <h2 className="text-sm font-semibold tracking-wide uppercase">{t("learn.multi")}</h2>
        <p className="text-sm leading-6">{t("learn.multiBody")}</p>
      </Panel>

      <Banner tone="warning" className="space-y-2">
        <h2 className="text-sm font-semibold tracking-wide uppercase">{t("learn.guide")}</h2>
        <ol className="space-y-1 text-sm leading-6">
          <li>{t("learn.guideOne")}</li>
          <li>{t("learn.guideTwo")}</li>
          <li>{t("learn.guideThree")}</li>
          <li>{t("learn.guideFour")}</li>
        </ol>
      </Banner>
    </section>
  );
}
