import Link from "next/link";

import { t } from "@/lib/i18n";
import { LOGIN_PATH } from "@/lib/nav";

import { BootstrapForm } from "./bootstrap-form";

export default function BootstrapPage() {
  return (
    <main className="mx-auto flex min-h-full w-full max-w-md flex-col justify-center gap-6 px-6 py-16">
      <div className="space-y-2">
        <p className="text-sm font-medium text-muted">{t("landing.selfHost")}</p>
        <h1 className="text-3xl font-semibold tracking-tight">{t("bootstrap.title")}</h1>
        <p className="text-sm leading-6 text-muted">{t("bootstrap.intro")}</p>
      </div>
      <div className="rounded-2xl border border-border bg-surface p-6 shadow-sm">
        <BootstrapForm />
      </div>
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
