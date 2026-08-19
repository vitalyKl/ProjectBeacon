export type MarkdownNode =
  | { type: "heading"; level: 1 | 2 | 3; text: string }
  | { type: "paragraph"; text: string }
  | { type: "list"; ordered: boolean; items: string[] }
  | { type: "code"; text: string }
  | { type: "quote"; text: string };

export function parseMarkdownBlocks(source: string): MarkdownNode[] {
  const lines = source.replaceAll("\r\n", "\n").split("\n");
  const nodes: MarkdownNode[] = [];
  let index = 0;

  while (index < lines.length) {
    const line = lines[index] ?? "";
    if (line.trim().length === 0) {
      index += 1;
      continue;
    }
    if (line.startsWith("```")) {
      const body: string[] = [];
      index += 1;
      while (index < lines.length && !(lines[index] ?? "").startsWith("```")) {
        body.push(lines[index] ?? "");
        index += 1;
      }
      if (index < lines.length) {
        index += 1;
      }
      nodes.push({ type: "code", text: body.join("\n") });
      continue;
    }
    const heading = /^(#{1,3})\s+(.+)$/.exec(line);
    if (heading) {
      nodes.push({
        type: "heading",
        level: heading[1]!.length as 1 | 2 | 3,
        text: heading[2]!.trim(),
      });
      index += 1;
      continue;
    }
    if (/^\s*[-*]\s+/.test(line) || /^\s*\d+\.\s+/.test(line)) {
      const ordered = /^\s*\d+\.\s+/.test(line);
      const items: string[] = [];
      while (index < lines.length) {
        const item = lines[index] ?? "";
        const match = ordered ? /^\s*\d+\.\s+(.+)$/.exec(item) : /^\s*[-*]\s+(.+)$/.exec(item);
        if (!match) {
          break;
        }
        items.push(match[1]!.trim());
        index += 1;
      }
      nodes.push({ type: "list", ordered, items });
      continue;
    }
    if (/^\s*>\s?/.test(line)) {
      const body: string[] = [];
      while (index < lines.length && /^\s*>\s?/.test(lines[index] ?? "")) {
        body.push((lines[index] ?? "").replace(/^\s*>\s?/, ""));
        index += 1;
      }
      nodes.push({ type: "quote", text: body.join("\n") });
      continue;
    }
    const body: string[] = [line];
    index += 1;
    while (index < lines.length) {
      const next = lines[index] ?? "";
      if (
        next.trim().length === 0 ||
        next.startsWith("```") ||
        /^#{1,3}\s+/.test(next) ||
        /^\s*[-*]\s+/.test(next) ||
        /^\s*\d+\.\s+/.test(next) ||
        /^\s*>\s?/.test(next)
      ) {
        break;
      }
      body.push(next);
      index += 1;
    }
    nodes.push({ type: "paragraph", text: body.join("\n") });
  }
  return nodes;
}

export type InlinePart =
  | { type: "text"; text: string }
  | { type: "code"; text: string }
  | { type: "strong"; text: string }
  | { type: "em"; text: string };

export function parseInlineMarkdown(source: string): InlinePart[] {
  const parts: InlinePart[] = [];
  const pattern = /(`[^`]+`|\*\*[^*]+\*\*|\*[^*]+\*)/g;
  let last = 0;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(source))) {
    if (match.index > last) {
      parts.push({ type: "text", text: source.slice(last, match.index) });
    }
    const token = match[0];
    if (token.startsWith("`")) {
      parts.push({ type: "code", text: token.slice(1, -1) });
    } else if (token.startsWith("**")) {
      parts.push({ type: "strong", text: token.slice(2, -2) });
    } else {
      parts.push({ type: "em", text: token.slice(1, -1) });
    }
    last = match.index + token.length;
  }
  if (last < source.length) {
    parts.push({ type: "text", text: source.slice(last) });
  }
  return parts;
}
