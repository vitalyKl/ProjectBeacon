"use client";

import { useState, type FormEvent } from "react";

import { ApiError, newIdempotencyKey } from "@/lib/api";
import { t } from "@/lib/i18n";
import {
  createMilestone,
  draftFromMilestone,
  emptyMilestoneDraft,
  MILESTONE_STATUSES,
  milestoneStatusLabel,
  milestoneWriteFromDraft,
  patchMilestone,
  type MilestoneDraft,
  type MilestoneStatus,
  type PublicMilestone,
} from "@/lib/roadmap";
import { FIELD_ERROR_CLASS, FIELD_INPUT_CLASS, FIELD_TEXTAREA_CLASS } from "@/lib/ui";
import { useT } from "@/lib/use-locale";

export function CreateMilestoneForm({
  projectId,
  onCreated,
}: {
  projectId: string;
  onCreated: () => Promise<void> | void;
}) {
  const label = useT();
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState<MilestoneDraft>(emptyMilestoneDraft);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  function reset() {
    setDraft(emptyMilestoneDraft());
    setError(null);
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const parsed = milestoneWriteFromDraft(draft);
    if (!parsed.ok) {
      setError(parsed.error === "title" ? t("common.titleRequired") : t("roadmap.invalidDate"));
      return;
    }
    setPending(true);
    setError(null);
    try {
      await createMilestone(projectId, parsed.value, newIdempotencyKey());
      reset();
      setOpen(false);
      await onCreated();
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("roadmap.failedCreate"));
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
        {label("roadmap.new")}
      </button>
    );
  }

  return (
    <MilestoneFields
      draft={draft}
      onChange={setDraft}
      error={error}
      pending={pending}
      submitLabel={pending ? label("common.saving") : label("common.save")}
      onSubmit={onSubmit}
      onCancel={() => {
        reset();
        setOpen(false);
      }}
    />
  );
}

export function EditMilestoneForm({
  milestone,
  onUpdated,
}: {
  milestone: PublicMilestone;
  onUpdated: () => Promise<void> | void;
}) {
  const label = useT();
  const [open, setOpen] = useState(false);
  const [draft, setDraft] = useState<MilestoneDraft>(() => draftFromMilestone(milestone));
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  function reset() {
    setDraft(draftFromMilestone(milestone));
    setError(null);
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const parsed = milestoneWriteFromDraft(draft);
    if (!parsed.ok) {
      setError(parsed.error === "title" ? t("common.titleRequired") : t("roadmap.invalidDate"));
      return;
    }
    setPending(true);
    setError(null);
    try {
      await patchMilestone(milestone.id, parsed.value);
      setOpen(false);
      await onUpdated();
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : t("roadmap.failedUpdate"));
    } finally {
      setPending(false);
    }
  }

  if (!open) {
    return (
      <button
        className="rounded-md border border-border px-2 py-0.5 text-xs"
        type="button"
        aria-label={label("roadmap.edit")}
        onClick={() => {
          reset();
          setOpen(true);
        }}
      >
        {label("common.edit")}
      </button>
    );
  }

  return (
    <MilestoneFields
      draft={draft}
      onChange={setDraft}
      error={error}
      pending={pending}
      submitLabel={pending ? label("common.saving") : label("common.save")}
      onSubmit={onSubmit}
      onCancel={() => {
        reset();
        setOpen(false);
      }}
    />
  );
}

function MilestoneFields({
  draft,
  onChange,
  error,
  pending,
  submitLabel,
  onSubmit,
  onCancel,
}: {
  draft: MilestoneDraft;
  onChange: (draft: MilestoneDraft) => void;
  error: string | null;
  pending: boolean;
  submitLabel: string;
  onSubmit: (event: FormEvent<HTMLFormElement>) => void;
  onCancel: () => void;
}) {
  const label = useT();

  return (
    <form
      className="flex max-w-xl flex-col gap-2 rounded-md border border-border bg-surface p-3"
      onSubmit={onSubmit}
    >
      <input
        className={FIELD_INPUT_CLASS}
        placeholder={label("common.title")}
        value={draft.title}
        onChange={(event) => onChange({ ...draft, title: event.target.value })}
        maxLength={200}
        required
        aria-label={label("common.title")}
      />
      <textarea
        className={FIELD_TEXTAREA_CLASS}
        placeholder={label("common.description")}
        value={draft.description}
        onChange={(event) => onChange({ ...draft, description: event.target.value })}
        maxLength={8000}
        aria-label={label("common.description")}
      />
      <div className="flex flex-wrap gap-2">
        <select
          className={FIELD_INPUT_CLASS}
          aria-label={label("common.status")}
          value={draft.status}
          onChange={(event) =>
            onChange({ ...draft, status: event.target.value as MilestoneStatus })
          }
        >
          {MILESTONE_STATUSES.map((status) => (
            <option key={status} value={status}>
              {milestoneStatusLabel(status)}
            </option>
          ))}
        </select>
        <label className="flex min-w-40 flex-1 flex-col gap-1 text-xs text-muted">
          {label("roadmap.targetDate")}
          <input
            className={FIELD_INPUT_CLASS}
            type="date"
            value={draft.targetDate}
            onChange={(event) => onChange({ ...draft, targetDate: event.target.value })}
          />
        </label>
      </div>
      {error ? <p className={FIELD_ERROR_CLASS}>{error}</p> : null}
      <div className="flex gap-2">
        <button
          className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-accent-fg disabled:opacity-60"
          type="submit"
          disabled={pending}
        >
          {submitLabel}
        </button>
        <button
          className="rounded-md border border-border px-3 py-1.5 text-sm"
          type="button"
          onClick={onCancel}
        >
          {label("common.cancel")}
        </button>
      </div>
    </form>
  );
}
