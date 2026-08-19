import { describe, expect, it } from "vitest";

import { isInteractiveIo, type PromptIo } from "./prompt.js";

describe("setup prompt", () => {
  it("treats a TTY stdin as interactive", () => {
    const stdout: PromptIo["stdout"] = { write() {} };
    expect(
      isInteractiveIo({
        stdin: { isTTY: true } as PromptIo["stdin"],
        stdout,
      }),
    ).toBe(true);
    expect(
      isInteractiveIo({
        stdin: { isTTY: false } as PromptIo["stdin"],
        stdout,
      }),
    ).toBe(false);
  });
});
