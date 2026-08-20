import Link from "next/link";

import { githubAuthorizeUrl } from "@/lib/api";
import { githubCallbackUrl, publicAuthConfig } from "@/lib/auth-config";
import { t } from "@/lib/i18n";
import { LOGIN_PATH } from "@/lib/nav";
import { BUTTON_VARIANT_CLASS } from "@/lib/ui";
import { Banner } from "@/lib/ui/banner";
import { PageHeader } from "@/lib/ui/page-header";
import { Panel } from "@/lib/ui/panel";

export const dynamic = "force-dynamic";

export default async function LandingPage() {
  const auth = await publicAuthConfig();
  const githubHref =
    auth.githubEnabled && auth.githubClientId
      ? githubAuthorizeUrl(auth.githubClientId, await githubCallbackUrl())
      : null;

  return (
    <main className="mx-auto flex min-h-full w-full max-w-5xl flex-col gap-10 px-6 py-16">
      <div className="space-y-3">
        <p className="text-sm font-medium tracking-wide text-muted uppercase">{t("common.brand")}</p>
        <PageHeader title={t("landing.title")} description={t("landing.intro")} />
      </div>

      <section className="grid gap-6 md:grid-cols-2">
        <Panel className="flex flex-col gap-4 p-6">
          <div className="space-y-1">
            <h2 className="text-xl font-semibold">{t("landing.hosted")}</h2>
            <p className="text-sm leading-6 text-muted">{t("landing.hostedBody")}</p>
          </div>
          {githubHref ? (
            <a className={`${BUTTON_VARIANT_CLASS.primary} inline-flex h-11 items-center justify-center px-4`} href={githubHref}>
              {t("landing.continueGithub")}
            </a>
          ) : (
            <Banner>{t("landing.githubDisabled")}</Banner>
          )}
        </Panel>

        <Panel className="flex flex-col gap-4 p-6">
          <div className="space-y-1">
            <h2 className="text-xl font-semibold">{t("landing.selfHost")}</h2>
            <p className="text-sm leading-6 text-muted">{t("landing.selfHostBody")}</p>
          </div>
          <div className="flex flex-col gap-2 sm:flex-row">
            <Link
              className={`${BUTTON_VARIANT_CLASS.primary} inline-flex h-11 flex-1 items-center justify-center px-4`}
              href={LOGIN_PATH}
            >
              {t("landing.localLogin")}
            </Link>
            <Link
              className={`${BUTTON_VARIANT_CLASS.secondary} inline-flex h-11 flex-1 items-center justify-center px-4 font-medium`}
              href="/bootstrap"
            >
              {t("landing.bootstrap")}
            </Link>
          </div>
        </Panel>
      </section>
    </main>
  );
}
