"use client";

import Link from "next/link";
import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";

import {
  ApiError,
  CODE_READ_SCOPE,
  DEFAULT_TOKEN_SCOPES,
  createProjectToken,
  fetchProjectActivity,
  fetchProjectSessions,
  fetchProjectTokens,
  revokeProjectToken,
  type PublicActivityEvent,
  type PublicAgentSession,
  type PublicApiToken,
  type PublicProject,
  type PublicRepo,
  type TokenTtl,
} from "@/lib/api";
import { offeredTasks } from "@/lib/brief";
import {
  connectionLabel,
  fetchDetailedProjectRepos,
  formatIndexWhen,
  indexModeLabel,
  repoDisplayName,
} from "@/lib/index-status";
import { activityLine, t } from "@/lib/i18n";
import { LIVE_POLL_MS } from "@/lib/poll";
import { DEFAULT_TASK_PRIORITY, priorityLabel } from "@/lib/priority";
import { fetchProjectTasks, statusLabel, type PublicTask } from "@/lib/roadmap";
import { useT, useTf } from "@/lib/use-locale";

import { CopyableProjectId } from "../copyable-project-id";

const TOKEN_TTLS: { value: TokenTtl; labelKey: "agents.ttl7d" | "agents.ttl90d" | "agents.ttl1y" | "agents.ttlNone" }[] = [
  { value: "7d", labelKey: "agents.ttl7d" },
  { value: "90d", labelKey: "agents.ttl90d" },
  { value: "1y", labelKey: "agents.ttl1y" },
  { value: "none", labelKey: "agents.ttlNone" },
];

function activityLabel(event: PublicActivityEvent): string {
  return activityLine(event.verb, event.object_type);
}

