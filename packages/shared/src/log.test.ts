import { describe, expect, it, vi } from "vitest";

import { redactLogValue, serializeLog, writeLog } from "./log.js";

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

  it("writes info lines to stderr by default", () => {
    const stderr = vi.spyOn(process.stderr, "write").mockImplementation(() => true);
    const stdout = vi.spyOn(process.stdout, "write").mockImplementation(() => true);
    writeLog({ level: "info", msg: "listening" });
    expect(stderr).toHaveBeenCalledOnce();
    expect(String(stderr.mock.calls[0]?.[0])).toContain('"msg":"listening"');
    expect(stdout).not.toHaveBeenCalled();
    stderr.mockRestore();
    stdout.mockRestore();
  });
});
