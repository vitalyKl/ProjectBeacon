import { describe, expect, it } from "vitest";

import { redactGitText } from "./clone.js";

describe("redactGitText", () => {
  it("strips installation tokens from git stderr", () => {
    expect(
      redactGitText(
        "fatal: unable to access 'https://x-access-token:ghs_secret@github.com/acme/demo.git/': 401",
      ),
    ).toBe("fatal: unable to access 'https://x-access-token:***@github.com/acme/demo.git/': 401");
    expect(redactGitText("Authorization: Bearer ghs_secret")).toBe("Authorization: Bearer ***");
    expect(redactGitText("token ghs_secret leftover")).toBe("token ghs_*** leftover");
  });
});
