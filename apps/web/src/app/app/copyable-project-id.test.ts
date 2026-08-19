import { describe, expect, it } from "vitest";

import { copyProjectId } from "./copyable-project-id";

describe("copyProjectId", () => {
  it("writes the project id and reports success", async () => {
    let written = "";
    const ok = await copyProjectId("01a013d6-6711-77e6-8a26-61e291b24143", {
      async writeText(value) {
        written = value;
      },
    });
    expect(ok).toBe(true);
    expect(written).toBe("01a013d6-6711-77e6-8a26-61e291b24143");
  });

  it("returns false when the clipboard is missing or rejects", async () => {
    expect(await copyProjectId("proj", undefined)).toBe(false);
    expect(
      await copyProjectId("proj", {
        async writeText() {
          throw new Error("denied");
        },
      }),
    ).toBe(false);
  });
});
