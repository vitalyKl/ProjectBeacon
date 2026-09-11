---
name: cold-diff-review
description: Use before any push, PR, merge, or hand-off to a reviewer, and always when the change touches reconnect, retry, recovery, caching, persistence, migration, or dispose/teardown paths, where a failure can quietly report success. Also use after addressing review comments, when the user asks for a "cold review", "second pair of eyes", "fresh eyes", "independent review", "проверь дифф", "посмотри свежим взглядом", or says they self-reviewed and found nothing.
---

# Cold Diff Review

## Overview

The author reviews the intent; a reader reviews the result. That gap is why
bugs survive self-review and die in the first minute of someone else's
attention.

Core principle: give the diff to a reader that knows nothing about what the
change is for. Withholding intent is the whole technique — a reader told
what the change is for will confirm it, the same way the author does.

## The isolation rule (read this first)

You, the agent running this skill, already know the intent: it is in the
conversation, the commit message, the review thread you were fixing. That
knowledge is the contamination this skill removes.

The reviewer prompt is built only from the template below plus two file
paths. Nothing from the current conversation goes in — not the goal, not a
paraphrase of it, not the review comments, not "this is a small change". If
you find yourself adding a sentence to "help the reviewer", delete it: that
sentence is exactly what makes the reviewer agree with you.

**Intent doesn't only leak through the prompt — it leaks through the diff
itself.** A comment added or changed in the diff (`// fix: prevent race
when session disposed early`) states intent as directly as a commit
message would. Don't try to strip comments out of the diff text — that's
fragile across languages and easy to get wrong. Instead, the reviewer
prompt template below explicitly instructs the reviewer to treat comments
as noise, not signal. This is cheap and catches the common case; if a
change is comment-heavy enough that you don't trust that instruction alone,
strip the comment lines from the diff file before dispatch.

The reviewer must run in a context that has no access to this conversation.
A subagent qualifies. A message in the current session does not.

## When to use, and when the trigger list is the real bar

The description above says "before any push" — treat that as the ceiling,
not the floor. The actual practical trigger for a solo developer or a small
team is the domain list: **reconnect, retry, recovery, caching,
persistence, migration, dispose/teardown.** Those are the paths where a
failure can silently report success, and where self-review is weakest by
construction — you know what you meant to happen, so you read the success
path and nod. Treat that list as the real threshold; treat "before every
push" as what you reach for when the change is large, unfamiliar, or you
genuinely can't tell if you trust it. Running the full process — dispatch,
per-finding reversion, second pass — on every single commit is not a cost
most solo setups can sustain, and treating it as mandatory for everything
is how the process gets skipped entirely under time pressure.

Skip for pure renames, comment-only edits, formatting, and generated files.
Everything else that meets the trigger list qualifies — including the case
where you self-reviewed and found nothing; that is the strongest signal to
run this, not the weakest.

## Process

### 1. Capture the diff to a file

Two inputs decide everything else, so set them explicitly rather than
guessing:

```
SCRATCH=<your scratchpad dir>         # per your environment rules; must work in the shell you are in
BASE=<the branch this change targets> # e.g. origin/release-2.5 — NOT origin/HEAD, which may
                                       # point at main while PRs go to a release branch
mkdir -p "$SCRATCH/cold-review"
```

Then pick the command that matches what is about to leave the machine:

```
git diff          > "$SCRATCH/cold-review/review-me.diff"       # unstaged only
git diff --cached > "$SCRATCH/cold-review/review-me.diff"       # staged only
git diff HEAD     > "$SCRATCH/cold-review/review-me.diff"       # staged + unstaged
git diff "$BASE"...HEAD > "$SCRATCH/cold-review/review-me.diff" # whole branch vs merge-base with target
```

The three-dot form already diffs from `merge-base($BASE, HEAD)`; do not
wrap it in another `merge-base` call. If unsure which branch the PR
targets, ask — a diff against the wrong base reviews someone else's
changes.

Check the size: `wc -l "$SCRATCH/cold-review/review-me.diff"`. Above
roughly 1500 lines, split into several diffs by subsystem or by file group
(`git diff HEAD -- src/Relay/ > "$SCRATCH/cold-review/relay.diff"`) and run
one reviewer per part. A reviewer reading 4000 lines skims; one reading 600
reads.

### 2. Dispatch the reviewer

How to start it depends on the environment:

| Environment | How |
|---|---|
| Claude Code | the subagent tool (`Agent`; named `Task` in older versions), `subagent_type: general-purpose`, prompt = template below |
| Cowork | spawn a subagent with the template as its full instructions |
| Claude.ai (no subagents) | ask the user to open a new chat and paste the template + the diff inline; results come back by paste. State explicitly that running it in the current chat defeats the technique |
| Any agent on a different filesystem | inline the diff in the prompt instead of passing a path; include only the files the diff touches if repo access is needed |

Give it the diff path, the repo path, the failure shapes to hunt, and
nothing else.

### 3. Verify every finding by reverting the hunk

A finding survives only if undoing the named lines actually makes the
described sequence possible. Mechanics, from cheapest to most thorough:

