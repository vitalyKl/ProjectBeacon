import { describe, expect, it } from "vitest";

import { githubIssuesMode, isGithubTwoWayEnabled, mergeProjectSettings } from "./settings.js";

describe("github settings", () => {
  it("defaults issue sync to off", () => {
    expect(githubIssuesMode({})).toBe("off");
    expect(githubIssuesMode({ github: { issues: "two_way" } })).toBe("off");
    expect(githubIssuesMode({ github: { issues: "import" } })).toBe("import");
  });

  it("reads the two-way flag at request time", () => {
    expect(isGithubTwoWayEnabled({})).toBe(false);
    expect(isGithubTwoWayEnabled({ FF_GITHUB_TWO_WAY: "true" })).toBe(true);
    expect(isGithubTwoWayEnabled({ FF_GITHUB_TWO_WAY: "false" })).toBe(false);
  });

  it("merges nested github settings", () => {
    expect(
      mergeProjectSettings({ github: { issues: "off" } }, { github: { issues: "import" } }),
    ).toEqual({ github: { issues: "import" } });
  });
});
