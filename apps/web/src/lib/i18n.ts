import { be } from "./locales/be";
import { de } from "./locales/de";
import { en, type MessageKey } from "./locales/en";
import { es } from "./locales/es";
import { fr } from "./locales/fr";
import { it } from "./locales/it";
import { ja } from "./locales/ja";
import { kk } from "./locales/kk";
import { ko } from "./locales/ko";
import { lt } from "./locales/lt";
import { lv } from "./locales/lv";
import { pl } from "./locales/pl";
import { pt } from "./locales/pt";
import { ru } from "./locales/ru";
import { uk } from "./locales/uk";
import { zh } from "./locales/zh";

export type { MessageKey };

export const LOCALES = [
  "en",
  "es",
  "uk",
  "fr",
  "de",
  "pt",
  "pl",
  "it",
  "ja",
  "zh",
  "ru",
  "lt",
  "lv",
  "be",
  "kk",
  "ko",
] as const;

export type Locale = (typeof LOCALES)[number];

export const LOCALE_STORAGE_KEY = "beacon.locale";

export const LOCALE_LABELS: Record<Locale, string> = {
  en: "English",
  es: "Español",
  uk: "Українська",
  fr: "Français",
  de: "Deutsch",
  pt: "Português",
  pl: "Polski",
  it: "Italiano",
  ja: "日本語",
  zh: "简体中文",
  ru: "Русский",
  lt: "Lietuvių",
  lv: "Latviešu",
  be: "Беларуская",
  kk: "Қазақша",
  ko: "한국어",
};

type Catalog = Partial<Record<MessageKey, string>>;
const CATALOGS: Record<Locale, Catalog> = {
  en,
  es,
  uk,
  fr,
  de,
  pt,
  pl,
  it,
  ja,
  zh,
  ru,
  lt,
  lv,
  be,
  kk,
  ko,
};

let currentLocale: Locale = "en";
const listeners = new Set<() => void>();

export function isLocale(value: string): value is Locale {
  return (LOCALES as readonly string[]).includes(value);
}

export function getLocale(): Locale {
  return currentLocale;
}

export function setLocale(locale: Locale): void {
  if (currentLocale === locale) {
    return;
  }
  currentLocale = locale;
  if (typeof window !== "undefined") {
    window.localStorage.setItem(LOCALE_STORAGE_KEY, locale);
    document.documentElement.lang = locale;
  }
  for (const listener of listeners) {
    listener();
  }
}

export function hydrateLocale(): Locale {
  if (typeof window === "undefined") {
    return currentLocale;
  }
  const stored = window.localStorage.getItem(LOCALE_STORAGE_KEY);
  if (stored && isLocale(stored)) {
    currentLocale = stored;
    document.documentElement.lang = stored;
    return stored;
  }
  const nav = window.navigator.language.slice(0, 2).toLowerCase();
  if (isLocale(nav)) {
    currentLocale = nav;
    document.documentElement.lang = nav;
  }
  return currentLocale;
}

export function subscribeLocale(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function t(key: MessageKey, locale: Locale = currentLocale): string {
  return CATALOGS[locale][key] ?? CATALOGS.en[key] ?? key;
}

export function tf(
  key: MessageKey,
  vars: Record<string, string>,
  locale: Locale = currentLocale,
): string {
  return Object.entries(vars).reduce(
    (text, [name, value]) => text.replaceAll(`{${name}}`, value),
    t(key, locale),
  );
}

const ACTIVITY_VERB_KEYS: Record<string, MessageKey> = {
  create: "activity.create",
  update: "activity.update",
  delete: "activity.delete",
  status: "activity.status",
  comment: "activity.comment",
  start_work: "activity.start_work",
  finish_work: "activity.finish_work",
  lock_stolen: "activity.lock_stolen",
  lock_released: "activity.lock_released",
  propose: "activity.propose",
  apply: "activity.apply",
  import: "activity.import",
  github_clone_invalidated: "activity.github_clone_invalidated",
};

export function activityVerbLabel(verb: string, locale: Locale = currentLocale): string {
  const key = ACTIVITY_VERB_KEYS[verb];
  return key ? t(key, locale) : verb.replaceAll("_", " ");
}

export function activityLine(
  verb: string,
  objectType: string,
  locale: Locale = currentLocale,
): string {
  return tf("activity.line", { verb: activityVerbLabel(verb, locale), object: objectType }, locale);
}
