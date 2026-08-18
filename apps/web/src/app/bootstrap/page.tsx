import Link from "next/link";

import { LOGIN_PATH } from "@/lib/nav";

import { BootstrapForm } from "./bootstrap-form";

export default function BootstrapPage() {
  return (
    <main className="mx-auto flex min-h-full w-full max-w-md flex-col justify-center gap-6 px-6 py-16">
      <div className="space-y-2">
        <p className="text-sm font-medium text-muted">Self-host</p>
        <h1 className="text-3xl font-semibold tracking-tight">First-user bootstrap</h1>
        <p className="text-sm leading-6 text-muted">
          Create the first user with the operator bootstrap token. This works once; after that, use
          local login.
        </p>
      </div>
      <div className="rounded-2xl border border-border bg-surface p-6 shadow-sm">
        <BootstrapForm />
      </div>
      <div className="flex flex-col gap-2 text-sm text-muted">
        <Link className="underline" href={LOGIN_PATH}>
          Already bootstrapped? Local login
        </Link>
        <Link className="underline" href="/">
          Back to both paths
        </Link>
      </div>
    </main>
  );
}
