import Link from "next/link";

import { t } from "@/lib/i18n";

type Search = {
  reason?: string;
};

export const dynamic = "force-dynamic";

export default async function GithubLoginFailedPage({
  searchParams,
}: {
  searchParams: Promise<Search>;
}) {
  const params = await searchParams;
  const reason = params.reason?.trim() || t("github.failedDefault");

  return (
    <main className="mx-auto flex min-h-full w-full max-w-md flex-col justify-center gap-4 px-6 py-16">
      <h1 className="text-2xl font-semibold">{t("github.failedTitle")}</h1>
      <p className="text-sm text-muted">{reason}</p>
      <Link className="text-sm underline" href="/">
        {t("github.backHome")}
      </Link>
    </main>
  );
}
