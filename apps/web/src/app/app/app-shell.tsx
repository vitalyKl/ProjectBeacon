"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useCallback, useEffect, useState, type ReactNode } from "react";

import {
  ApiError,
  fetchMe,
  fetchOrgProjects,
  logoutSession,
  type PublicMe,
  type PublicOrg,
  type PublicProject,
} from "@/lib/api";
import { LOCALES, LOCALE_LABELS, isLocale, setLocale, t } from "@/lib/i18n";
import {
  APP_NAV_VISIBLE,
  LOGIN_PATH,
  NEW_PROJECT_PATH,
  POST_LOGIN_PATH,
  isNavItemActive,
  navGroupMessage,
} from "@/lib/nav";
import { hydrateTheme } from "@/lib/theme";
import { BUTTON_VARIANT_CLASS, FIELD_INPUT_CLASS, cx } from "@/lib/ui";
import { CommandPalette } from "@/lib/ui/command-palette";
import { useLocale, useT } from "@/lib/use-locale";

import { AppSelectionProvider, ProjectProvider } from "./project-context";
import {
  ORG_STORAGE_KEY,
  PROJECT_STORAGE_KEY,
  pickOrg,
  pickProject,
  readStoredId,
  writeStoredId,
} from "./selection";
import { ToastProvider } from "./toast";

