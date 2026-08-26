import { describe, expect, it } from "vitest";

import {
  githubIssuesMode,
  isGithubTwoWayEnabled,
  mergeProjectSettings,
  parseProjectSettingsPatch,
  parseWebhookSettings,
} from "./settings.js";

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

  it("accepts import and rejects two-way unless the request-time flag is on", () => {
    expect(parseProjectSettingsPatch({ github: { issues: "import" } })).toEqual({
      ok: true,
      patch: { github: { issues: "import" } },
    });
    expect(parseProjectSettingsPatch({ github: { issues: "two_way" } }, {})).toMatchObject({
      ok: false,
      reason: "github_two_way_off",
    });
    expect(
      parseProjectSettingsPatch({ github: { issues: "two_way" } }, { FF_GITHUB_TWO_WAY: "true" }),
    ).toMatchObject({
      ok: false,
      reason: "github_two_way_unavailable",
    });
  });

  it("parses webhook urls from the urls key", () => {
    expect(parseWebhookSettings(undefined)).toEqual({ urls: [], ok: true });
    expect(parseWebhookSettings(null)).toEqual({ urls: [], ok: true });
    expect(parseWebhookSettings({ urls: ["https://hooks.slack.com/xyz"] })).toEqual({
      urls: ["https://hooks.slack.com/xyz"],
      ok: true,
    });
    expect(
      parseWebhookSettings({
        urls: ["https://hooks.slack.com/abc", "https://example.com/hook"],
      }),
    ).toEqual({
      urls: ["https://hooks.slack.com/abc", "https://example.com/hook"],
      ok: true,
    });
    expect(parseWebhookSettings({ urls: [] })).toEqual({ urls: [], ok: true });
  });

  it("rejects invalid webhook urls and malformed input", () => {
    expect(parseWebhookSettings({ urls: ["not-a-url"] })).toEqual({ urls: [], ok: true });
    expect(parseWebhookSettings({ urls: ["http://localhost/hook"] })).toEqual({
      urls: [],
      ok: true,
    });
    expect(parseWebhookSettings({ webhooks: ["https://x.com"] })).toMatchObject({
      ok: false,
      message: "webhooks.urls must be an array",
    });
    expect(parseWebhookSettings([])).toMatchObject({
      ok: false,
      message: "webhooks must be an object",
    });
    expect(parseWebhookSettings("https://x.com")).toMatchObject({
      ok: false,
      message: "webhooks must be an object",
    });
  });
});
