"use client";

import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";

import {
  ApiError,
  createProjectInvite,
  fetchProjectMembers,
  fetchProjectRepos,
  fetchRepo,
  updateProject,
  updateRepoIndexMode,
  upsertProjectMember,
  type IndexMode,
  type ProjectRole,
  type PublicMe,
  type PublicProject,
  type PublicProjectInvite,
  type PublicProjectMember,
  type PublicRepo,
} from "@/lib/api";
import { DEFAULT_POLL_MS } from "@/lib/poll";

const PROJECT_ROLES: { value: ProjectRole; label: string }[] = [
  { value: "admin", label: "Admin" },
  { value: "write", label: "Write" },
  { value: "read", label: "Read" },
];

const VISIBLE_INDEX_MODES: { value: IndexMode; label: string }[] = [
  { value: "sidecar", label: "This machine" },
  { value: "bind_mount", label: "Compose workspace" },
];

const HOSTED_INDEX_MODES: { value: IndexMode; label: string }[] = [
  { value: "hosted_clone", label: "Hosted clone" },
  { value: "both", label: "Sidecar, then hosted clone" },
];

function memberLabel(member: PublicProjectMember): string {
  return member.name || member.login || member.email || member.user_id;
}

function inviteTarget(invite: PublicProjectInvite): string {
  return invite.email || invite.github_login || "Invite";
}

function indexModes(hostedClone: boolean): { value: IndexMode; label: string }[] {
  return hostedClone ? [...VISIBLE_INDEX_MODES, ...HOSTED_INDEX_MODES] : VISIBLE_INDEX_MODES;
}

