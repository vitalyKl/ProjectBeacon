import Link from "next/link";

import { githubAuthorizeUrl } from "@/lib/api";
import { githubCallbackUrl, publicAuthConfig } from "@/lib/auth-config";
import { t } from "@/lib/i18n";
import { PageHeader } from "@/lib/ui/page-header";
import { Panel } from "@/lib/ui/panel";

import { LoginForm } from "./login-form";

export const dynamic = "force-dynamic";

export default async function LoginPage() {
  const auth = await publicAuthConfig();
  const githubHref =
    auth.githubEnabled && auth.githubClientId
      ? githubAuthorizeUrl(auth.githubClientId, await githubCallbackUrl())
      : null;

  return (
    <main className="mx-auto flex min-h-full w-full max-w-md flex-col justify-center gap-6 px-6 py-16">
      <div className="space-y-2">
        <p className="text-sm font-medium text-muted">{t("landing.selfHost")}</p>
        <PageHeader title={t("login.title")} description={t("login.intro")} />
      </div>
      <Panel className="p-6">
        <LoginForm />
      </Panel>
      <div className="flex flex-col gap-2 text-sm text-muted">
        {githubHref ? (
          <a className="font-medium text-foreground underline" href={githubHref}>
            {t("login.githubInstead")}
          </a>
        ) : null}
        <Link className="underline" href="/bootstrap">
          {t("login.firstUser")}
        </Link>
        <Link className="underline" href="/">
          {t("login.back")}
        </Link>
      </div>
    </main>
  );
}
