import { parseInlineMarkdown, parseMarkdownBlocks } from "@/lib/markdown";

function Inline({ text }: { text: string }) {
  return (
    <>
      {parseInlineMarkdown(text).map((part, index) => {
        if (part.type === "code") {
          return (
            <code key={index} className="rounded bg-background px-1 py-0.5 font-mono text-[0.9em]">
              {part.text}
            </code>
          );
        }
        if (part.type === "strong") {
          return <strong key={index}>{part.text}</strong>;
        }
        if (part.type === "em") {
          return <em key={index}>{part.text}</em>;
        }
        return <span key={index}>{part.text}</span>;
      })}
    </>
  );
}

export function MarkdownView({
  source,
  className = "space-y-2 text-sm leading-6",
}: {
  source: string;
  className?: string;
}) {
  const nodes = parseMarkdownBlocks(source);
  if (nodes.length === 0) {
    return null;
  }
  return (
    <div className={className}>
      {nodes.map((node, index) => {
        if (node.type === "heading") {
          const Tag = node.level === 1 ? "h3" : node.level === 2 ? "h4" : "h5";
          return (
            <Tag key={index} className="font-semibold tracking-tight">
              <Inline text={node.text} />
            </Tag>
          );
        }
        if (node.type === "list") {
          const ListTag = node.ordered ? "ol" : "ul";
          return (
            <ListTag
              key={index}
              className={node.ordered ? "list-decimal space-y-1 pl-5" : "list-disc space-y-1 pl-5"}
            >
              {node.items.map((item, itemIndex) => (
                <li key={itemIndex}>
                  <Inline text={item} />
                </li>
              ))}
            </ListTag>
          );
        }
        if (node.type === "code") {
          return (
            <pre
              key={index}
              className="overflow-x-auto rounded-md border border-border bg-background px-3 py-2 font-mono text-xs"
            >
              {node.text}
            </pre>
          );
        }
        if (node.type === "quote") {
          return (
            <blockquote key={index} className="border-l-2 border-border pl-3 text-muted">
              <Inline text={node.text} />
            </blockquote>
          );
        }
        return (
          <p key={index}>
            <Inline text={node.text} />
          </p>
        );
      })}
    </div>
  );
}
