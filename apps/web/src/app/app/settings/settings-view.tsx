"use client";

import { useCallback, useEffect, useMemo, useState, useSyncExternalStore, type FormEvent } from "react";

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
import { t } from "@/lib/i18n";
import { indexModeLabel } from "@/lib/index-status";
import { DEFAULT_POLL_MS } from "@/lib/poll";
import { SETTINGS_JUMP_LINKS, SETTINGS_THEME_OPTIONS } from "@/lib/settings";
import { getTheme, setTheme, subscribeTheme } from "@/lib/theme";
import { FIELD_INPUT_CLASS, FIELD_TEXTAREA_CLASS } from "@/lib/ui";
import { Banner } from "@/lib/ui/banner";
import { Button } from "@/lib/ui/button";
import { EmptyState } from "@/lib/ui/empty-state";
import { Field } from "@/lib/ui/field";
import { PageHeader } from "@/lib/ui/page-header";
import { Panel } from "@/lib/ui/panel";
import { Segmented, SegmentedItem } from "@/lib/ui/segmented";
import { useT, useTf } from "@/lib/use-locale";

import { AttachLocalRepoForm } from "../attach-local-repo-form";
import { CopyableProjectId } from "../copyable-project-id";
import { LabelsCatalog } from "./labels-catalog";
import { LanguagePicker } from "./language-picker";

const PROJECT_ROLES: { value: ProjectRole; labelKey: "settings.roleAdmin" | "settings.roleWrite" | "settings.roleRead" }[] = [
  { value: "admin", labelKey: "settings.roleAdmin" },
  { value: "write", labelKey: "settings.roleWrite" },
  { value: "read", labelKey: "settings.roleRead" },
];

const VISIBLE_INDEX_MODES: { value: IndexMode; labelKey: "settings.modeSidecar" | "settings.modeBindMount" }[] = [
  { value: "sidecar", labelKey: "settings.modeSidecar" },
  { value: "bind_mount", labelKey: "settings.modeBindMount" },
];

const HOSTED_INDEX_MODES: {
  value: IndexMode;
  labelKey: "settings.modeHostedClone" | "settings.modeBoth";
}[] = [
  { value: "hosted_clone", labelKey: "settings.modeHostedClone" },
  { value: "both", labelKey: "settings.modeBoth" },
];

function memberLabel(member: PublicProjectMember): string {
  return member.name || member.login || member.email || member.user_id;
}

function inviteTarget(invite: PublicProjectInvite): string {
  return invite.email || invite.github_login || t("common.invite");
}

function roleLabel(role: ProjectRole): string {
  if (role === "admin") {
    return t("settings.roleAdmin");
  }
  if (role === "write") {
    return t("settings.roleWrite");
  }
  return t("settings.roleRead");
}

function indexModes(hostedClone: boolean) {
  return hostedClone ? [...VISIBLE_INDEX_MODES, ...HOSTED_INDEX_MODES] : VISIBLE_INDEX_MODES;
}

function SettingsJumpNav({ ids }: { ids: ReadonlyArray<(typeof SETTINGS_JUMP_LINKS)[number]["id"]> }) {
  const label = useT();
  const links = SETTINGS_JUMP_LINKS.filter((item) => ids.includes(item.id));
  return (
    <nav className="flex flex-wrap gap-3 text-sm" aria-label={label("nav.settings")}>
      {links.map((item) => (
        <a className="underline-offset-2 hover:underline" href={`#${item.id}`} key={item.id}>
          {label(item.message)}
        </a>
      ))}
    </nav>
  );
}

