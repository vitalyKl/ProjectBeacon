import type { DecisionSummary, ConstraintView } from "@beacon/api-spec";

export type LintWarning = {
  code: "unlinked_supersede" | "milestone_orphaned_tasks" | "constraint_contradiction";
  title: string;
  detail: string;
};

/** A decision A has superseded_by: B means "A is superseded by B".
 * This is valid only when B is an accepted decision.
 * If B is not found in the decision list, the chain is broken.
 */
export function lintSupersedeLinks(decisions: DecisionSummary[]): LintWarning[] {
  const warnings: LintWarning[] = [];
  const idSet = new Set(decisions.map((d) => d.id));

  for (const decision of decisions) {
    if (decision.status !== "superseded" || !decision.superseded_by) {
      continue;
    }
    if (!idSet.has(decision.superseded_by)) {
      warnings.push({
        code: "unlinked_supersede",
        title: `Decision "${decision.title}" references a missing superseder`,
        detail: `This decision is marked as superseded by ${decision.superseded_by}, but that decision does not exist in the board.`,
      });
    }
  }

  return warnings;
}

/** A milestone is "orphaned" if it is closed but still has active tasks
 * referencing it, suggesting the milestone was closed too early or
 * tasks were not migrated.
 */
export function lintMilestoneOrphans(
  milestoneId: string | null,
  tasks: { id: string; milestoneId: string | null; status: string; deletedAt: string | null }[],
): LintWarning[] {
  const warnings: LintWarning[] = [];
  if (!milestoneId) {
    return warnings;
  }

  const activeOrphanedTasks = tasks.filter(
    (task) =>
      task.milestoneId === milestoneId &&
      task.deletedAt === null &&
      (task.status === "done" || task.status === "canceled"),
  );

  if (activeOrphanedTasks.length > 0) {
    warnings.push({
      code: "milestone_orphaned_tasks",
      title: `Milestone has ${activeOrphanedTasks.length} completed task(s) still linked`,
      detail: `${activeOrphanedTasks.length} task(s) are still bound to this milestone even though they are done or canceled. Consider clearing the milestone reference or reassigning them.`,
    });
  }

  return warnings;
}

/** Two active constraints with the same scope_path but opposing body content.
 * We detect contradictions by checking if one constraint's body contains
 * a negation of the other's intent (e.g., "must use X" vs "must not use X").
 */
export function lintContradictingConstraints(constraints: ConstraintView[]): LintWarning[] {
  const warnings: LintWarning[] = [];
  const byScope = new Map<string, ConstraintView[]>();

  for (const c of constraints) {
    const scope = c.scope_path || "";
    const list = byScope.get(scope) ?? [];
    list.push(c);
    byScope.set(scope, list);
  }

  const seen = new Set<string>();

  for (const [, group] of byScope) {
    if (group.length < 2) {
      continue;
    }
    for (let i = 0; i < group.length; i += 1) {
      for (let j = i + 1; j < group.length; j += 1) {
        const left = group[i];
        const right = group[j];
        if (!left || !right) {
          continue;
        }

        // Check for direct contradiction: one starts with "must use" and the other with "must not use"
        // or similar patterns that indicate opposing constraints.
        const isContradiction = detectContradiction(left.body, right.body);

        if (isContradiction) {
          const pairKey = [left.id, right.id].sort().join(":");
          if (!seen.has(pairKey)) {
            seen.add(pairKey);
            warnings.push({
              code: "constraint_contradiction",
              title: "Contradicting constraints in the same scope",
              detail: `These two active constraints conflict: "${truncate(left.body, 80)}" vs "${truncate(right.body, 80)}"`,
            });
          }
        }
      }
    }
  }

  return warnings;
}

function detectContradiction(left: string, right: string): boolean {
  const lowerLeft = left.toLowerCase().trim();
  const lowerRight = right.toLowerCase().trim();

  // Check for "must not" vs "must" patterns
  const mustNotLeft = /\bmust\s+not\b/.test(lowerLeft);
  const mustLeft = /\bmust\b/.test(lowerLeft) && !mustNotLeft;
  const mustNotRight = /\bmust\s+not\b/.test(lowerRight);
  const mustRight = /\bmust\b/.test(lowerRight) && !mustNotRight;

  if ((mustNotLeft && mustRight) || (mustNotRight && mustLeft)) {
    // Check if they refer to the same subject by looking for common keywords
    const leftWords = extractKeywords(lowerLeft);
    const rightWords = extractKeywords(lowerRight);
    if (hasOverlap(leftWords, rightWords)) {
      return true;
    }
  }

  // Check for "should not" vs "should" patterns
  const shouldNotLeft = /\bshould\s+not\b/.test(lowerLeft);
  const shouldLeft = /\bshould\b/.test(lowerLeft) && !shouldNotLeft;
  const shouldNotRight = /\bshould\s+not\b/.test(lowerRight);
  const shouldRight = /\bshould\b/.test(lowerRight) && !shouldNotRight;

  if ((shouldNotLeft && shouldRight) || (shouldNotRight && shouldLeft)) {
    const leftWords = extractKeywords(lowerLeft);
    const rightWords = extractKeywords(lowerRight);
    if (hasOverlap(leftWords, rightWords)) {
      return true;
    }
  }

  return false;
}

function extractKeywords(text: string): string[] {
  // Extract significant words (nouns/identifiers), filtering out common words
  const stopWords = new Set([
    "must",
    "should",
    "not",
    "do",
    "don't",
    "does",
    "doesn't",
    "has",
    "have",
    "had",
    "be",
    "been",
    "being",
    "is",
    "are",
    "was",
    "were",
    "in",
    "on",
    "at",
    "to",
    "for",
    "with",
    "a",
    "an",
    "the",
    "and",
    "or",
    "but",
    "if",
    "of",
    "from",
    "by",
    "this",
    "that",
    "it",
    "as",
    "we",
    "our",
    "their",
    "use",
    "uses",
    "using",
  ]);
  return text
    .split(/[\s,;.]+/)
    .filter((w) => w.length > 2 && !stopWords.has(w))
    .map((w) => w.toLowerCase());
}

function hasOverlap(wordsA: string[], wordsB: string[]): boolean {
  const setA = new Set(wordsA);
  return wordsB.some((w) => setA.has(w));
}

function truncate(text: string, max: number): string {
  return text.length <= max ? text : text.slice(0, max) + "…";
}
