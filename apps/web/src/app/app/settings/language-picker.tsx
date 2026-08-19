"use client";

import { LOCALES, LOCALE_LABELS, isLocale, setLocale } from "@/lib/i18n";
import { useLocale, useT } from "@/lib/use-locale";

export function LanguagePicker() {
  const locale = useLocale();
  const t = useT();

  return (
    <section className="space-y-3">
      <div>
        <h2 className="text-lg font-semibold">{t("settings.language")}</h2>
        <p className="text-sm text-muted">{t("settings.languageHint")}</p>
      </div>
      <label className="flex max-w-xs flex-col gap-1 text-sm">
        {t("common.language")}
        <select
          className="h-9 rounded-md border border-border bg-background px-2"
          value={locale}
          aria-label={t("common.language")}
          onChange={(event) => {
            if (isLocale(event.target.value)) {
              setLocale(event.target.value);
            }
          }}
        >
          {LOCALES.map((item) => (
            <option key={item} value={item}>
              {LOCALE_LABELS[item]}
            </option>
          ))}
        </select>
      </label>
    </section>
  );
}
