export type DependencyEdge = {
  fromTaskId: string;
  toTaskId: string;
};

export function wouldCreateCycle(
  edges: readonly DependencyEdge[],
  fromTaskId: string,
  toTaskId: string,
): boolean {
  if (fromTaskId === toTaskId) {
    return true;
  }

  const outgoing = new Map<string, string[]>();
  for (const edge of edges) {
    const next = outgoing.get(edge.fromTaskId);
    if (next) {
      next.push(edge.toTaskId);
    } else {
      outgoing.set(edge.fromTaskId, [edge.toTaskId]);
    }
  }

  const stack = [toTaskId];
  const seen = new Set<string>();
  while (stack.length > 0) {
    const node = stack.pop()!;
    if (node === fromTaskId) {
      return true;
    }
    if (seen.has(node)) {
      continue;
    }
    seen.add(node);
    const next = outgoing.get(node);
    if (next) {
      for (const child of next) {
        stack.push(child);
      }
    }
  }
  return false;
}