export function AppShell({ children, banner }: { children: ReactNode; banner?: ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const locale = useLocale();
  const label = useT();
  const [me, setMe] = useState<PublicMe | null>(null);
  const [org, setOrg] = useState<PublicOrg | null>(null);
  const [projects, setProjects] = useState<PublicProject[]>([]);
  const [project, setProject] = useState<PublicProject | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const loadProjects = useCallback(async (nextOrg: PublicOrg) => {
    const items = await fetchOrgProjects(nextOrg.id);
    setProjects(items);
    const nextProject = pickProject(items, readStoredId(PROJECT_STORAGE_KEY));
    setProject(nextProject);
    writeStoredId(PROJECT_STORAGE_KEY, nextProject?.id ?? null);
  }, []);

  useEffect(() => {
    hydrateTheme();
  }, []);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const session = await fetchMe();
        if (cancelled) {
          return;
        }
        if (!session) {
          router.replace(LOGIN_PATH);
          return;
        }
        setMe(session);
        const nextOrg = pickOrg(session, readStoredId(ORG_STORAGE_KEY));
        setOrg(nextOrg);
        writeStoredId(ORG_STORAGE_KEY, nextOrg?.id ?? null);
        if (nextOrg) {
          await loadProjects(nextOrg);
        }
      } catch (caught) {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : t("common.failedSession"));
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [loadProjects, router]);

  async function onOrgChange(orgId: string) {
    const next = me?.orgs.find((item) => item.id === orgId);
    if (!next) {
      return;
    }
    setOrg(next);
    writeStoredId(ORG_STORAGE_KEY, next.id);
    writeStoredId(PROJECT_STORAGE_KEY, null);
    setProject(null);
    setError(null);
    try {
      await loadProjects(next);
    } catch (caught) {
      setProjects([]);
      setError(caught instanceof ApiError ? caught.message : t("common.failedProjects"));
    }
  }

  function onProjectChange(projectId: string) {
    const next = projects.find((item) => item.id === projectId) ?? null;
    setProject(next);
    writeStoredId(PROJECT_STORAGE_KEY, next?.id ?? null);
  }

  function replaceProject(next: PublicProject) {
    setProject(next);
    setProjects((current) => current.map((item) => (item.id === next.id ? next : item)));
    writeStoredId(PROJECT_STORAGE_KEY, next.id);
  }

  async function onLogout() {
    try {
      await logoutSession();
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("common.logoutFailed"));
      return;
    }
    writeStoredId(ORG_STORAGE_KEY, null);
    writeStoredId(PROJECT_STORAGE_KEY, null);
    router.replace("/");
    router.refresh();
  }

  if (loading) {
    return (
      <div className="flex min-h-full items-center justify-center text-sm text-muted">
        {label("common.loading")}
      </div>
    );
  }

  if (!me) {
    return null;
  }

  return (
    <ToastProvider>
      <AppSelectionProvider value={{ me, org, project, projects, replaceProject }}>
        <ProjectProvider
          value={{
            org,
            project,
            projects,
            loading,
            setProjectId: onProjectChange,
            reloadProjects: async () => {
              if (org) {
                await loadProjects(org);
              }
            },
          }}
        >
          <div className="flex min-h-full flex-col">
            <a
              className="sr-only focus:not-sr-only focus:absolute focus:z-50 focus:m-3 focus:rounded-md focus:bg-surface focus:px-3 focus:py-2"
              href="#main"
            >
              {label("a11y.skipToMain")}
            </a>
            <header className="flex flex-wrap items-center gap-3 border-b border-border bg-surface px-4 py-3">
              <Link className="font-semibold tracking-tight" href={POST_LOGIN_PATH}>
                {label("common.brand")}
              </Link>
              <select
                className={cx(FIELD_INPUT_CLASS, "min-w-40")}
                aria-label={label("common.org")}
                value={org?.id ?? ""}
                onChange={(event) => void onOrgChange(event.target.value)}
              >
                {me.orgs.length === 0 ? <option value="">{label("common.org")}</option> : null}
                {me.orgs.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.name}
                  </option>
                ))}
              </select>
              <select
                className={cx(FIELD_INPUT_CLASS, "min-w-40")}
                aria-label={label("common.project")}
                value={project?.id ?? ""}
                onChange={(event) => onProjectChange(event.target.value)}
                disabled={!org}
              >
                {projects.length === 0 ? <option value="">{label("common.project")}</option> : null}
                {projects.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.name}
                  </option>
                ))}
              </select>
              <div className="ml-auto flex flex-wrap items-center gap-3 text-sm">
                <CommandPalette hasProject={project !== null} />
                <select
                  className={FIELD_INPUT_CLASS}
                  aria-label={label("common.language")}
                  value={locale}
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
                <Link className={BUTTON_VARIANT_CLASS.secondary} href={NEW_PROJECT_PATH}>
                  {label("wizard.newProject")}
                </Link>
                <span className="text-muted">{me.login}</span>
                <button
                  className={BUTTON_VARIANT_CLASS.secondary}
                  type="button"
                  onClick={() => void onLogout()}
                >
                  {label("common.logout")}
                </button>
              </div>
            </header>
            {banner}
            <nav className="overflow-x-auto border-b border-border bg-surface px-3 py-2 md:hidden">
              <ul className="flex w-max gap-1">
                {APP_NAV_VISIBLE.map((item) => {
                  const active = isNavItemActive(pathname, item.href);
                  return (
                    <li key={item.href}>
                      <Link
                        className={`block whitespace-nowrap rounded-md px-3 py-2 text-sm ${
                          active ? "bg-background font-medium" : "text-muted hover:text-foreground"
                        }`}
                        href={item.href}
                      >
                        {label(item.message)}
                      </Link>
                    </li>
                  );
                })}
              </ul>
            </nav>
            <div className="flex min-h-0 flex-1">
              <nav className="hidden w-52 shrink-0 border-r border-border bg-surface px-3 py-4 md:block">
                <ul className="flex flex-col gap-1">
                  {APP_NAV_VISIBLE.map((item, index) => {
                    const previous = APP_NAV_VISIBLE[index - 1]?.group;
                    const groupLabel = navGroupMessage(item.group, previous);
                    const active = isNavItemActive(pathname, item.href);
                    return (
                      <li key={item.href}>
                        {groupLabel ? (
                          <p className="px-3 pt-3 pb-1 text-xs font-semibold tracking-wide text-muted uppercase">
                            {label(groupLabel)}
                          </p>
                        ) : null}
                        <Link
                          className={`block rounded-md px-3 py-2 text-sm ${
                            active
                              ? "bg-background font-medium"
                              : "text-muted hover:text-foreground"
                          }`}
                          href={item.href}
                        >
                          {label(item.message)}
                        </Link>
                      </li>
                    );
                  })}
                </ul>
              </nav>
              <main id="main" className="min-w-0 flex-1 px-6 py-6">
                {error ? <p className="mb-4 text-sm text-red-600">{error}</p> : null}
                {children}
              </main>
            </div>
          </div>
        </ProjectProvider>
      </AppSelectionProvider>
    </ToastProvider>
  );
}
