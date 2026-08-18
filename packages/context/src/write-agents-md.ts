import { writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { renderBeaconAgentsMd } from "./beacon-brief.js";

const dest = join(dirname(fileURLToPath(import.meta.url)), "../../..", "AGENTS.md");
writeFileSync(dest, renderBeaconAgentsMd());
