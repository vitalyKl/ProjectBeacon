"use client";

import { useCallback, useSyncExternalStore } from "react";

import {
  getLocale,
  hydrateLocale,
  subscribeLocale,
  t,
  tf,
  type Locale,
  type MessageKey,
} from "./i18n";

function subscribe(listener: () => void): () => void {
  hydrateLocale();
  return subscribeLocale(listener);
}

function readClientLocale(): Locale {
  return getLocale();
}

function readServerLocale(): Locale {
  return "en";
}

export function useLocale(): Locale {
  return useSyncExternalStore(subscribe, readClientLocale, readServerLocale);
}

export function useT(): (key: MessageKey) => string {
  const locale = useLocale();
  return useCallback((key: MessageKey) => t(key, locale), [locale]);
}

export function useTf(): (key: MessageKey, vars: Record<string, string>) => string {
  const locale = useLocale();
  return useCallback((key: MessageKey, vars: Record<string, string>) => tf(key, vars, locale), [locale]);
}
