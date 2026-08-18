import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { BEACON_AGENTS_MD_INPUT, renderBeaconAgentsMd } from "./beacon-brief.js";
import { exportAgentsMd } from "./export.js";

const agentsMdPath = join(dirname(fileURLToPath(import.meta.url)), "../../..", "AGENTS.md");

describe("checked-in AGENTS.md", () => {
  it("matches exportAgentsMd for Beacon's project brief", () => {
    const checkedIn = readFileSync(agentsMdPath, "utf8").replaceAll("\r\n", "\n");
    const rendered = renderBeaconAgentsMd();
    expect(rendered).toBe(exportAgentsMd(BEACON_AGENTS_MD_INPUT));
    expect(checkedIn).toBe(rendered);
    expect(rendered.startsWith("---\nmanaged-by: projectbeacon\n")).toBe(true);
    expect(rendered).toContain("revision: uncompiled");
    expect(rendered).toContain("scope: project");
    expect(rendered.endsWith("\n")).toBe(true);
  });
});
