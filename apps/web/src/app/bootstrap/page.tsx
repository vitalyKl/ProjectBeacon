import Link from "next/link";

import { t } from "@/lib/i18n";
import { LOGIN_PATH } from "@/lib/nav";
import { PageHeader } from "@/lib/ui/page-header";
import { Panel } from "@/lib/ui/panel";

import { BootstrapForm } from "./bootstrap-form";

export default function BootstrapPage() {
  return (
    <main className="mx-auto flex min-h-full w-full max-w-md flex-col justify-center gap-6 px-6 py-16">
      <div className="space-y-2">
        <p className="text-sm font-medium text-muted">{t("landing.selfHost")}</p>
        <PageHeader title={t("bootstrap.title")} description={t("bootstrap.intro")} />
      </div>
      <Panel className="p-6">
        <BootstrapForm />
      </Panel>
      <div className="flex flex-col gap-2 text-sm text-muted">
        <Link className="underline" href={LOGIN_PATH}>
          {t("bootstrap.already")}
        </Link>
        <Link className="underline" href="/">
          {t("login.back")}
        </Link>
      </div>
    </main>
  );
}
