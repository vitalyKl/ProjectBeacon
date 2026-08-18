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
import { APP_NAV, LOGIN_PATH, POST_LOGIN_PATH } from "@/lib/nav";

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
          setError(caught instanceof ApiError ? caught.message : "failed to load session");
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
      setError(caught instanceof ApiError ? caught.message : "failed to load projects");
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
      setError(caught instanceof ApiError ? caught.message : "logout failed");
      return;
    }
    writeStoredId(ORG_STORAGE_KEY, null);
    writeStoredId(PROJECT_STORAGE_KEY, null);
    router.replace("/");
    router.refresh();
  }

  if (loading) {
    return (
      <div className="flex min-h-full items-center justify-center text-sm text-muted">Loading…</div>
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
            <header className="flex flex-wrap items-center gap-3 border-b border-border bg-surface px-4 py-3">
              <Link className="font-semibold tracking-tight" href={POST_LOGIN_PATH}>
                Beacon
              </Link>
              <select
                className="h-9 min-w-40 rounded-md border border-border bg-background px-2 text-sm"
                aria-label="Org"
                value={org?.id ?? ""}
                onChange={(event) => void onOrgChange(event.target.value)}
              >
                {me.orgs.length === 0 ? <option value="">Org</option> : null}
                {me.orgs.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.name}
                  </option>
                ))}
              </select>
              <select
                className="h-9 min-w-40 rounded-md border border-border bg-background px-2 text-sm"
                aria-label="Project"
                value={project?.id ?? ""}
                onChange={(event) => onProjectChange(event.target.value)}
                disabled={!org}
              >
                {projects.length === 0 ? <option value="">Project</option> : null}
                {projects.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.name}
                  </option>
                ))}
              </select>
              <div className="ml-auto flex items-center gap-3 text-sm">
                <span className="text-muted">{me.login}</span>
                <button
                  className="rounded-md border border-border px-3 py-1.5"
                  type="button"
                  onClick={() => void onLogout()}
                >
                  Log out
                </button>
              </div>
            </header>
            {banner}
            <div className="flex min-h-0 flex-1">
              <nav className="w-52 shrink-0 border-r border-border bg-surface px-3 py-4">
                <ul className="flex flex-col gap-1">
                  {APP_NAV.map((item) => {
                    const active = pathname === item.href;
                    return (
                      <li key={item.href}>
                        <Link
                          className={`block rounded-md px-3 py-2 text-sm ${
                            active
                              ? "bg-background font-medium"
                              : "text-muted hover:text-foreground"
                          }`}
                          href={item.href}
                        >
                          {item.label}
                        </Link>
                      </li>
                    );
                  })}
                </ul>
              </nav>
              <main className="min-w-0 flex-1 px-6 py-6">
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