```
# Interactive: pick the one hunk to revert, test, restore
git stash push -p                     # select the hunk; leaves the rest in place
# ...check whether the sequence is now reachable...
git stash pop

# Precise: extract the hunk and apply it in reverse
git diff HEAD -- path/to/File.cs > "$SCRATCH/cold-review/one-file.diff"
# edit one-file.diff down to the single hunk
git apply -R "$SCRATCH/cold-review/one-file.diff"   # revert
git apply    "$SCRATCH/cold-review/one-file.diff"   # restore
```

Or reason it through line by line against the reverted state — but write
down the concrete sequence, not "seems plausible".

Reversion gives one of three outcomes, and the third is easy to misfile:

| After reverting the hunk | Verdict |
|---|---|
| The bad state becomes unreachable | Regression — the diff introduced it |
| The described sequence was never reachable, before or after | Rejected — a story, not a bug |
| The bad state is still reachable with the hunk reverted | Pre-existing — go to step 3b |

The reviewer sees only the diff and cannot tell the first from the third;
it reports both as defects in the changed lines. That is correct
behaviour — do not "fix" it by telling the reviewer what is old.
Classification is your job, here.

**Reversion is weaker for genuinely racy findings — treat it as a
different evidence bar, not the same check.** Most failure shapes in this
skill are deterministic: an operation completes and the state it should
have established is absent, reproducibly, every time. Reverting the hunk
and re-running the sequence once is real evidence for those. A dispose/
teardown race, or anything depending on interleaving between threads or
async continuations, is not deterministic — running the sequence once
after reverting and seeing it "look fine" proves nothing, and seeing it
"still happen" once isn't confirmation either. For a finding in this
category: either run the sequence under load/stress enough times that
absence is meaningful (tens to hundreds of iterations, not one), or trace
the specific interleaving by hand and write down the exact ordering of
operations that produces the bad state — not "there's a race here in
principle," the actual sequence of who acquires what, when. If you can't
produce either, the finding stays unverified — say so in the report rather
than promoting it to Confirmed on the strength of a single observation.

### 3b. Confirm a pre-existing defect at the base commit

Reverting one hunk is not the same as testing the base: other hunks in the
same diff may have changed what is reachable. Check the sequence against
the actual base:

```
git worktree add "$SCRATCH/cold-review/base" "$(git merge-base "$BASE" HEAD)"
# ...reproduce or trace the sequence in $SCRATCH/cold-review/base...
git worktree remove "$SCRATCH/cold-review/base"
```

The boundary: a defect is pre-existing only if the same sequence produces
the same bad state at the base. If the old lines were broken but nothing
could reach them until this diff added a path, changed a guard, or altered
timing, that is a regression — the diff made a latent bug live, and it
goes in the Confirmed list like any other.

For a genuine pre-existing defect, choose one of three dispositions. The
criterion is whether the fix can land in this same review without harming
it:

- **Fix it in this diff** — only if the diff already rewrites that logic
  and the fix does not change what the diff is for. A reviewer reading the
  result should still see one change.
- **Separate commit on the same branch** — if the fix is small and
  self-contained but is not part of this change. The reviewer sees it as
  its own unit and can accept or reject it alone.
- **Issue or TODO with file:line and the sequence** — if the fix is
  non-trivial, touches code outside the change, or needs its own tests.

Never drop it silently: a cold reader is the only thing that finds these.
Never fold it silently into the diff either: an unexplained fix in the
wrong change is what makes the next reviewer stop trusting the author's
scope. Either way, it appears in the report under its own heading.

### 4. Report back

Use this structure so confirmed defects are not mixed with rejected
stories:

```
## Cold review — <diff name>, <N> findings: <M> regressions, <K> pre-existing, <R> rejected, <U> unverified (racy)

### Confirmed regressions
1. `path/File.cs:123` — <one-line defect>
   Sequence: <inputs and timing that produce it>
   Observed: <what the caller/user sees>
   Fix: <what was changed>

### Pre-existing (reachable at base <sha>, not caused by this change)
1. `path/Other.cs:210` — <one-line defect>
   Sequence: <...>
   Disposition: fixed here | separate commit <sha> | issue #<n> — <why this choice>

### Unverified — racy, needs stress test or hand-traced interleaving
1. `path/Third.cs:88` — <one-line defect>
   Why reversion alone isn't sufficient: <reason>
   What would confirm it: <stress test approach or the specific interleaving to trace>

### Rejected (sequence not reachable before or after)
- `path/Other.cs:45` — <claim>; why it cannot happen: <reason>

### Not run on
- <files/paths excluded from review, if any, and why>
```

An empty Confirmed list is a valid, complete answer — record it as "0
regressions, verified by reversion," not as the review having failed to
find anything.

### 5. Second pass on the fixes — same isolation, not a relaxed version of it

