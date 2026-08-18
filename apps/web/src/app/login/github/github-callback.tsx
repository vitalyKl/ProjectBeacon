"use client";

import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";

import { ApiError, loginGithub } from "@/lib/api";

export function GithubCallback({ code }: { code: string }) {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    loginGithub(code)
      .then(() => {
        if (!cancelled) {
          router.replace("/app");
          router.refresh();
        }
      })
      .catch((caught: unknown) => {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : "GitHub sign-in failed");
        }
      });
    return () => {
      cancelled = true;
    };
  }, [code, router]);

  return (
    <main className="mx-auto flex min-h-full w-full max-w-md flex-col justify-center gap-4 px-6 py-16">
      <h1 className="text-2xl font-semibold">GitHub sign-in</h1>
      {error ? <p className="text-sm text-red-600">{error}</p> : <p className="text-sm text-muted">Finishing sign-in…</p>}
    </main>
  );
}
