import Link from "next/link";

import { githubAuthorizeUrl } from "@/lib/api";
import { githubCallbackUrl, publicAuthConfig } from "@/lib/auth-config";

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
        <p className="text-sm font-medium text-muted">Self-host</p>
        <h1 className="text-3xl font-semibold tracking-tight">Local login</h1>
        <p className="text-sm leading-6 text-muted">
          Username and password for this Compose or air-gapped instance.
        </p>
      </div>
      <div className="rounded-2xl border border-border bg-surface p-6 shadow-sm">
        <LoginForm />
      </div>
      <div className="flex flex-col gap-2 text-sm text-muted">
        {githubHref ? (
          <a className="font-medium text-foreground underline" href={githubHref}>
            Continue with GitHub instead
          </a>
        ) : null}
        <Link className="underline" href="/bootstrap">
          First user? Bootstrap this instance
        </Link>
        <Link className="underline" href="/">
          Back to both paths
        </Link>
      </div>
    </main>
  );
}
