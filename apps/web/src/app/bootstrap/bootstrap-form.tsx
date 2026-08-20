"use client";

import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";

import { ApiError, bootstrapLocal } from "@/lib/api";
import { POST_LOGIN_PATH } from "@/lib/nav";
import { FIELD_INPUT_CLASS } from "@/lib/ui";
import { Banner } from "@/lib/ui/banner";
import { Button } from "@/lib/ui/button";
import { Field } from "@/lib/ui/field";
import { useT } from "@/lib/use-locale";

export function BootstrapForm() {
  const t = useT();
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
      setError(caught instanceof ApiError ? caught.message : t("bootstrap.failed"));
    } finally {
      setPending(false);
    }
  }

  return (
    <form className="flex flex-col gap-4" onSubmit={onSubmit}>
      <Field label={t("bootstrap.token")}>
        <input
          className={`${FIELD_INPUT_CLASS} h-11 font-mono`}
          name="token"
          autoComplete="off"
          required
        />
      </Field>
      <Field label={t("login.username")}>
        <input
          className={`${FIELD_INPUT_CLASS} h-11`}
          name="login"
          autoComplete="username"
          required
          maxLength={64}
        />
      </Field>
      <Field label={t("login.password")}>
        <input
          className={`${FIELD_INPUT_CLASS} h-11`}
          name="password"
          type="password"
          autoComplete="new-password"
          required
          minLength={10}
        />
      </Field>
      <p className="text-xs leading-5 text-muted">{t("bootstrap.passwordHint")}</p>
      {error ? <Banner tone="danger">{error}</Banner> : null}
      <Button className="h-11" type="submit" disabled={pending}>
        {pending ? t("bootstrap.creating") : t("bootstrap.create")}
      </Button>
    </form>
  );
}