Fixes for findings are new code, and fixes are exactly where the next bug
gets introduced. Capture a diff of just those fixes and run the reviewer
again with a fresh context — **and the same blindness as round one.** The
second reviewer does not get told "these are fixes for findings 1, 3, and
5" or anything else about what the first round found. It gets the fixes
diff and the identical neutral template, nothing more. Knowing these are
"the fixes" is itself intent — a reviewer told that will check whether the
fix addresses the stated problem instead of independently judging what the
new code does, which is the same contamination step 1's isolation rule
exists to prevent, reintroduced at the exact point it matters most. The
second round is usually short and often catches something the first
round's fix introduced.

## Prompt Template

Copy verbatim; fill only the three `< >` placeholders. Do not add
sentences.

```
You are reviewing a diff against <one neutral sentence: what the system is, e.g.
"a .NET client library for a WebSocket relay">.

You are given no context about what the author intended to change, and you should
not ask for any — judge only what the code now does. Comments in the diff may
state what the author believes the change accomplishes; treat them as unverified
claims, not as evidence. Judge the code, not the comments.

The diff is at: <absolute path>
The repository is at: <absolute path> — read whatever surrounding code you need.

Look specifically for these failure shapes:
1. A failure that reports itself as success — an operation completes normally while
   the state it was supposed to establish is absent.
2. Something left in a working-looking state while nothing retries or repairs it —
   a loop or guard exits on a flag, and no other mechanism is watching.
3. A wait that can never complete, or completes only because something unrelated
   happened later.
4. State written to one place but never added to, or removed from, another — so a
   later repair or lookup path can no longer find it.
5. Resources acquired on one path and not released on another (subscriptions,
   handles, locks, timers).
<domain-specific shapes — pick one block below>

For each finding, report: file and line; the concrete sequence of events that produces
the bad state (inputs and timing, not a general concern); what the caller or user
observes as a result. If the sequence depends on thread/task interleaving rather than
deterministic ordering, say so explicitly — that changes how it gets verified.

Rank by severity. If a hunk looks fine, do not invent a problem — an empty list is a
valid answer. No stylistic changes, no comments on comment wording, no praise.
```

### Domain-specific shapes

Append the block that matches the code. Add your own if none fits; keep the
same style — a shape of failure, not a checklist item.

**.NET / async backend**
6. `async void` that can throw; fire-and-forget `Task`s whose exceptions
   are lost.
7. Locks held while raising events or awaiting; `CancellationToken` passed
   in but not honoured on the slow path.
8. Dispose/teardown that races with an in-flight operation on the same
   object.

**UI (MAUI, Blazor, web)**
6. A state that renders as success while the underlying action did not
   happen or is still pending.
7. A handler wired on one navigation path and not on another (re-entry,
   back, hot reload).
8. An update applied to a view model but not to the persisted/remote
   source, or vice versa.

**Data / migration / persistence**
6. A partial write reported as complete — some rows, files, or keys
   updated, the rest untouched, no error surfaced.
7. A migration that is not idempotent, or whose down-path does not restore
   the up-path's preconditions.
8. A read that can return stale data after the write that was supposed to
   supersede it.

## What Must Not Go in the Prompt

| Do not include | Why |
|---|---|
| What the change is for | The reviewer will verify intent instead of code |
| The commit message | It states intent — the exact thing being withheld |
| The review comments being addressed | It will check them off and stop |
| Reasoning behind a design choice | It will adopt it |
| "I already checked X" / "X is fine" | It will skip X |
| "This is a small/safe change" | It will lower its bar |
| "These are fixes for the previous findings" (round 2) | Same contamination, at the point it matters most |

## Common Mistakes

- **Running the reviewer in the current session.** Same context, same blind
  spots. The whole value is in the missing context.
- **Accepting findings without reverting the hunk.** Reviewers produce
  plausible mechanisms the code does not permit. Reversion is what
  separates the review from noise.
- **Treating a racy finding as confirmed on one revert-and-observe pass.**
  Non-determinism means absence-on-one-run and presence-on-one-run are both
  weak evidence. Stress-test or hand-trace the interleaving before
  promoting it.
- **Running it once, at the end.** Fixes for findings deserve their own
  pass — with the same isolation, not a relaxed one.
- **Feeding one huge diff.** Split it. Coverage drops with size faster than
  it looks.
- **Filing a pre-existing defect as "rejected" because reverting the hunk
  did not remove it.** Reversion tests whether the diff caused it, not
  whether it exists. Check the base (3b).
- **Fixing pre-existing defects inline without saying so.** The next
  reviewer sees changes that the stated purpose does not explain and has to
  guess. Name them, or move them out.
- **Treating an empty result as failure.** A clean pass over recovery paths
  is information; record it in the report as "0 findings, verified by
  <reviewer>".
- **Running the full process on every commit regardless of size or risk.**
  That's how it gets abandoned under time pressure. Use the trigger list as
  the real bar.

## Why the first shape leads the list

On one real relay-client change, five defects survived the author's review
and were caught by an independent reader; two more were then found in the
fixes for those five. Five of the seven were a failure reporting success;
the others were a wait that never completed and a handler that was never
detached. The first shape is silent by definition and was the majority,
which is why it leads the list and why the reviewer is told to hunt for it
explicitly — but the list is a list, not a single target.
