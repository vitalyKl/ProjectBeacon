import Link from "next/link";

import { githubAuthorizeUrl } from "@/lib/api";
import { githubCallbackUrl, publicAuthConfig } from "@/lib/auth-config";

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
        <p className="text-sm font-medium tracking-wide text-muted uppercase">Beacon</p>
        <h1 className="max-w-2xl text-4xl font-semibold tracking-tight">
          Hosted and self-host are equal paths.
        </h1>
        <p className="max-w-2xl text-base leading-7 text-muted">
          Sign in on this instance. Hosted GitHub and self-host login are equal first steps.
        </p>
      </header>

      <section className="grid gap-6 md:grid-cols-2">
        <article className="flex flex-col gap-4 rounded-2xl border border-border bg-surface p-6 shadow-sm">
          <div className="space-y-1">
            <h2 className="text-xl font-semibold">Hosted</h2>
            <p className="text-sm leading-6 text-muted">
              Continue with GitHub on the hosted control plane. Optional on self-host.
            </p>
          </div>
          {githubHref ? (
            <a
              className="inline-flex h-11 items-center justify-center rounded-lg bg-accent px-4 text-sm font-medium text-accent-fg"
              href={githubHref}
            >
              Continue with GitHub
            </a>
          ) : (
            <p className="rounded-lg border border-dashed border-border px-3 py-2 text-sm text-muted">
              GitHub sign-in is not enabled on this instance. Use self-host login or bootstrap.
            </p>
          )}
        </article>

        <article className="flex flex-col gap-4 rounded-2xl border border-border bg-surface p-6 shadow-sm">
          <div className="space-y-1">
            <h2 className="text-xl font-semibold">Self-host</h2>
            <p className="text-sm leading-6 text-muted">
              Local username and password, or first-user bootstrap with the operator token.
            </p>
          </div>
          <div className="flex flex-col gap-2 sm:flex-row">
            <Link
              className="inline-flex h-11 flex-1 items-center justify-center rounded-lg bg-accent px-4 text-sm font-medium text-accent-fg"
              href="/login"
            >
              Local login
            </Link>
            <Link
              className="inline-flex h-11 flex-1 items-center justify-center rounded-lg border border-border px-4 text-sm font-medium"
              href="/bootstrap"
            >
              First-user bootstrap
            </Link>
          </div>
        </article>
      </section>
    </main>
  );
}
