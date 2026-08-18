import { describe, expect, it } from "vitest";

import { redactLogValue, serializeLog } from "./log.js";

describe("log redaction", () => {
  it("redacts Authorization and file bodies", () => {
    const line = serializeLog({
      msg: "request",
      authorization: "Bearer secret",
      content: "file body",
      brief_markdown: "# secret brief",
      project_id: "proj",
    });
    expect(line).toContain('"authorization":"[redacted]"');
    expect(line).toContain('"content":"[redacted]"');
    expect(line).toContain('"brief_markdown":"[redacted]"');
    expect(line).toContain('"project_id":"proj"');
    expect(line).not.toContain("Bearer secret");
    expect(line).not.toContain("file body");
  });

  it("redacts nested authorization headers", () => {
    expect(redactLogValue("headers", { Authorization: "Bearer x", accept: "json" })).toEqual({
      Authorization: "[redacted]",
      accept: "json",
    });
  });
});