function ThemeControl() {
  const label = useT();
  const theme = useSyncExternalStore(subscribeTheme, getTheme, getTheme);
  return (
    <section className="space-y-3" id="theme">
      <div>
        <h2 className="text-lg font-semibold">{label("settings.theme")}</h2>
        <p className="text-sm text-muted">{label("settings.themeHint")}</p>
      </div>
      <Segmented>
        {SETTINGS_THEME_OPTIONS.map((option) => (
          <SegmentedItem
            key={option.value}
            active={theme === option.value}
            aria-pressed={theme === option.value}
            onClick={() => setTheme(option.value)}
          >
            {label(option.message)}
          </SegmentedItem>
        ))}
      </Segmented>
    </section>
  );
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
  const label = useT();
  const format = useTf();
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
          setError(caught instanceof ApiError ? caught.message : t("common.failedLoadSettings"));
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
          setError(caught instanceof ApiError ? caught.message : t("common.failedRefreshSettings"));
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
  const canWriteLabels = Boolean(
    me &&
      members.some(
        (member) =>
          member.user_id === me.id && (member.role === "admin" || member.role === "write"),
      ),
  );

  async function onSaveProject(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!project) {
      return;
    }
    const nextName = name.trim();
    if (!nextName) {
      setError(t("common.nameRequired"));
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const updated = await updateProject(project.id, { name: nextName, description });
      onProjectSaved?.(updated);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("common.failedUpdateProject"));
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
      setError(caught instanceof ApiError ? caught.message : t("common.failedUpdateMember"));
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
      setInviteError(t("common.inviteTargetRequired"));
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
      setInviteError(caught instanceof ApiError ? caught.message : t("common.failedCreateInvite"));
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
      setError(caught instanceof ApiError ? caught.message : t("common.failedUpdateRepo"));
    }
  }

  if (!project) {
    return (
      <section className="space-y-6">
        <PageHeader title={label("nav.settings")} description={label("settings.selectProject")} />
        <SettingsJumpNav ids={["theme", "language"]} />
        <ThemeControl />
        <LanguagePicker />
      </section>
    );
  }

  return (
    <section className="space-y-8" data-sidecar-tunnel={sidecarTunnel ? "on" : "off"}>
      <PageHeader
        title={label("nav.settings")}
        description={format("settings.forProject", { name: project.name })}
      />
      <SettingsJumpNav ids={SETTINGS_JUMP_LINKS.map((item) => item.id)} />
      {error ? <Banner tone="danger">{error}</Banner> : null}
      {loading ? <p className="text-sm text-muted">{label("common.loading")}</p> : null}

      <ThemeControl />
      <LanguagePicker />

      <section className="space-y-3" id="project">
        <h2 className="text-lg font-semibold">{label("common.project")}</h2>
        <CopyableProjectId projectId={project.id} />
        <form className="max-w-xl space-y-3" onSubmit={onSaveProject}>
          <Field label={label("common.name")}>
            <input
              className={FIELD_INPUT_CLASS}
              value={name}
              onChange={(event) => setName(event.target.value)}
              maxLength={120}
              required
              disabled={!isAdmin}
            />
          </Field>
          <Field label={label("common.description")}>
            <textarea
              className={FIELD_TEXTAREA_CLASS}
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              maxLength={2000}
              disabled={!isAdmin}
            />
          </Field>
          {isAdmin ? (
            <Button type="submit" disabled={saving}>
              {saving ? label("common.saving") : label("common.save")}
            </Button>
          ) : null}
        </form>
      </section>

      <section className="space-y-3" id="members">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h2 className="text-lg font-semibold">{label("settings.members")}</h2>
            <p className="text-sm text-muted">{label("settings.membersHint")}</p>
          </div>
          {isAdmin ? (
            <Button variant="secondary" type="button" onClick={openInvite}>
              {label("common.invite")}
            </Button>
          ) : null}
        </div>
        {inviteError ? <Banner tone="danger">{inviteError}</Banner> : null}
        {showInvite ? (
          <Panel className="max-w-xl space-y-3">
            <form className="space-y-3" onSubmit={onInvite}>
              <Field label={label("settings.inviteTarget")}>
                <input
                  className={FIELD_INPUT_CLASS}
                  value={inviteTargetValue}
                  onChange={(event) => setInviteTargetValue(event.target.value)}
                  required
                />
              </Field>
              <Field label={label("common.role")}>
                <select
                  className={FIELD_INPUT_CLASS}
                  value={inviteRole}
                  onChange={(event) => setInviteRole(event.target.value as ProjectRole)}
                >
                  {roleOptions.map((option) => (
                    <option key={option.value} value={option.value}>
                      {label(option.labelKey)}
                    </option>
                  ))}
                </select>
              </Field>
              <p className="text-xs text-muted">{label("settings.inviteExpiresIn")}</p>
              <div className="flex gap-2">
                <Button type="submit" disabled={inviting}>
                  {inviting ? label("settings.sending") : label("settings.sendInvite")}
                </Button>
                <Button
                  variant="secondary"
                  type="button"
                  onClick={() => {
                    setShowInvite(false);
                    setInviteError(null);
                  }}
                >
                  {label("common.cancel")}
                </Button>
              </div>
            </form>
          </Panel>
        ) : null}
        {members.length === 0 ? (
          <EmptyState description={label("settings.noMembers")} />
        ) : (
          <ul className="space-y-2">
            {members.map((member) => (
              <li key={member.user_id}>
                <Panel className="flex flex-wrap items-center justify-between gap-3 px-4 py-3 text-sm">
                  <div>
                    <div className="font-medium">{memberLabel(member)}</div>
                    <p className="text-muted">{member.email ?? member.login ?? ""}</p>
                  </div>
                  <label className="flex items-center gap-2">
                    <span className="text-muted">{label("common.role")}</span>
                    <select
                      className={FIELD_INPUT_CLASS}
                      value={member.role}
                      onChange={(event) =>
                        void onRoleChange(member, event.target.value as ProjectRole)
                      }
                      aria-label={format("settings.roleFor", { name: memberLabel(member) })}
                      disabled={!isAdmin}
                    >
                      {roleOptions.map((option) => (
                        <option key={option.value} value={option.value}>
                          {label(option.labelKey)}
                        </option>
                      ))}
                    </select>
                  </label>
                </Panel>
              </li>
            ))}
          </ul>
        )}
        {invites.length > 0 ? (
          <div className="space-y-2">
            <h3 className="text-sm font-medium">{label("settings.pendingInvites")}</h3>
            <ul className="space-y-2">
              {invites.map((invite) => (
                <li key={invite.id}>
                  <Panel className="px-4 py-3 text-sm">
                    <div className="font-medium">{inviteTarget(invite)}</div>
                    <p className="text-muted">
                      {format("settings.inviteMeta", {
                        role: roleLabel(invite.role),
                        when: new Date(invite.expires_at).toLocaleString(),
                      })}
                    </p>
                  </Panel>
                </li>
              ))}
            </ul>
          </div>
        ) : null}
      </section>

      <section className="space-y-3" id="repositories">
        <div>
          <h2 className="text-lg font-semibold">{label("settings.repositories")}</h2>
          <p className="text-sm text-muted">{label("settings.repositoriesHint")}</p>
        </div>
        {repos === null || repos.length === 0 ? (
          <EmptyState description={label("settings.noRepos")} />
        ) : (
          <ul className="space-y-2">
            {repos.map((repo) => (
              <li key={repo.id}>
                <Panel className="space-y-1 px-4 py-3 text-sm">
                  <div className="font-medium">
                    {repo.remote_url || repo.local_root_hint || label("common.repository")}
                  </div>
                  <p className="text-muted">{indexModeLabel(repo.index_mode, { hostedClone })}</p>
                </Panel>
              </li>
            ))}
          </ul>
        )}
        <AttachLocalRepoForm
          projectId={project.id}
          canSubmit={isAdmin}
          onAttached={(created) => {
            setRepos((current) => (current ? [...current, created] : [created]));
          }}
        />
      </section>

      <LabelsCatalog projectId={project.id} repos={repos} canWrite={canWriteLabels} />

      {sidecarTunnel ? (
        <section className="space-y-3">
          <div>
            <h2 className="text-lg font-semibold">{label("settings.sidecarTunnel")}</h2>
            <p className="text-sm text-muted">{label("settings.sidecarHint")}</p>
          </div>
          {repos === null || repos.length === 0 ? (
            <EmptyState description={label("settings.connectRepo")} />
          ) : (
            <ul className="space-y-2">
              {repos.map((repo) => (
                <li key={repo.id}>
                  <Panel className="space-y-1 px-4 py-3 text-sm">
                    <div className="font-medium">
                      {repo.remote_url || repo.local_root_hint || label("common.repository")}
                    </div>
                    <p className="text-muted">
                      {repo.sidecar_connected ? label("settings.sidecarOn") : label("settings.sidecarOff")}
                    </p>
                  </Panel>
                </li>
              ))}
            </ul>
          )}
        </section>
      ) : null}

      {hostedClone ? (
        <section className="space-y-3">
          <div>
            <h2 className="text-lg font-semibold">{label("settings.hostedClone")}</h2>
            <p className="text-sm text-muted">{label("settings.hostedHint")}</p>
          </div>
          {repos === null || repos.length === 0 ? (
            <EmptyState description={label("settings.connectRepo")} />
          ) : (
            <ul className="space-y-2">
              {repos.map((repo) => (
                <li key={repo.id}>
                  <Panel className="space-y-2 px-4 py-3 text-sm">
                    <div className="font-medium">
                      {repo.remote_url || repo.local_root_hint || label("common.repository")}
                    </div>
                    <Field label={label("settings.index")}>
                      <select
                        className={`${FIELD_INPUT_CLASS} max-w-sm`}
                        value={repo.index_mode}
                        onChange={(event) =>
                          void onIndexModeChange(repo, event.target.value as IndexMode)
                        }
                        disabled={!isAdmin}
                      >
                        {modeOptions.map((option) => (
                          <option key={option.value} value={option.value}>
                            {label(option.labelKey)}
                          </option>
                        ))}
                      </select>
                    </Field>
                  </Panel>
                </li>
              ))}
            </ul>
          )}
        </section>
      ) : null}
    </section>
  );
}
