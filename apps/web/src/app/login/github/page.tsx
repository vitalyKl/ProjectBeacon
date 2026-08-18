import Link from "next/link";

import { GithubCallback } from "./github-callback";

type Search = {
  code?: string;
  error?: string;
  error_description?: string;
};

export default async function GithubLoginPage({
  searchParams,
}: {
  searchParams: Promise<Search>;
}) {
  const params = await searchParams;
  const code = typeof params.code === "string" ? params.code : "";
  const oauthError = params.error_description ?? params.error;

  if (!code) {
    return (
      <main className="mx-auto flex min-h-full w-full max-w-md flex-col justify-center gap-4 px-6 py-16">
        <h1 className="text-2xl font-semibold">GitHub sign-in</h1>
        <p className="text-sm text-muted">
          {oauthError ?? "Missing authorization code. Start again from the homepage."}
        </p>
        <Link className="text-sm underline" href="/">
          Back home
        </Link>
      </main>
    );
  }

  return <GithubCallback code={code} />;
}