export function AgentsView({ project }: { project: PublicProject | null }) {
  const label = useT();
  const format = useTf();
  const [sessions, setSessions] = useState<PublicAgentSession[]>([]);
  const [activity, setActivity] = useState<PublicActivityEvent[]>([]);
  const [tokens, setTokens] = useState<PublicApiToken[]>([]);
  const [tasks, setTasks] = useState<PublicTask[]>([]);
  const [repos, setRepos] = useState<PublicRepo[] | null>([]);
  const [error, setError] = useState<string | null>(null);
  const [tokenError, setTokenError] = useState<string | null>(null);
  const [loading, setLoading] = useState(Boolean(project));
  const [creating, setCreating] = useState(false);
  const [showCreate, setShowCreate] = useState(false);
  const [tokenName, setTokenName] = useState("");
  const [ttl, setTtl] = useState<TokenTtl>("90d");
  const [allowCode, setAllowCode] = useState(false);
  const [confirmNone, setConfirmNone] = useState(false);
  const [revealed, setRevealed] = useState<PublicApiToken | null>(null);
  const [copied, setCopied] = useState(false);
  const [canManageTokens, setCanManageTokens] = useState(true);

  const loadLive = useCallback(async (projectId: string) => {
    const [nextSessions, nextActivity, nextRepos, nextTasks] = await Promise.all([
      fetchProjectSessions(projectId),
      fetchProjectActivity(projectId),
      fetchDetailedProjectRepos(projectId),
      fetchProjectTasks(projectId),
    ]);
    return {
      sessions: nextSessions,
      activity: nextActivity,
      repos: nextRepos,
      tasks: nextTasks,
    };
  }, []);

  const loadAll = useCallback(
    async (projectId: string) => {
      const [live, nextTokens] = await Promise.all([
        loadLive(projectId),
        fetchProjectTokens(projectId)
          .then((tokens) => ({ tokens, canManage: true }))
          .catch((caught: unknown) => {
            if (caught instanceof ApiError && (caught.status === 403 || caught.status === 404)) {
              return { tokens: [] as PublicApiToken[], canManage: false };
            }
            throw caught;
          }),
      ]);
      return { ...live, tokens: nextTokens.tokens, canManageTokens: nextTokens.canManage };
    },
    [loadLive],
  );

  useEffect(() => {
    if (!project) {
      return;
    }
    let cancelled = false;
    void loadAll(project.id)
      .then((next) => {
        if (cancelled) {
          return;
        }
        setSessions(next.sessions);
        setActivity(next.activity);
        setTokens(next.tokens);
        setRepos(next.repos);
        setTasks(next.tasks);
        setCanManageTokens(next.canManageTokens);
      })
      .catch((caught: unknown) => {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : t("agents.failedLoad"));
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });
    return () => {
      cancelled = true;
    };
  }, [loadAll, project]);

  useEffect(() => {
    if (!project) {
      return;
    }
    const id = window.setInterval(() => {
      void loadLive(project.id)
        .then((next) => {
          setSessions(next.sessions);
          setActivity(next.activity);
          setRepos(next.repos);
          setTasks(next.tasks);
        })
        .catch((caught: unknown) => {
          setError(caught instanceof ApiError ? caught.message : t("agents.failedRefresh"));
        });
    }, LIVE_POLL_MS);
    return () => {
      window.clearInterval(id);
    };
  }, [loadLive, project]);

  const activeSessions = useMemo(
    () => sessions.filter((session) => session.status === "active"),
    [sessions],
  );
  const offered = useMemo(() => offeredTasks(tasks), [tasks]);
  const listedTokens = useMemo(() => tokens.filter((token) => !token.revoked_at), [tokens]);

  function resetCreateForm() {
    setTokenName("");
    setTtl("90d");
    setAllowCode(false);
    setConfirmNone(false);
    setTokenError(null);
  }

  function openCreate() {
    resetCreateForm();
    setRevealed(null);
    setCopied(false);
    setShowCreate(true);
  }

  async function onCreateToken(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!project) {
      return;
    }
    const name = tokenName.trim();
    if (!name) {
      setTokenError(t("common.nameRequired"));
      return;
    }
    if (ttl === "none" && !confirmNone) {
      setTokenError(t("agents.confirmExpiry"));
      return;
    }
    setCreating(true);
    setTokenError(null);
    const scopes = allowCode
      ? [...DEFAULT_TOKEN_SCOPES, CODE_READ_SCOPE]
      : [...DEFAULT_TOKEN_SCOPES];
    try {
      const created = await createProjectToken(project.id, { name, scopes, ttl });
      setTokens((current) => [created, ...current.filter((item) => item.id !== created.id)]);
      setRevealed(created);
      setShowCreate(false);
      resetCreateForm();
    } catch (caught) {
      setTokenError(caught instanceof ApiError ? caught.message : t("agents.failedCreate"));
    } finally {
      setCreating(false);
    }
  }

  async function onRevoke(token: PublicApiToken) {
    try {
      const revoked = await revokeProjectToken(token.id);
      setTokens((current) => current.map((item) => (item.id === revoked.id ? revoked : item)));
      if (revealed?.id === token.id) {
        setRevealed(null);
      }
    } catch (caught) {
      setTokenError(caught instanceof ApiError ? caught.message : t("agents.failedRevoke"));
    }
  }

  async function onCopySecret() {
    if (!revealed?.token) {
      return;
    }
    try {
      await navigator.clipboard.writeText(revealed.token);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  if (!project) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">{label("nav.agents")}</h1>
        <p className="text-sm text-muted">{label("agents.selectProject")}</p>
      </section>
    );
  }

  return (
    <section className="space-y-8">
      <div className="space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight">{label("nav.agents")}</h1>
        <p className="text-sm text-muted">{label("agents.intro")}</p>
        <CopyableProjectId projectId={project.id} />
      </div>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {loading ? <p className="text-sm text-muted">{label("common.loading")}</p> : null}

      <section className="space-y-3">
        <h2 className="text-lg font-semibold">{label("index.title")}</h2>
        {repos === null ? (
          <p className="text-sm text-muted">{label("agents.noRepos")}</p>
        ) : repos.length === 0 ? (
          <p className="text-sm text-muted">{label("agents.noRepos")}</p>
        ) : (
          <ul className="space-y-2">
            {repos.map((repo) => (
              <li
                key={repo.id}
                className="rounded-lg border border-border bg-surface px-4 py-3 text-sm"
              >
                <div className="font-medium">{repoDisplayName(repo)}</div>
                <dl className="mt-2 grid gap-1 text-muted sm:grid-cols-2">
                  <div>{format("agents.indexLine", { value: indexModeLabel(repo.index_mode) })}</div>
                  <div>{format("agents.sidecarLine", { value: connectionLabel(repo.sidecar_connected) })}</div>
                  <div>
                    {format("agents.workerLine", { value: connectionLabel(repo.worker_index_connected) })}
                  </div>
                  <div>
                    {format("agents.lastIndexedLine", { value: formatIndexWhen(repo.last_indexed_at) })}
                  </div>
                </dl>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="space-y-3">
        <h2 className="text-lg font-semibold">{label("agents.ready")}</h2>
        {offered.length === 0 ? (
          <p className="text-sm text-muted">{label("agents.noReady")}</p>
        ) : (
          <ul className="space-y-2">
            {offered.map((task) => (
              <li
                key={task.id}
                className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border bg-surface px-4 py-3 text-sm"
              >
                <div>
                  <Link className="font-medium underline" href={`/app/tasks/${task.id}`}>
                    {task.title}
                  </Link>
                  <p className="text-muted capitalize">
                    {statusLabel(task.status)}
                    {task.priority !== DEFAULT_TASK_PRIORITY
                      ? ` · ${priorityLabel(task.priority)}`
                      : ""}
                  </p>
                </div>
                <span className="text-xs text-muted">{label("agents.offered")}</span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="space-y-3">
        <h2 className="text-lg font-semibold">{label("agents.sessions")}</h2>
        {activeSessions.length === 0 ? (
          <p className="text-sm text-muted">{label("agents.noSessions")}</p>
        ) : (
          <ul className="space-y-2">
            {activeSessions.map((session) => (
              <li
                key={session.id}
                className="rounded-lg border border-border bg-surface px-4 py-3 text-sm"
              >
                <div className="font-medium">{session.agent.name}</div>
                <p className="text-muted">
                  {format("agents.lastSeen", {
                    host: session.agent.host,
                    when: formatIndexWhen(session.last_heartbeat_at),
                  })}
                </p>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="space-y-3">
        <h2 className="text-lg font-semibold">{label("agents.activity")}</h2>
        {activity.length === 0 ? (
          <p className="text-sm text-muted">{label("agents.noActivity")}</p>
        ) : (
          <ul className="space-y-2">
            {activity.slice(0, 40).map((event) => (
              <li
                key={event.id}
                className="rounded-lg border border-border bg-surface px-4 py-3 text-sm"
              >
                <div className="font-medium">{activityLabel(event)}</div>
                <p className="text-muted">{formatIndexWhen(event.created_at)}</p>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-lg font-semibold">{label("agents.tokens")}</h2>
          {canManageTokens ? (
            <button
              className="rounded-md border border-border px-3 py-1.5 text-sm"
              type="button"
              onClick={openCreate}
            >
              {label("agents.newToken")}
            </button>
          ) : null}
        </div>
        {tokenError ? <p className="text-sm text-red-600">{tokenError}</p> : null}
        {revealed?.token ? (
          <div className="space-y-2 rounded-lg border border-border bg-surface px-4 py-3">
            <p className="text-sm font-medium">{label("agents.copyNow")}</p>
            <code className="block break-all rounded-md bg-background px-3 py-2 text-sm">
              {revealed.token}
            </code>
            <button
              className="rounded-md border border-border px-3 py-1.5 text-sm"
              type="button"
              onClick={() => void onCopySecret()}
            >
              {copied ? label("common.copied") : label("agents.copyToken")}
            </button>
          </div>
        ) : null}
        {showCreate ? (
          <form
            className="space-y-3 rounded-lg border border-border bg-surface p-4"
            onSubmit={onCreateToken}
          >
            <label className="flex flex-col gap-1 text-sm">
              {label("common.name")}
              <input
                className="h-9 rounded-md border border-border bg-background px-3"
                value={tokenName}
                onChange={(event) => setTokenName(event.target.value)}
                maxLength={120}
                required
              />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              {label("agents.expires")}
              <select
                className="h-9 rounded-md border border-border bg-background px-2"
                value={ttl}
                onChange={(event) => setTtl(event.target.value as TokenTtl)}
              >
                {TOKEN_TTLS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {label(option.labelKey)}
                  </option>
                ))}
              </select>
            </label>
            {ttl === "none" ? (
              <label className="flex items-start gap-2 text-sm">
                <input
                  className="mt-1"
                  type="checkbox"
                  checked={confirmNone}
                  onChange={(event) => setConfirmNone(event.target.checked)}
                />
                {label("agents.confirmNever")}
              </label>
            ) : null}
            <label className="flex items-start gap-2 text-sm">
              <input
                className="mt-1"
                type="checkbox"
                checked={allowCode}
                onChange={(event) => setAllowCode(event.target.checked)}
              />
              {label("agents.allowCode")}
            </label>
            <div className="flex gap-2">
              <button
                className="h-9 rounded-md bg-accent px-3 text-sm font-medium text-accent-fg disabled:opacity-60"
                type="submit"
                disabled={creating}
              >
                {creating ? label("common.creating") : label("agents.createToken")}
              </button>
              <button
                className="h-9 rounded-md border border-border px-3 text-sm"
                type="button"
                onClick={() => {
                  setShowCreate(false);
                  resetCreateForm();
                }}
              >
                {label("common.cancel")}
              </button>
            </div>
          </form>
        ) : null}
        {!canManageTokens ? (
          <p className="text-sm text-muted">{label("agents.adminsOnly")}</p>
        ) : listedTokens.length === 0 ? (
          <p className="text-sm text-muted">{label("agents.noTokens")}</p>
        ) : (
          <ul className="space-y-2">
            {listedTokens.map((token) => (
              <li
                key={token.id}
                className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border bg-surface px-4 py-3 text-sm"
              >
                <div>
                  <div className="font-medium">{token.name}</div>
                  <p className="text-muted">
                    {format("agents.tokenMeta", {
                      prefix: token.prefix,
                      code: token.scopes.includes(CODE_READ_SCOPE)
                        ? label("agents.codeOn")
                        : label("agents.codeOff"),
                      when: formatIndexWhen(token.expires_at),
                    })}
                  </p>
                </div>
                <button
                  className="rounded-md border border-border px-3 py-1.5"
                  type="button"
                  onClick={() => void onRevoke(token)}
                >
                  {label("agents.revoke")}
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>
    </section>
  );
}
