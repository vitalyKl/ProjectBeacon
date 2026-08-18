"use client";

import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";

import { ApiError, bootstrapLocal } from "@/lib/api";
import { POST_LOGIN_PATH } from "@/lib/nav";

export function BootstrapForm() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setPending(true);
    const form = new FormData(event.currentTarget);
    const token = String(form.get("token") ?? "").trim();
    const login = String(form.get("login") ?? "").trim();
    const password = String(form.get("password") ?? "");
    try {
      await bootstrapLocal(token, login, password);
      router.replace(POST_LOGIN_PATH);
      router.refresh();
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "bootstrap failed");
    } finally {
      setPending(false);
    }
  }

  return (
    <form className="flex flex-col gap-4" onSubmit={onSubmit}>
      <label className="flex flex-col gap-1 text-sm">
        BOOTSTRAP token
        <input
          className="h-11 rounded-lg border border-border bg-background px-3 font-mono"
          name="token"
          autoComplete="off"
          required
        />
      </label>
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
          autoComplete="new-password"
          required
          minLength={10}
        />
      </label>
      <p className="text-xs leading-5 text-muted">Password must be at least 10 characters.</p>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      <button
        className="h-11 rounded-lg bg-accent text-sm font-medium text-accent-fg disabled:opacity-60"
        type="submit"
        disabled={pending}
      >
        {pending ? "Creating first user…" : "Create first user"}
      </button>
    </form>
  );
}
