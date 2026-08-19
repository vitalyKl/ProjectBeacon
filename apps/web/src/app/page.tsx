import Link from "next/link";

import { githubAuthorizeUrl } from "@/lib/api";
import { githubCallbackUrl, publicAuthConfig } from "@/lib/auth-config";
import { t } from "@/lib/i18n";
import { LOGIN_PATH } from "@/lib/nav";

export const dynamic = "force-dynamic";

export default async function LandingPage() {
  const auth = await publicAuthConfig();
  const githubHref =
    auth.githubEnabled && auth.githubClientId
      ? githubAuthorizeUrl(auth.githubClientId, await githubCallbackUrl())
      : null;

  return (
    <main className="mx-auto flex min-h-full w-full max-w-5xl flex-col gap-10 px-6 py-16">
      <header className="space-y-3">
        <p className="text-sm font-medium tracking-wide text-muted uppercase">{t("common.brand")}</p>
        <h1 className="max-w-2xl text-4xl font-semibold tracking-tight">{t("landing.title")}</h1>
        <p className="max-w-2xl text-base leading-7 text-muted">{t("landing.intro")}</p>
      </header>

      <section className="grid gap-6 md:grid-cols-2">
        <article className="flex flex-col gap-4 rounded-2xl border border-border bg-surface p-6 shadow-sm">
          <div className="space-y-1">
            <h2 className="text-xl font-semibold">{t("landing.hosted")}</h2>
            <p className="text-sm leading-6 text-muted">{t("landing.hostedBody")}</p>
          </div>
          {githubHref ? (
            <a
              className="inline-flex h-11 items-center justify-center rounded-lg bg-accent px-4 text-sm font-medium text-accent-fg"
              href={githubHref}
            >
              {t("landing.continueGithub")}
            </a>
          ) : (
            <p className="rounded-lg border border-dashed border-border px-3 py-2 text-sm text-muted">
              {t("landing.githubDisabled")}
            </p>
          )}
        </article>

        <article className="flex flex-col gap-4 rounded-2xl border border-border bg-surface p-6 shadow-sm">
          <div className="space-y-1">
            <h2 className="text-xl font-semibold">{t("landing.selfHost")}</h2>
            <p className="text-sm leading-6 text-muted">{t("landing.selfHostBody")}</p>
          </div>
          <div className="flex flex-col gap-2 sm:flex-row">
            <Link
              className="inline-flex h-11 flex-1 items-center justify-center rounded-lg bg-accent px-4 text-sm font-medium text-accent-fg"
              href={LOGIN_PATH}
            >
              {t("landing.localLogin")}
            </Link>
            <Link
              className="inline-flex h-11 flex-1 items-center justify-center rounded-lg border border-border px-4 text-sm font-medium"
              href="/bootstrap"
            >
              {t("landing.bootstrap")}
            </Link>
          </div>
        </article>
      </section>
    </main>
  );
}