export function SettingsView({
  project,
  me,
  hostedClone,
  sidecarTunnel,
  onProjectSaved,
}: {
  project: PublicProject | null;
  me: PublicMe | null;
  hostedClone: boolean;
  sidecarTunnel: boolean;
  onProjectSaved?: (project: PublicProject) => void;
}) {
  const [name, setName] = useState(project?.name ?? "");
  const [description, setDescription] = useState(project?.description ?? "");
  const [members, setMembers] = useState<PublicProjectMember[]>([]);
  const [invites, setInvites] = useState<PublicProjectInvite[]>([]);
  const [repos, setRepos] = useState<PublicRepo[] | null>([]);
  const [error, setError] = useState<string | null>(null);
  const [inviteError, setInviteError] = useState<string | null>(null);
  const [loading, setLoading] = useState(Boolean(project));
  const [saving, setSaving] = useState(false);
  const [inviting, setInviting] = useState(false);
  const [showInvite, setShowInvite] = useState(false);
  const [inviteTargetValue, setInviteTargetValue] = useState("");
  const [inviteRole, setInviteRole] = useState<ProjectRole>("read");

  const loadMembers = useCallback(async (projectId: string) => {
    return fetchProjectMembers(projectId);
  }, []);

  const loadRepos = useCallback(async (projectId: string) => {
    const listed = await fetchProjectRepos(projectId);
    if (!listed) {
      return listed;
    }
    return Promise.all(
      listed.map(async (repo) => {
        const live = await fetchRepo(repo.id);
        return live ?? repo;
      }),
    );
  }, []);

  useEffect(() => {
    if (!project) {
      return;
    }
    let cancelled = false;
    void Promise.all([loadMembers(project.id), loadRepos(project.id)])
      .then(([nextMembers, nextRepos]) => {
        if (cancelled) {
          return;
        }
        setMembers(nextMembers);
        setRepos(nextRepos);
      })
      .catch((caught: unknown) => {
        if (!cancelled) {
          setError(caught instanceof ApiError ? caught.message : "failed to load settings");
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
  }, [loadMembers, loadRepos, project]);

  useEffect(() => {
    if (!project) {
      return;
    }
    const id = window.setInterval(() => {
      void Promise.all([loadMembers(project.id), loadRepos(project.id)])
        .then(([nextMembers, nextRepos]) => {
          setMembers(nextMembers);
          setRepos(nextRepos);
        })
        .catch((caught: unknown) => {
          setError(caught instanceof ApiError ? caught.message : "failed to refresh settings");
        });
    }, DEFAULT_POLL_MS);
    return () => {
      window.clearInterval(id);
    };
  }, [loadMembers, loadRepos, project]);

  const roleOptions = useMemo(() => PROJECT_ROLES, []);
  const modeOptions = useMemo(() => indexModes(hostedClone), [hostedClone]);
  const isAdmin = Boolean(
    me && members.some((member) => member.user_id === me.id && member.role === "admin"),
  );

  async function onSaveProject(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!project) {
      return;
    }
    const nextName = name.trim();
    if (!nextName) {
      setError("name is required");
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const updated = await updateProject(project.id, { name: nextName, description });
      onProjectSaved?.(updated);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "failed to update project");
    } finally {
      setSaving(false);
    }
  }

  async function onRoleChange(member: PublicProjectMember, role: ProjectRole) {
    if (!project || member.role === role) {
      return;
    }
    try {
      const updated = await upsertProjectMember(project.id, { user_id: member.user_id, role });
      setMembers((current) =>
        current.map((item) => (item.user_id === updated.user_id ? updated : item)),
      );
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "failed to update member");
    }
  }

  function openInvite() {
    setInviteTargetValue("");
    setInviteRole("read");
    setInviteError(null);
    setShowInvite(true);
  }

  async function onInvite(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!project) {
      return;
    }
    const target = inviteTargetValue.trim();
    if (!target) {
      setInviteError("email or GitHub login is required");
      return;
    }
    setInviting(true);
    setInviteError(null);
    const body = target.includes("@")
      ? { email: target, role: inviteRole }
      : { github_login: target, role: inviteRole };
    try {
      const invite = await createProjectInvite(project.id, body);
      setInvites((current) => [invite, ...current.filter((item) => item.id !== invite.id)]);
      setShowInvite(false);
      setInviteTargetValue("");
      setInviteRole("read");
    } catch (caught) {
      setInviteError(caught instanceof ApiError ? caught.message : "failed to create invite");
    } finally {
      setInviting(false);
    }
  }

  async function onIndexModeChange(repo: PublicRepo, mode: IndexMode) {
    if (repo.index_mode === mode) {
      return;
    }
    try {
      const updated = await updateRepoIndexMode(repo.id, mode);
      setRepos((current) =>
        current
          ? current.map((item) => (item.id === updated.id ? { ...item, ...updated } : item))
          : current,
      );
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "failed to update repository");
    }
  }

  if (!project) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
        <p className="text-sm text-muted">Select a project to edit members and settings.</p>
      </section>
    );
  }

  return (
    <section className="space-y-8" data-sidecar-tunnel={sidecarTunnel ? "on" : "off"}>
      <div className="space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight">Settings</h1>
        <p className="text-sm text-muted">Project members and settings for {project.name}.</p>
      </div>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {loading ? <p className="text-sm text-muted">Loading…</p> : null}

      <section className="space-y-3">
        <h2 className="text-lg font-semibold">Project</h2>
        <form className="max-w-xl space-y-3" onSubmit={onSaveProject}>
          <label className="flex flex-col gap-1 text-sm">
            Name
            <input
              className="h-9 rounded-md border border-border bg-background px-3"
              value={name}
              onChange={(event) => setName(event.target.value)}
              maxLength={120}
              required
              disabled={!isAdmin}
            />
          </label>
          <label className="flex flex-col gap-1 text-sm">
            Description
            <textarea
              className="min-h-24 rounded-md border border-border bg-background px-3 py-2"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              maxLength={2000}
              disabled={!isAdmin}
            />
          </label>
          {isAdmin ? (
            <button
              className="h-9 rounded-md bg-accent px-3 text-sm font-medium text-accent-fg disabled:opacity-60"
              type="submit"
              disabled={saving}
            >
              {saving ? "Saving…" : "Save"}
            </button>
          ) : null}
        </form>
      </section>

      <section className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="text-lg font-semibold">Members</h2>
            <p className="text-sm text-muted">These roles apply only to this project.</p>
          </div>
          {isAdmin ? (
            <button
              className="rounded-md border border-border px-3 py-1.5 text-sm"
              type="button"
              onClick={openInvite}
            >
              Invite
            </button>
          ) : null}
        </div>
        {inviteError ? <p className="text-sm text-red-600">{inviteError}</p> : null}
        {showInvite ? (
          <form
            className="max-w-xl space-y-3 rounded-lg border border-border bg-surface p-4"
            onSubmit={onInvite}
          >
            <label className="flex flex-col gap-1 text-sm">
              Email or GitHub login
              <input
                className="h-9 rounded-md border border-border bg-background px-3"
                value={inviteTargetValue}
                onChange={(event) => setInviteTargetValue(event.target.value)}
                required
              />
            </label>
            <label className="flex flex-col gap-1 text-sm">
              Role
              <select
                className="h-9 rounded-md border border-border bg-background px-2"
                value={inviteRole}
                onChange={(event) => setInviteRole(event.target.value as ProjectRole)}
              >
                {roleOptions.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </label>
            <p className="text-xs text-muted">Invites expire in 7 days.</p>
            <div className="flex gap-2">
              <button
                className="h-9 rounded-md bg-accent px-3 text-sm font-medium text-accent-fg disabled:opacity-60"
                type="submit"
                disabled={inviting}
              >
                {inviting ? "Sending…" : "Send invite"}
              </button>
              <button
                className="h-9 rounded-md border border-border px-3 text-sm"
                type="button"
                onClick={() => {
                  setShowInvite(false);
                  setInviteError(null);
                }}
              >
                Cancel
              </button>
            </div>
          </form>
        ) : null}
        {members.length === 0 ? (
          <p className="text-sm text-muted">No members yet.</p>
        ) : (
          <ul className="space-y-2">
            {members.map((member) => (
              <li
                key={member.user_id}
                className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border bg-surface px-4 py-3 text-sm"
              >
                <div>
                  <div className="font-medium">{memberLabel(member)}</div>
                  <p className="text-muted">{member.email ?? member.login ?? ""}</p>
                </div>
                <label className="flex items-center gap-2">
                  <span className="text-muted">Role</span>
                  <select
                    className="h-9 rounded-md border border-border bg-background px-2"
                    value={member.role}
                    onChange={(event) =>
                      void onRoleChange(member, event.target.value as ProjectRole)
                    }
                    aria-label={`Role for ${memberLabel(member)}`}
                    disabled={!isAdmin}
                  >
                    {roleOptions.map((option) => (
                      <option key={option.value} value={option.value}>
                        {option.label}
                      </option>
                    ))}
                  </select>
                </label>
              </li>
            ))}
          </ul>
        )}
        {invites.length > 0 ? (
          <div className="space-y-2">
            <h3 className="text-sm font-medium">Pending invites</h3>
            <ul className="space-y-2">
              {invites.map((invite) => (
                <li
                  key={invite.id}
                  className="rounded-lg border border-border bg-surface px-4 py-3 text-sm"
                >
                  <div className="font-medium">{inviteTarget(invite)}</div>
                  <p className="text-muted">
                    {invite.role} · expires {new Date(invite.expires_at).toLocaleString()}
                  </p>
                </li>
              ))}
            </ul>
          </div>
        ) : null}
      </section>

      {sidecarTunnel ? (
        <section className="space-y-3">
          <div>
            <h2 className="text-lg font-semibold">Sidecar tunnel</h2>
            <p className="text-sm text-muted">
              When a sidecar is connected, remote code tools can read this machine through the
              control plane.
            </p>
          </div>
          {repos === null || repos.length === 0 ? (
            <p className="text-sm text-muted">Connect a repository first.</p>
          ) : (
            <ul className="space-y-2">
              {repos.map((repo) => (
                <li
                  key={repo.id}
                  className="space-y-1 rounded-lg border border-border bg-surface px-4 py-3 text-sm"
                >
                  <div className="font-medium">
                    {repo.remote_url || repo.local_root_hint || "Repository"}
                  </div>
                  <p className="text-muted">
                    {repo.sidecar_connected
                      ? "Sidecar connected. Code tools may transit file contents."
                      : "Sidecar offline."}
                  </p>
                </li>
              ))}
            </ul>
          )}
        </section>
      ) : null}

      {hostedClone ? (
        <section className="space-y-3">
          <div>
            <h2 className="text-lg font-semibold">Hosted clone</h2>
            <p className="text-sm text-muted">
              Index a GitHub clone on this instance. Leave this off unless you want source on the
              server.
            </p>
          </div>
          {repos === null || repos.length === 0 ? (
            <p className="text-sm text-muted">Connect a repository first.</p>
          ) : (
            <ul className="space-y-2">
              {repos.map((repo) => (
                <li
                  key={repo.id}
                  className="space-y-2 rounded-lg border border-border bg-surface px-4 py-3 text-sm"
                >
                  <div className="font-medium">
                    {repo.remote_url || repo.local_root_hint || "Repository"}
                  </div>
                  <label className="flex flex-col gap-1">
                    Index
                    <select
                      className="h-9 max-w-sm rounded-md border border-border bg-background px-2"
                      value={repo.index_mode}
                      onChange={(event) =>
                        void onIndexModeChange(repo, event.target.value as IndexMode)
                      }
                      disabled={!isAdmin}
                    >
                      {modeOptions.map((option) => (
                        <option key={option.value} value={option.value}>
                          {option.label}
                        </option>
                      ))}
                    </select>
                  </label>
                </li>
              ))}
            </ul>
          )}
        </section>
      ) : null}
    </section>
  );
}
