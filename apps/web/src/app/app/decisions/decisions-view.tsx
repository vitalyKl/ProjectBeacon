"use client";

import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";

import { ApiError, newIdempotencyKey } from "@/lib/api";
import {
  applyConstraint,
  canAcceptDecision,
  canApplyConstraint,
  canDeprecateDecision,
  canSupersedeDecision,
  CONSTRAINT_KINDS,
  CONSTRAINT_STATUSES,
  constraintKindLabel,
  constraintStatusLabel,
  createConstraint,
  createDecision,
  DECISION_STATUSES,
  decisionStatusLabel,
  fetchProjectConstraints,
  fetchProjectDecisions,
  patchDecision,
  sortConstraints,
  sortDecisions,
  successorDecisionOptions,
  type ConstraintKind,
  type ConstraintStatus,
  type DecisionStatus,
  type PublicConstraint,
  type PublicDecision,
} from "@/lib/decisions";
import { t } from "@/lib/i18n";
import { DEFAULT_POLL_MS } from "@/lib/poll";
import { useInterval } from "@/lib/use-interval";
import { useT, useTf } from "@/lib/use-locale";

import { useSelectedProject } from "../project-context";

export function DecisionsView() {
  const label = useT();
  const format = useTf();
  const { project } = useSelectedProject();
  const [decisions, setDecisions] = useState<PublicDecision[]>([]);
  const [constraints, setConstraints] = useState<PublicConstraint[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(Boolean(project));
  const projectId = project?.id ?? null;

  const reload = useCallback(
    async (opts?: { silent?: boolean }) => {
      if (!projectId) {
        return;
      }
      try {
        const [nextDecisions, nextConstraints] = await Promise.all([
          fetchProjectDecisions(projectId),
          fetchProjectConstraints(projectId),
        ]);
        setDecisions(nextDecisions);
        setConstraints(nextConstraints);
        setError(null);
      } catch (caught) {
        setError(caught instanceof ApiError ? caught.message : t("decisions.failedLoad"));
      } finally {
        if (!opts?.silent) {
          setLoading(false);
        }
      }
    },
    [projectId],
  );

  useEffect(() => {
    if (!projectId) {
      const id = window.setTimeout(() => {
        setDecisions([]);
        setConstraints([]);
        setError(null);
        setLoading(false);
      }, 0);
      return () => window.clearTimeout(id);
    }
    const id = window.setTimeout(() => {
      void reload();
    }, 0);
    return () => window.clearTimeout(id);
  }, [projectId, reload]);

  useInterval(
    () => {
      void reload({ silent: true });
    },
    projectId ? DEFAULT_POLL_MS : null,
  );

  const sortedDecisions = useMemo(() => sortDecisions(decisions), [decisions]);
  const sortedConstraints = useMemo(() => sortConstraints(constraints), [constraints]);

  async function onCreatedDecision(created: PublicDecision) {
    setDecisions((current) => [created, ...current.filter((item) => item.id !== created.id)]);
  }

  async function onCreatedConstraint(created: PublicConstraint) {
    setConstraints((current) => [created, ...current.filter((item) => item.id !== created.id)]);
  }

  async function onUpdatedDecision(updated: PublicDecision) {
    setDecisions((current) => current.map((item) => (item.id === updated.id ? updated : item)));
  }

  async function onApply(constraint: PublicConstraint) {
    try {
      const applied = await applyConstraint(constraint.id);
      setConstraints((current) =>
        current.map((item) => (item.id === applied.id ? applied : item)),
      );
      setError(null);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("decisions.failedApply"));
    }
  }

  if (!project) {
    return (
      <section className="space-y-2">
        <h1 className="text-2xl font-semibold tracking-tight">{label("nav.decisions")}</h1>
        <p className="text-sm text-muted">{label("common.selectProject")}</p>
      </section>
    );
  }

  return (
    <section className="space-y-8">
      <header className="space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight">{label("nav.decisions")}</h1>
        <p className="text-sm text-muted">{format("decisions.intro", { name: project.name })}</p>
      </header>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      {loading ? <p className="text-sm text-muted">{label("common.loading")}</p> : null}

      <section className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-lg font-semibold">{label("decisions.adrs")}</h2>
          <CreateDecisionForm projectId={project.id} onCreated={onCreatedDecision} />
        </div>
        {sortedDecisions.length === 0 && !loading ? (
          <p className="text-sm text-muted">{label("decisions.empty")}</p>
        ) : (
          <ul className="space-y-3">
            {sortedDecisions.map((item) => (
              <li
                key={item.id}
                className="space-y-2 rounded-lg border border-border bg-surface px-4 py-3"
              >
                <div className="flex flex-wrap items-start justify-between gap-2">
                  <h3 className="font-medium">{item.title}</h3>
                  <span className="text-xs tracking-wide text-muted uppercase">
                    {decisionStatusLabel(item.status)}
                  </span>
                </div>
                <p className="text-sm whitespace-pre-wrap">{item.decision}</p>
                {item.context ? (
                  <p className="text-sm text-muted whitespace-pre-wrap">{item.context}</p>
                ) : null}
                {item.consequences ? (
                  <p className="text-sm text-muted whitespace-pre-wrap">{item.consequences}</p>
                ) : null}
                <DecisionLifecycleActions
                  decision={item}
                  decisions={sortedDecisions}
                  onUpdated={onUpdatedDecision}
                  onError={setError}
                />
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h2 className="text-lg font-semibold">{label("decisions.constraints")}</h2>
          <CreateConstraintForm projectId={project.id} onCreated={onCreatedConstraint} />
        </div>
        {sortedConstraints.length === 0 && !loading ? (
          <p className="text-sm text-muted">{label("decisions.noConstraints")}</p>
        ) : (
          <ul className="space-y-3">
            {sortedConstraints.map((item) => (
              <li
                key={item.id}
                className="flex flex-wrap items-start justify-between gap-3 rounded-lg border border-border bg-surface px-4 py-3"
              >
                <div className="min-w-0 space-y-1">
                  <div className="flex flex-wrap items-center gap-2 text-xs tracking-wide text-muted uppercase">
                    <span>{constraintKindLabel(item.kind)}</span>
                    <span>{constraintStatusLabel(item.status)}</span>
                    {item.scope_path ? <span className="normal-case">{item.scope_path}</span> : null}
                  </div>
                  <p className="text-sm whitespace-pre-wrap">{item.body}</p>
                </div>
                {canApplyConstraint(item) ? (
                  <button
                    className="rounded-md border border-border px-3 py-1.5 text-sm"
                    type="button"
                    onClick={() => void onApply(item)}
                  >
                    {label("common.apply")}
                  </button>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </section>
    </section>
  );
}

function DecisionLifecycleActions({
  decision,
  decisions,
  onUpdated,
  onError,
}: {
  decision: PublicDecision;
  decisions: PublicDecision[];
  onUpdated: (decision: PublicDecision) => void | Promise<void>;
  onError: (message: string | null) => void;
}) {
  const label = useT();
  const format = useTf();
  const [superseding, setSuperseding] = useState(false);
  const successors = successorDecisionOptions(decisions, decision.id);
  const [successorId, setSuccessorId] = useState(successors[0]?.id ?? "");
  const [pending, setPending] = useState(false);
  const successor = decision.superseded_by
    ? decisions.find((item) => item.id === decision.superseded_by)
    : undefined;

  async function applyStatus(status: "accepted" | "superseded" | "deprecated", nextId?: string) {
    setPending(true);
    try {
      const updated = await patchDecision(decision.id, {
        status,
        ...(status === "superseded" ? { superseded_by: nextId } : {}),
      });
      onError(null);
      setSuperseding(false);
      await onUpdated(updated);
    } catch (caught) {
      onError(caught instanceof ApiError ? caught.message : t("decisions.failedUpdate"));
    } finally {
      setPending(false);
    }
  }

  const showAccept = canAcceptDecision(decision);
  const showSupersede = canSupersedeDecision(decision);
  const showDeprecate = canDeprecateDecision(decision);
  if (!showAccept && !showSupersede && !showDeprecate && !successor) {
    return null;
  }

  return (
    <div className="space-y-2">
      {successor ? (
        <p className="text-sm text-muted">
          {format("decisions.supersededBy", { title: successor.title })}
        </p>
      ) : null}
      {showAccept || showSupersede || showDeprecate ? (
        <div className="flex flex-wrap gap-2">
          {showAccept ? (
            <button
              className="rounded-md border border-border px-3 py-1.5 text-sm"
              type="button"
              disabled={pending}
              onClick={() => void applyStatus("accepted")}
            >
              {label("decisions.accept")}
            </button>
          ) : null}
          {showSupersede ? (
            <button
              className="rounded-md border border-border px-3 py-1.5 text-sm"
              type="button"
              disabled={pending}
              onClick={() => {
                setSuccessorId(successors[0]?.id ?? "");
                setSuperseding((open) => !open);
              }}
            >
              {label("decisions.supersede")}
            </button>
          ) : null}
          {showDeprecate ? (
            <button
              className="rounded-md border border-border px-3 py-1.5 text-sm"
              type="button"
              disabled={pending}
              onClick={() => void applyStatus("deprecated")}
            >
              {label("decisions.deprecate")}
            </button>
          ) : null}
        </div>
      ) : null}
      {superseding && showSupersede ? (
        successors.length === 0 ? (
          <p className="text-sm text-muted">{label("decisions.noSuccessor")}</p>
        ) : (
          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(event) => {
              event.preventDefault();
              if (!successorId) {
                return;
              }
              void applyStatus("superseded", successorId);
            }}
          >
            <label className="flex min-w-48 flex-1 flex-col gap-1 text-sm">
              <span className="text-muted">{label("decisions.supersedeWith")}</span>
              <select
                className="h-9 rounded-md border border-border bg-background px-2 text-sm"
                aria-label={label("decisions.chooseSuccessor")}
                value={successorId}
                onChange={(event) => setSuccessorId(event.target.value)}
              >
                {successors.map((item) => (
                  <option key={item.id} value={item.id}>
                    {item.title}
                  </option>
                ))}
              </select>
            </label>
            <button
              className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
              type="submit"
              disabled={pending || !successorId}
            >
              {pending ? label("common.saving") : label("decisions.supersede")}
            </button>
          </form>
        )
      ) : null}
    </div>
  );
}

function CreateDecisionForm({
  projectId,
  onCreated,
}: {
  projectId: string;
  onCreated: (decision: PublicDecision) => void | Promise<void>;
}) {
  const label = useT();
  const [open, setOpen] = useState(false);
  const [title, setTitle] = useState("");
  const [context, setContext] = useState("");
  const [decision, setDecision] = useState("");
  const [consequences, setConsequences] = useState("");
  const [status, setStatus] = useState<DecisionStatus>("accepted");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  function reset() {
    setTitle("");
    setContext("");
    setDecision("");
    setConsequences("");
    setStatus("accepted");
    setError(null);
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const nextTitle = title.trim();
    const nextContext = context.trim();
    const nextDecision = decision.trim();
    if (!nextTitle || !nextContext || !nextDecision) {
      setError(t("decisions.required"));
      return;
    }
    setPending(true);
    setError(null);
    try {
      const created = await createDecision(
        projectId,
        {
          title: nextTitle,
          context: nextContext,
          decision: nextDecision,
          consequences: consequences.trim(),
          status,
        },
        newIdempotencyKey(),
      );
      reset();
      setOpen(false);
      await onCreated(created);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("decisions.failedCreate"));
    } finally {
      setPending(false);
    }
  }

  if (!open) {
    return (
      <button
        className="rounded-md border border-border px-3 py-1.5 text-sm"
        type="button"
        onClick={() => {
          reset();
          setOpen(true);
        }}
      >
        {label("decisions.new")}
      </button>
    );
  }

  return (
    <form
      className="flex w-full max-w-xl flex-col gap-2 rounded-md border border-border bg-surface p-3"
      onSubmit={onSubmit}
    >
      <input
        className="h-9 rounded-md border border-border bg-background px-2 text-sm"
        placeholder={label("common.title")}
        value={title}
        onChange={(event) => setTitle(event.target.value)}
        maxLength={200}
        required
      />
      <textarea
        className="min-h-16 rounded-md border border-border bg-background px-2 py-1 text-sm"
        placeholder={label("decisions.context")}
        value={context}
        onChange={(event) => setContext(event.target.value)}
        maxLength={8000}
        required
      />
      <textarea
        className="min-h-16 rounded-md border border-border bg-background px-2 py-1 text-sm"
        placeholder={label("decisions.decision")}
        value={decision}
        onChange={(event) => setDecision(event.target.value)}
        maxLength={8000}
        required
      />
      <textarea
        className="min-h-16 rounded-md border border-border bg-background px-2 py-1 text-sm"
        placeholder={label("decisions.consequences")}
        value={consequences}
        onChange={(event) => setConsequences(event.target.value)}
        maxLength={8000}
      />
      <select
        className="h-9 rounded-md border border-border bg-background px-2 text-sm capitalize"
        aria-label={label("decisions.status")}
        value={status}
        onChange={(event) => setStatus(event.target.value as DecisionStatus)}
      >
        {DECISION_STATUSES.map((item) => (
          <option key={item} value={item}>
            {decisionStatusLabel(item)}
          </option>
        ))}
      </select>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      <div className="flex gap-2">
        <button
          className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
          type="submit"
          disabled={pending}
        >
          {pending ? label("common.saving") : label("common.save")}
        </button>
        <button
          className="rounded-md border border-border px-3 py-1.5 text-sm"
          type="button"
          onClick={() => {
            reset();
            setOpen(false);
          }}
        >
          {label("common.cancel")}
        </button>
      </div>
    </form>
  );
}

function CreateConstraintForm({
  projectId,
  onCreated,
}: {
  projectId: string;
  onCreated: (constraint: PublicConstraint) => void | Promise<void>;
}) {
  const label = useT();
  const [open, setOpen] = useState(false);
  const [kind, setKind] = useState<ConstraintKind>("must");
  const [body, setBody] = useState("");
  const [scopePath, setScopePath] = useState("");
  const [status, setStatus] = useState<ConstraintStatus>("active");
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  function reset() {
    setKind("must");
    setBody("");
    setScopePath("");
    setStatus("active");
    setError(null);
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const nextBody = body.trim();
    if (!nextBody) {
      setError(t("decisions.bodyRequired"));
      return;
    }
    setPending(true);
    setError(null);
    try {
      const created = await createConstraint(
        projectId,
        {
          kind,
          body: nextBody,
          scope_path: scopePath.trim(),
          status,
        },
        newIdempotencyKey(),
      );
      reset();
      setOpen(false);
      await onCreated(created);
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("decisions.failedCreateConstraint"));
    } finally {
      setPending(false);
    }
  }

  if (!open) {
    return (
      <button
        className="rounded-md border border-border px-3 py-1.5 text-sm"
        type="button"
        onClick={() => {
          reset();
          setOpen(true);
        }}
      >
        {label("decisions.newConstraint")}
      </button>
    );
  }

  return (
    <form
      className="flex w-full max-w-xl flex-col gap-2 rounded-md border border-border bg-surface p-3"
      onSubmit={onSubmit}
    >
      <textarea
        className="min-h-16 rounded-md border border-border bg-background px-2 py-1 text-sm"
        placeholder={label("decisions.rule")}
        value={body}
        onChange={(event) => setBody(event.target.value)}
        maxLength={8000}
        required
      />
      <input
        className="h-9 rounded-md border border-border bg-background px-2 text-sm"
        placeholder={label("decisions.scopeOptional")}
        value={scopePath}
        onChange={(event) => setScopePath(event.target.value)}
        maxLength={1024}
      />
      <div className="flex flex-wrap gap-2">
        <select
          className="h-9 rounded-md border border-border bg-background px-2 text-sm capitalize"
          aria-label={label("decisions.kind")}
          value={kind}
          onChange={(event) => setKind(event.target.value as ConstraintKind)}
        >
          {CONSTRAINT_KINDS.map((item) => (
            <option key={item} value={item}>
              {constraintKindLabel(item)}
            </option>
          ))}
        </select>
        <select
          className="h-9 rounded-md border border-border bg-background px-2 text-sm capitalize"
          aria-label={label("decisions.constraintStatus")}
          value={status}
          onChange={(event) => setStatus(event.target.value as ConstraintStatus)}
        >
          {CONSTRAINT_STATUSES.filter((item) => item !== "rejected").map((item) => (
            <option key={item} value={item}>
              {constraintStatusLabel(item)}
            </option>
          ))}
        </select>
      </div>
      {error ? <p className="text-sm text-red-600">{error}</p> : null}
      <div className="flex gap-2">
        <button
          className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
          type="submit"
          disabled={pending}
        >
          {pending ? label("common.saving") : label("common.save")}
        </button>
        <button
          className="rounded-md border border-border px-3 py-1.5 text-sm"
          type="button"
          onClick={() => {
            reset();
            setOpen(false);
          }}
        >
          {label("common.cancel")}
        </button>
      </div>
    </form>
  );
}
