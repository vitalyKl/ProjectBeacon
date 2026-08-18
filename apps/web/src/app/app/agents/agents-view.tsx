"use client";

import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";

import {
  ApiError,
  CODE_READ_SCOPE,
  DEFAULT_TOKEN_SCOPES,
  createProjectToken,
  fetchProjectActivity,
  fetchProjectRepos,
  fetchProjectSessions,
  fetchProjectTokens,
  fetchRepo,
  revokeProjectToken,
  type PublicActivityEvent,
  type PublicAgentSession,
  type PublicApiToken,
  type PublicProject,
  type PublicRepo,
  type TokenTtl,
} from "@/lib/api";
import { LIVE_POLL_MS } from "@/lib/poll";

const TOKEN_TTLS: { value: TokenTtl; label: string }[] = [
  { value: "7d", label: "7 days" },
  { value: "90d", label: "90 days" },
  { value: "1y", label: "1 year" },
  { value: "none", label: "No expiry" },
];

function formatWhen(value: string | null | undefined): string {
  if (!value) {
    return "never";
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }
  return date.toLocaleString();
}

function activityLabel(event: PublicActivityEvent): string {
  const verb = event.verb.replaceAll("_", " ");
  return `${verb} · ${event.object_type}`;
}

export function AgentsView({ project }: { project: PublicProject | null }) {
  const [sessions, setSessions] = useState<PublicAgentSession[]>([]);
  const [activity, setActivity] = useState<PublicActivityEvent[]>([]);
  const [tokens, setTokens] = useState<PublicApiToken[]>([]);
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
    const [nextSessions, nextActivity, nextRepos] = await Promise.all([
      fetchProjectSessions(projectId),
      fetchProjectActivity(projectId),
      fetchProjectRepos(projectId),
    ]);
    let detailed = nextRepos;
    if (nextRepos) {
      detailed = await Promise.all(
        nextRepos.map(async (repo) => {
          const live = await fetchRepo(repo.id);
          return live ?? repo;
        }),
      );
    }
    return { sessions: nextSessions, activity: nextActivity, repos: detailed };
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
        setCanManageTokens(next.canManageTokens);
      })
      .catch((caught: unknown) => {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : "failed to load agents");
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
        })
        .catch((caught: unknown) => {
          setError(caught instanceof ApiError ? caught.message : "failed to refresh agents");
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
      setTokenError("name is required");
      return;
    }
    if (ttl === "none" && !confirmNone) {
      setTokenError("Confirm that this token should never expire.");
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
      setTokenError(caught instanceof ApiError ? caught.message : "failed to create token");
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
      setTokenError(caught instanceof ApiError ? caught.message : "failed to revoke token");
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
        <h1 className="text-2xl font-semibold tracking-tight">Agents</h1>
        <p className="text-sm text-muted">
          Select a project to see sessions, activity, and tokens.
        </p>
      </section>
    );
  }

  return (
    <section className="space-y-8">
      <div className="space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight">Agents</h1>
        <p className="text-sm text-muted">Sessions and tokens for {project.name}.</p>
      </div>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {loading ? <p className="text-sm text-muted">Loading…</p> : null}

      <section className="space-y-3">
        <h2 className="text-lg font-semibold">Index</h2>
        {repos === null ? (
          <p className="text-sm text-muted">No repositories yet.</p>
        ) : repos.length === 0 ? (
          <p className="text-sm text-muted">No repositories yet.</p>
        ) : (
          <ul className="space-y-2">
            {repos.map((repo) => (
              <li
                key={repo.id}
                className="rounded-lg border border-border bg-surface px-4 py-3 text-sm"
              >
                <div className="font-medium">
                  {repo.remote_url || repo.local_root_hint || "Repository"}
                </div>
                <dl className="mt-2 grid gap-1 text-muted sm:grid-cols-2">
                  <div>Index: {repo.index_mode.replaceAll("_", " ")}</div>
                  <div>Sidecar: {repo.sidecar_connected ? "connected" : "offline"}</div>
                  <div>Worker: {repo.worker_index_connected ? "connected" : "offline"}</div>
                  <div>Last indexed: {formatWhen(repo.last_indexed_at)}</div>
                </dl>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="space-y-3">
        <h2 className="text-lg font-semibold">Active sessions</h2>
        {activeSessions.length === 0 ? (
          <p className="text-sm text-muted">No active sessions.</p>
        ) : (
          <ul className="space-y-2">
            {activeSessions.map((session) => (
              <li
                key={session.id}
                className="rounded-lg border border-border bg-surface px-4 py-3 text-sm"
              >
                <div className="font-medium">{session.agent.name}</div>
                <p className="text-muted">
                  {session.agent.host} · last seen {formatWhen(session.last_heartbeat_at)}
                </p>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="space-y-3">
        <h2 className="text-lg font-semibold">Activity</h2>
        {activity.length === 0 ? (
          <p className="text-sm text-muted">No activity yet.</p>
        ) : (
          <ul className="space-y-2">
            {activity.slice(0, 40).map((event) => (
              <li
                key={event.id}
                className="rounded-lg border border-border bg-surface px-4 py-3 text-sm"
              >
                <div className="font-medium">{activityLabel(event)}</div>
                <p className="text-muted">{formatWhen(event.created_at)}</p>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-lg font-semibold">Tokens</h2>
          {canManageTokens ? (
            <button
              className="rounded-md border border-border px-3 py-1.5 text-sm"
              type="button"
              onClick={openCreate}
            >
              New token
            </button>
          ) : null}
        </div>
        {tokenError ? <p className="text-sm text-red-600">{tokenError}</p> : null}
        {revealed?.token ? (
          <div className="space-y-2 rounded-lg border border-border bg-surface px-4 py-3">
            <p className="text-sm font-medium">Copy this token now. It will not be shown again.</p>
            <code className="block break-all rounded-md bg-background px-3 py-2 text-sm">
              {revealed.token}
            </code>
            <button
              className="rounded-md border border-border px-3 py-1.5 text-sm"
              type="button"
              onClick={() => void onCopySecret()}
            >
              {copied ? "Copied" : "Copy token"}
            </button>
          </div>
        ) : null}
        {showCreate ? (
          <form
            className="space-y-3 rounded-lg border border-border bg-surface p-4"
            onSubmit={onCreateToken}
          >
            <label className="flex flex-col gap-1 text-sm">
              Name
              <input
                className="h-9 rounded-md border border-border bg-background px-3"
                value={tokenName}
                onChange={(event) => setTokenName(event.target.value)}
                maxLength={120}
                required
              />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              Expires
              <select
                className="h-9 rounded-md border border-border bg-background px-2"
                value={ttl}
                onChange={(event) => setTtl(event.target.value as TokenTtl)}
              >
                {TOKEN_TTLS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
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
                I confirm this token should never expire.
              </label>
            ) : null}
            <label className="flex items-start gap-2 text-sm">
              <input
                className="mt-1"
                type="checkbox"
                checked={allowCode}
                onChange={(event) => setAllowCode(event.target.checked)}
              />
              Allow code tools (tree, file, symbols)
            </label>
            <div className="flex gap-2">
              <button
                className="h-9 rounded-md bg-accent px-3 text-sm font-medium text-accent-fg disabled:opacity-60"
                type="submit"
                disabled={creating}
              >
                {creating ? "Creating…" : "Create token"}
              </button>
              <button
                className="h-9 rounded-md border border-border px-3 text-sm"
                type="button"
                onClick={() => {
                  setShowCreate(false);
                  resetCreateForm();
                }}
              >
                Cancel
              </button>
            </div>
          </form>
        ) : null}
        {!canManageTokens ? (
          <p className="text-sm text-muted">Project admins can mint and revoke tokens.</p>
        ) : listedTokens.length === 0 ? (
          <p className="text-sm text-muted">No tokens yet.</p>
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
                    {token.prefix}… ·{" "}
                    {token.scopes.includes(CODE_READ_SCOPE) ? "code tools on" : "code tools off"} ·
                    expires {formatWhen(token.expires_at)}
                  </p>
                </div>
                <button
                  className="rounded-md border border-border px-3 py-1.5"
                  type="button"
                  onClick={() => void onRevoke(token)}
                >
                  Revoke
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>
    </section>
  );
}
