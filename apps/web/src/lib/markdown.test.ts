import { describe, expect, it } from "vitest";

import { parseInlineMarkdown, parseMarkdownBlocks } from "./markdown";

describe("parseMarkdownBlocks", () => {
  it("splits headings, lists, code, and paragraphs", () => {
    const nodes = parseMarkdownBlocks(
      [
        "# Title",
        "",
        "A **bold** line.",
        "",
        "- one",
        "- two",
        "",
        "```",
        "code",
        "```",
        "",
        "> quote",
      ].join("\n"),
    );
    expect(nodes).toEqual([
      { type: "heading", level: 1, text: "Title" },
      { type: "paragraph", text: "A **bold** line." },
      { type: "list", ordered: false, items: ["one", "two"] },
      { type: "code", text: "code" },
      { type: "quote", text: "quote" },
    ]);
  });

  it("parses inline code, bold, and italics", () => {
    expect(parseInlineMarkdown("See `GET /v1` and **Board** plus *ready*.")).toEqual([
      { type: "text", text: "See " },
      { type: "code", text: "GET /v1" },
      { type: "text", text: " and " },
      { type: "strong", text: "Board" },
      { type: "text", text: " plus " },
      { type: "em", text: "ready" },
      { type: "text", text: "." },
    ]);
  });
});
