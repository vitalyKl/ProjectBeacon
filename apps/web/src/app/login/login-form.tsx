"use client";

import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";

import { ApiError, loginLocal } from "@/lib/api";
import { POST_LOGIN_PATH } from "@/lib/nav";

export function LoginForm() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setPending(true);
    const form = new FormData(event.currentTarget);
    const login = String(form.get("login") ?? "").trim();
    const password = String(form.get("password") ?? "");
    try {
      await loginLocal(login, password);
      router.replace(POST_LOGIN_PATH);
      router.refresh();
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "invalid login or password");
    } finally {
      setPending(false);
    }
  }

  return (
    <form className="flex flex-col gap-4" onSubmit={onSubmit}>
      <label className="flex flex-col gap-1 text-sm">
        Username
        <input
          className="h-11 rounded-lg border border-border bg-background px-3"
          name="login"
          autoComplete="username"
          required
          maxLength={64}
        />
      </label>
      <label className="flex flex-col gap-1 text-sm">
        Password
        <input
          className="h-11 rounded-lg border border-border bg-background px-3"
          name="password"
          type="password"
          autoComplete="current-password"
          required
        />
      </label>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      <button
        className="h-11 rounded-lg bg-accent text-sm font-medium text-accent-fg disabled:opacity-60"
        type="submit"
        disabled={pending}
      >
        {pending ? "Signing in…" : "Sign in"}
      </button>
    </form>
  );
}
